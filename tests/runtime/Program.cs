using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ClientPlugin;
using Sandbox;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Shared.Config;
using Shared.Concealment;
using Shared.Logging;
using VRageMath;

static class Program
{
    private static int checks;
    private static readonly TestLogger Log = new TestLogger();
    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }
    private static MySession World(string path) => new MySession { Ready = true, CurrentPath = path };
    private static void OtherPrefix() { }
    public static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--cluster")
        {
            var settings = new PluginConfig();
            MySession.Static = World("/configured-cluster");
            using var disabled = new ConcealmentRuntime(settings, Log, "configured-cluster");
            MySession.Loading();
            disabled.Update();
            Check(ConcealmentRuntime.ClusterMode && disabled.Manager == null && disabled.Tick == 0,
                "configured Cluster mode creates no manager before loading or on update");
            Check(ConcealmentCommands.Execute(disabled, "!conceal off", null).Contains("only") && settings.Enabled,
                "configured mode commands do not mutate settings");
            Console.WriteLine($"PASS: {checks} configured mode lifecycle checks");
            return;
        }
        const string owner = "concealment.runtime.tests";
        var other = new Harmony("another.plugin.tests");
        var target = AccessTools.Method(typeof(ConcealmentManager), nameof(ConcealmentManager.PatchTarget));
        other.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(Program), nameof(OtherPrefix))));
        var config = new PluginConfig();
        using (var runtime = new ConcealmentRuntime(config, Log, owner))
        {
            runtime.Update();
            Check(runtime.Manager == null, "main menu must not start the engine");
            MySession.Static = new MySession { CurrentPath = "/world/a" };
            runtime.Update();
            Check(runtime.Manager == null, "loading world must not start the engine");
            MySession.Static.Ready = true;
            Sync.IsServer = false;
            runtime.Update();
            Check(runtime.Manager == null, "joining a remote server must not start the engine");
            Check(ConcealmentCommands.Execute(runtime, "!conceal off", null).Contains("only"), "guest cannot control local simulation");
            Check(config.Enabled, "guest command must leave settings unchanged");
            Sync.IsServer = true;
            runtime.Update();
            var first = runtime.Manager;
            Check(first != null && runtime.Tick == 1 && runtime.IsAuthoritative, "offline host starts the engine");
            Check(runtime.DataDirectory == "/world/a", "client production banks are world-scoped");
            Check(Harmony.GetPatchInfo(target).Owners.Contains(owner), "session installs its own patch");
            runtime.Update();
            Check(ReferenceEquals(first, runtime.Manager) && runtime.Tick == 2, "do not create a manager each frame");
            MySandboxGame.IsPaused = true;
            runtime.Update();
            Check(runtime.Tick == 2 && first.Updates == 2, "paused games do not bank simulation time");
            MySandboxGame.IsPaused = false;

            foreach (var bad in new[] { "NaN", "Infinity", "-Infinity", "1e999", "bad", "1 2" })
            {
                int scans = first.Scans;
                Check(ConcealmentCommands.Execute(runtime, "!conceal run " + bad, null).StartsWith("Usage:"), "reject invalid distance: " + bad);
                Check(first.Scans == scans, "invalid distance must not change the world");
            }
            Check(ConcealmentCommands.Execute(runtime, "!conceal run -1", null).Contains("negative"), "reject negative distance");
            config.ConcealDistance = 1234;
            Check(ConcealmentCommands.Execute(runtime, "!conceal run 0", null) == "2 grids concealed." && first.Distance == 1234, "zero uses configured distance");
            Check(ConcealmentCommands.Execute(runtime, "!conceal reveal", null).Contains("controlling"), "reveal requires caller position");
            Check(ConcealmentCommands.Execute(runtime, "!conceal reveal 40", new Vector3D()) == "1 grids revealed." && first.Distance == 40, "reveal uses caller radius");
            Check(ConcealmentCommands.Execute(runtime, "!conceal off", null).StartsWith("Concealment disabled.") && !config.Enabled, "off reveals and disables");
            Check(ConcealmentCommands.Execute(runtime, "!conceal on", null).StartsWith("Concealment enabled.") && config.Enabled, "on enables and scans");
            Check(!ConcealmentCommands.IsCommand("!concealment off") && ConcealmentCommands.IsCommand("!CONCEAL off"), "recognize only the exact command root");

            MySession.Unload();
            Check(first.Disposed && first.Reveals >= 2 && runtime.Manager == null && runtime.Tick == 0, "unloading reveals, disposes, and resets");
            Check(!Harmony.GetPatchInfo(target).Owners.Contains(owner) && Harmony.GetPatchInfo(target).Owners.Contains(other.Id), "cleanup preserves another plugin's patches");
            MySession.Static = World("/world/friends");
            runtime.Update();
            Check(runtime.Manager != first && runtime.Tick == 1 && runtime.DataDirectory == "/world/friends", "friends host gets a fresh manager and bank path");
            var hosted = runtime.Manager;
            Sync.IsServer = false;
            Check(!runtime.IsAuthoritative && ConcealmentCommands.Execute(runtime, "!conceal off", null).Contains("only"), "authority is checked even before the next update");
            runtime.Update();
            Check(hosted.Disposed && runtime.Manager == null, "losing authority releases the engine");
            MySession.Unload();
            MySession.Static = World("/remote/world");
            runtime.Update();
            Check(runtime.Manager == null, "host-to-client transition must stay passive");
            Sync.IsServer = true;
            MySession.Unload();
            MySession.Static = World("/world/error");
            runtime.Update();
            var failing = runtime.Manager;
            failing.ThrowOnUpdate = true;
            failing.ThrowOnReveal = true;
            failing.ThrowOnDispose = true;
            runtime.Update();
            runtime.Update();
            Check(runtime.Manager == null && failing.Disposed && Log.Failures >= 2, "failure cleans up safely even when reveal and dispose fail");
            Check(!Harmony.GetPatchInfo(target).Owners.Contains(owner), "failure still removes our patches");
            MySession.Unload();
            MySession.Static = World("/world/recovered");
            runtime.Update();
            Check(runtime.Manager != null, "failure in one world does not disable later worlds");
            MySession.Unload();
            MySession.Static = new MySession { CurrentPath = "/world/loading" };
            MySession.Loading();
            Check(runtime.Manager != null && runtime.Tick == 0, "restore hooks exist before native grids load");
            runtime.Update();
            Check(runtime.Tick == 0, "preload registration does not simulate an unready world");
            MySession.Static.Ready = true;
        }
        Check(ConcealmentRuntime.Instance == null, "plugin disposal clears static runtime");
        using (var dedicated = new ConcealmentRuntime(config, Log, owner, "/legacy/server-data"))
        {
            dedicated.Update();
            Check(dedicated.Manager != null && dedicated.DataDirectory == "/legacy/server-data", "Magnetar preserves its existing production bank location");
        }
        MySession.Unload();
        other.Unpatch(target, HarmonyPatchType.All, other.Id);
        Check(!Harmony.HasAnyPatches(owner), "no leaked patches after disposal");

        var folder = Path.Combine(Path.GetTempPath(), "concealment-config-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Concealment.cfg");
        try
        {
            using (var saved = PersistentConfig<PluginConfig>.Load(Log, path))
            {
                Check(saved.IsNewDeployment && File.Exists(path), "new client config is initialized");
                saved.Data.ConcealDistance = 9876;
                saved.Data.DynamicConcealment.Add(new DynamicConcealmentRule { Name = "turret", TargetType = "LargeGatlingTurret", Distance = 50 });
            }
            using (var loaded = PersistentConfig<PluginConfig>.Load(Log, path))
                Check(!loaded.IsNewDeployment && loaded.Data.ConcealDistance == 9876 && loaded.Data.DynamicConcealment.Single().Name == "turret", "client config preserves settings and structured rules");
            File.WriteAllText(path, "broken xml");
            using (var recovered = PersistentConfig<PluginConfig>.Load(Log, path))
                Check(!recovered.IsNewDeployment && recovered.Data.Enabled && Directory.GetFiles(folder, "*.corrupted.*").Length == 1, "corrupt config is preserved and safely replaced");
        }
        finally { Directory.Delete(folder, true); }
        if (args.Length == 1)
        {
            var root = Path.GetFullPath(args[0]);
            foreach (var targetName in new[] { "ClientPlugin", "ServerPlugin" })
            {
                var assemblyPath = Path.Combine(root, targetName, "bin", "Release", "net10.0", "Concealment.dll");
                var assembly = Assembly.LoadFile(assemblyPath);
                Check(assembly.GetName().Version == new Version(1, 2, 0, 0), targetName + " has the correct version");
                bool hasSdk = assembly.GetReferencedAssemblies().Any(a => a.Name == "PluginSdk");
                Check(hasSdk == (targetName == "ServerPlugin"), "only Magnetar depends on PluginSdk");
                var copyright = assembly.GetCustomAttributes<AssemblyCopyrightAttribute>().Single();
                Check(copyright.Copyright == "Copyright (c) 2026 CometWorks", targetName + " retains its copyright");
            }
        }
        Console.WriteLine($"PASS: {checks} authority, lifecycle, patch ownership, command, and config checks");
    }
}
