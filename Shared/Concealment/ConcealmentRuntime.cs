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
    // Scheduling can stop before the native world unloads. Its claims and update
    // guards still belong to this runtime until that world is detached.
    internal static bool StandaloneOwner => !ClusterMode && Instance is { Manager: not null } runtime
        && runtime.session == MySession.Static && runtime.session != null && Sync.IsServer;
    internal static bool StandaloneActive => StandaloneOwner && !Instance.disposed && !Instance.failed;
    public static ConcealmentRuntime Instance { get; private set; }
    public PluginConfig ConfigData { get; }
    public IPluginLogger Log { get; }
    public long Tick { get; private set; }
    public ConcealmentManager Manager { get; private set; }
    public string DataDirectory { get; private set; }
    public bool IsAuthoritative => !ClusterMode && !disposed && !failed && session != null && session == MySession.Static &&
        !session.IsUnloading && Sync.IsServer;

    private readonly Harmony harmony;
    private readonly string serverDataDirectory;
    private MySession session;
    private bool failed;
    private bool disposed;

    public ConcealmentRuntime(PluginConfig config, IPluginLogger log, string patchId,
        string serverDataDirectory = null)
    {
        if (Instance?.Manager != null)
            throw new InvalidOperationException("Concealment still owns a loaded world; unload it before replacing the runtime.");
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
        if (disposed || failed || ClusterMode || current == null || current.IsUnloading || !Sync.IsServer) return;
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
                StopManager();
            }
            catch (Exception cleanupEx)
            {
                Log.Error(cleanupEx, "Failed to clean up after the concealment failure");
            }
        }
    }

    private void StopManager()
    {
        try
        {
            if (session == MySession.Static)
                Manager?.RevealAll();
        }
        finally
        {
            Manager?.StopScheduling();
            // A rejected claim may have no ConcealedGroup to reveal. Retain its
            // builder serializer and native guards across every later world save.
            if (Manager?.RequiresWorldRetention != true)
                ReleaseManager();
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
            if (disposed) FinishDispose();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        typeof(MySession).GetEvent("BeforeLoading").RemoveEventHandler(null, (Action)BeginSession);
        // Plugin disposal is not necessarily world unload. Keep only world-owned
        // persistence/protection when the loaded world still has saved state.
        if (!ClusterMode && session != null && session == MySession.Static && !session.IsUnloading && Sync.IsServer)
        {
            try { StopManager(); }
            finally { if (Manager == null) FinishDispose(); }
        }
        else EndSession();
    }

    private void FinishDispose()
    {
        typeof(MySession).GetEvent("OnUnloading").RemoveEventHandler(null, (Action)EndSession);
        if (ReferenceEquals(Instance, this)) Instance = null;
    }
}
