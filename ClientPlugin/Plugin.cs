using System;
using System.IO;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Layouts;
using Sandbox.Game.Multiplayer;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using Shared.Config;
using Shared.Concealment;
using Shared.Logging;
using Shared.Plugin;
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Plugins;

#if !LOCAL_BUILD
using System.Reflection;
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
[assembly: AssemblyCopyright("Copyright (c) 2026 CometWorks")]
#endif

namespace ClientPlugin;

// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin, ICommonPlugin
{
    public const string Name = "Concealment";
    public static Plugin Instance { get; private set; }
    public IPluginLogger Log { get; } = new PluginLogger(Name);
    public PluginConfig ConfigData => config?.Data;
    public IPluginConfig Config => ConfigData;
    public long Tick => runtime?.Tick ?? 0;
    public ConcealmentManager Manager => runtime?.Manager;

    private PersistentConfig<PluginConfig> config;
    private ConcealmentRuntime runtime;
    private IMyUtilities utilities;

    public void Init(object gameInstance)
    {
        Instance = this;
        config = PersistentConfig<PluginConfig>.Load(Log,
            Path.Combine(MyFileSystem.UserDataPath, Name + ".cfg"));
        Common.SetPlugin(this, MyFinalBuildConstants.APP_VERSION_STRING.ToString(),
            MyFileSystem.UserDataPath);
        runtime = new ConcealmentRuntime(ConfigData, Log, Name + ".Client");
        Log.Info("Loaded; concealment runs in offline and player-hosted worlds");
    }

    public void Update()
    {
        if (utilities != MyAPIGateway.Utilities)
        {
            if (utilities != null)
                utilities.MessageEntered -= OnMessageEntered;
            utilities = MyAPIGateway.Utilities;
            if (utilities != null)
                utilities.MessageEntered += OnMessageEntered;
        }
        runtime?.Update();
    }

    private void OnMessageEntered(string message, ref bool sendToOthers)
    {
        // Joining clients send commands to their host, including Magnetar's SDK commands.
        if (!Sync.IsServer || !ConcealmentCommands.IsCommand(message))
            return;
        sendToOthers = false;
        RunCommand(message);
    }

    public void RunCommand(string message)
    {
        try
        {
            var position = MyAPIGateway.Session?.Player?.Controller?.ControlledEntity?.Entity?.GetPosition();
            utilities?.ShowMessage(Name, ConcealmentCommands.Execute(runtime, message, position));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Concealment command failed");
            utilities?.ShowMessage(Name, "Concealment command failed; see the game log.");
        }
    }

    // ReSharper disable once UnusedMember.Global
    public void OpenConfigDialog()
    {
        var settings = new SettingsGenerator();
        settings.SetLayout<Simple>();
        MyGuiSandbox.AddScreen(settings.Dialog);
    }

    public void Dispose()
    {
        if (utilities != null)
            utilities.MessageEntered -= OnMessageEntered;
        runtime?.Dispose();
        config?.Dispose();
        runtime = null;
        config = null;
        utilities = null;
        Instance = null;
    }
}
