using System;
using System.Linq;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Shared.Config;
using Shared.Logging;

namespace Shared.Concealment;

// One engine per authoritative world, regardless of which loader owns the plugin.
public sealed class ConcealmentRuntime : IDisposable
{
    public static bool ClusterMode { get; } = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY"));
    internal static bool StandaloneActive => !ClusterMode && Instance is { disposed: false, Manager: not null } runtime
        && (runtime.serverDataDirectory != null || Sync.IsServer);
    public static ConcealmentRuntime Instance { get; private set; }
    public PluginConfig ConfigData { get; }
    public IPluginLogger Log { get; }
    public long Tick { get; private set; }
    public ConcealmentManager Manager { get; private set; }
    public string DataDirectory { get; private set; }
    public bool IsAuthoritative => session != null && session == MySession.Static &&
        !session.IsUnloading && Sync.IsServer;

    private readonly Harmony harmony;
    private readonly string serverDataDirectory;
    private MySession session;
    private bool failed;
    private bool disposed;

    public ConcealmentRuntime(PluginConfig config, IPluginLogger log, string patchId,
        string serverDataDirectory = null)
    {
        ConfigData = config;
        Log = log;
        harmony = new Harmony(patchId);
        this.serverDataDirectory = serverDataDirectory;
        Instance = this;
        if (!ClusterMode)
        {
            typeof(MySession).GetEvent("BeforeLoading").AddEventHandler(null, (Action)BeginSession);
            typeof(MySession).GetEvent("OnUnloading").AddEventHandler(null, (Action)EndSession);
        }
    }

    // BeforeLoading runs before native grid Init. Install restore/update guards
    // here, while keeping joining clients and the main menu passive.
    private void BeginSession()
    {
        var current = MySession.Static;
        if (disposed || ClusterMode || current == null || current.IsUnloading || !Sync.IsServer) return;
        if (session != null && session != current) EndSession();
        session = current;
        DataDirectory = serverDataDirectory ?? current.CurrentPath;
        Manager ??= new ConcealmentManager(this, harmony);
    }

    public void Update()
    {
        if (disposed || ClusterMode)
            return;

        var current = MySession.Static;
        if (session != null && (session != current || !Sync.IsServer))
            EndSession();

        if (current == null || !current.Ready || current.IsUnloading || !Sync.IsServer)
            return;

        if (session == null)
        {
            session = current;
            DataDirectory = serverDataDirectory ?? current.CurrentPath;
        }

        if (failed || MySandboxGame.IsPaused)
            return;

        try
        {
            if (Manager == null) BeginSession();
            Manager.Update();
            Tick++;
        }
        catch (Exception ex)
        {
            failed = true;
            Log.Critical(ex, "Concealment stopped for this session after an update failure");
            try
            {
                ReleaseManager();
            }
            catch (Exception cleanupEx)
            {
                Log.Error(cleanupEx, "Failed to clean up after the concealment failure");
            }
        }
    }

    private void ReleaseManager()
    {
        try
        {
            if (session == MySession.Static)
                Manager?.RevealAll();
        }
        finally
        {
            try
            {
                Manager?.Dispose();
            }
            finally
            {
                Manager = null;
                // Remove only our patches; never touch another plugin's patches.
                foreach (var method in Harmony.GetAllPatchedMethods()
                    .Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(harmony.Id) == true)
                    .ToArray())
                    harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
            }
        }
    }

    private void EndSession()
    {
        try
        {
            ReleaseManager();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to finish the concealment session");
        }
        finally
        {
            session = null;
            Tick = 0;
            failed = false;
            DataDirectory = null;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        typeof(MySession).GetEvent("BeforeLoading").RemoveEventHandler(null, (Action)BeginSession);
        typeof(MySession).GetEvent("OnUnloading").RemoveEventHandler(null, (Action)EndSession);
        EndSession();
        Instance = null;
    }
}
