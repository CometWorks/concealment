using System;
using System.IO;
using System.Reflection;
using System.Threading;
using PluginSdk.Commands;
using Shared.Config;
using Shared.Concealment;
using Shared.Logging;
using Shared.Plugin;
using VRage.FileSystem;
using VRage.Game;
using VRage.Plugins;
using ConcealmentConfig = Shared.Config.PluginConfig;
using ConfigStorage = PluginSdk.Config.ConfigStorage;
// Define assembly version when compiled by Magnetar
#if !LOCAL_BUILD
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
[assembly: AssemblyCopyright("Copyright (c) 2026 CometWorks")]

#endif

namespace ServerPlugin;

// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin, ICommonPlugin
{
    public const string Name = "Concealment";
    public static Plugin Instance { get; private set; }

    public static bool ClusterMode => ConcealmentRuntime.ClusterMode;

    public long Tick => runtime?.Tick ?? 0;
    private ConcealmentRuntime runtime;

    public IPluginLogger Log => Logger;
    private static readonly IPluginLogger Logger = new PluginLogger(Name);

    public IPluginConfig Config => ConfigData;
    public ConcealmentConfig ConfigData { get; private set; }
    public ConcealmentManager Manager => runtime?.Manager;

    private string configPath;
    private static readonly string ConfigFileName = $"{Name}.cfg";

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining
    )]
    public void Init(object gameInstance)
    {
#if DEBUG
        // Allow the debugger some time to connect once the plugin assembly is loaded
        Thread.Sleep(100);
#endif

        Instance = this;

        Log.Info("Loading");

        configPath = Path.Combine(MyFileSystem.UserDataPath, ConfigFileName);
        ConfigData = LoadConfig(configPath);
        ConfigData.PropertyChanged += ConfigOnPropertyChanged;

        var gameVersion = MyFinalBuildConstants.APP_VERSION_STRING.ToString();
        Common.SetPlugin(this, gameVersion, MyFileSystem.UserDataPath);

        if (ClusterMode)
        {
            Log.Info("Cluster owns grid lifecycle; standalone Concealment is disabled until restart.");
            return;
        }

        runtime = new ConcealmentRuntime(ConfigData, Log, Name + ".Server", MyFileSystem.UserDataPath);
        RegisterCommands();

        Log.Debug("Successfully loaded");
    }

    public void Dispose()
    {
        try
        {
            runtime?.Dispose();
            SaveConfig();

            if (ConfigData != null)
                ConfigData.PropertyChanged -= ConfigOnPropertyChanged;

            // IMPORTANT: Do NOT call harmony.UnpatchAll() here! It may break other plugins.
        }
        catch (Exception ex)
        {
            Log.Critical(ex, "Dispose failed");
        }

        runtime = null;
        ConfigData = null;
        Instance = null;
    }

    public void Update() => runtime?.Update();

    private ConcealmentConfig LoadConfig(string path)
    {
        try
        {
            var loaded = ConfigStorage.LoadXml<ConcealmentConfig>(path);
            ConfigStorage.SaveXml(loaded, path);
            return loaded;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load configuration file: {0}", path);

            try
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var corruptedPath = $"{path}.corrupted.{timestamp}.txt";
                Log.Info("Moving corrupted configuration file: {0} => {1}", path, corruptedPath);
                File.Move(path, corruptedPath);
            }
            catch (Exception moveEx)
            {
                Log.Warning(moveEx, "Failed to move corrupted configuration file");
            }

            var config = new ConcealmentConfig();
            ConfigStorage.SaveXml(config, path);
            return config;
        }
    }

    private void SaveConfig()
    {
        if (ConfigData == null || configPath == null)
            return;

        ConfigStorage.SaveXml(ConfigData, configPath);
    }

    private void ConfigOnPropertyChanged(
        object sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        try
        {
            SaveConfig();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save configuration file: {0}", configPath);
        }
    }

    private void RegisterCommands()
    {
        try
        {
            ServerCommands.Register(Assembly.GetExecutingAssembly(), typeof(ConcealmentCommands));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to register chat commands");
        }
    }
}
