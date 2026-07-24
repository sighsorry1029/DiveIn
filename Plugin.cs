using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;

namespace ServerSyncModTemplate;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInIncompatibility("Searica.Valheim.UnderTheSea")]
[BepInIncompatibility("blacks7ar.VikingsDoSwim")]
public partial class ServerSyncModTemplatePlugin : BaseUnityPlugin
{
    internal const string ModName = "DiveIn";
    internal const string ModVersion = "1.1.9";
    internal const string Author = "sighsorry";
    private const string ModGUID = $"{Author}.{ModName}";

    private static readonly string ConfigFileName = $"{ModGUID}.cfg";
    private static readonly string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
    private readonly Harmony _harmony = new(ModGUID);
    public static readonly ManualLogSource ServerSyncModTemplateLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
    private static readonly ConfigSync ConfigSync = new(ModGUID)
    {
        DisplayName = ModName,
        CurrentVersion = ModVersion,
        MinimumRequiredVersion = ModVersion,
        ModRequired = true
    };

    private FileSystemWatcher _watcher = null!;
    private readonly object _reloadLock = new();
    private string? _lastConfigFileText;
    private bool _isShuttingDown;

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public void Awake()
    {
        _isShuttingDown = false;
        bool saveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        try
        {
            _serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
            _ = ConfigSync.AddLockingConfigEntry(_serverConfigLocked);

            InitializePlayerDiveConfig();
            InitializeMonsterDiveYaml();

            _harmony.PatchAll(Assembly.GetExecutingAssembly());
            DiveLocalization.Register();
            SetupConfigWatcher();
            SetupMonsterDiveYamlWatcher();

            Config.Save();
            _lastConfigFileText = ReadFileTextIfExists(ConfigFileFullPath);
        }
        catch
        {
            Cleanup(saveConfig: false);
            throw;
        }
        finally
        {
            Config.SaveOnConfigSet = saveOnSet;
        }
    }

    private void OnDestroy()
    {
        Cleanup(saveConfig: true);
    }

    private void Cleanup(bool saveConfig)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        TryCleanup(_harmony.UnpatchSelf, "Harmony patches");
        TryCleanup(
            () =>
            {
                PlayerDiveController? localDiver = PlayerDiveController.LocalInstance;
                if (localDiver == null)
                {
                    return;
                }

                localDiver.DisableUnderwaterMovement();
                UnityEngine.Object.Destroy(localDiver);
            },
            "player dive state");
        TryCleanup(PlayerDiveKeyHints.DestroyHints, "player key hints");
        TryCleanup(UnderwaterVisualState.ResetAll, "underwater visuals");
        TryCleanup(
            () =>
            {
                int restoredMonsterCount = RestoreAllTrackedMonsterDiveFlags();
                if (restoredMonsterCount > 0)
                {
                    ServerSyncModTemplateLogger.LogInfo($"Restored original dive flags for {restoredMonsterCount} monster instances.");
                }
            },
            "monster dive flags");
        TryCleanup(ClearSteeringMemory, "monster steering memory");
        TryCleanup(DisposeMonsterDiveYamlWatcher, "monster YAML watcher");
        TryCleanup(DisposeConfigWatcher, "configuration watcher");
        if (saveConfig)
        {
            TryCleanup(() => SaveWithRespectToConfigSet(), "configuration save");
        }
    }

    private static void TryCleanup(Action cleanup, string description)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            ServerSyncModTemplateLogger.LogError($"Failed to clean up {description}: {ex.Message}");
        }
    }

    private void SetupConfigWatcher()
    {
        _watcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName);
        _watcher.Changed += ReadConfigValues;
        _watcher.Created += ReadConfigValues;
        _watcher.Renamed += ReadConfigValues;
        _watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _watcher.EnableRaisingEvents = true;
    }

    private void DisposeConfigWatcher()
    {
        if (_watcher == null)
        {
            return;
        }

        _watcher.Changed -= ReadConfigValues;
        _watcher.Created -= ReadConfigValues;
        _watcher.Renamed -= ReadConfigValues;
        _watcher.Dispose();
        _watcher = null!;
    }

    private void ReadConfigValues(object sender, FileSystemEventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        lock (_reloadLock)
        {
            if (!File.Exists(ConfigFileFullPath))
            {
                ServerSyncModTemplateLogger.LogWarning("Config file does not exist. Skipping reload.");
                return;
            }

            try
            {
                string configFileText = File.ReadAllText(ConfigFileFullPath);
                if (string.Equals(_lastConfigFileText, configFileText, StringComparison.Ordinal))
                {
                    return;
                }

                SaveWithRespectToConfigSet(reload: true);
                _lastConfigFileText = ReadFileTextIfExists(ConfigFileFullPath);
                UnderwaterVisualState.ResetAll();
                ClearSteeringMemory();
                ServerSyncModTemplateLogger.LogInfo("Configuration reload complete.");
            }
            catch (Exception ex)
            {
                ServerSyncModTemplateLogger.LogError($"Error reloading configuration: {ex.Message}");
            }
        }
    }

    private static string? ReadFileTextIfExists(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private void SaveWithRespectToConfigSet(bool reload = false)
    {
        bool originalSaveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        try
        {
            if (reload)
            {
                Config.Reload();
            }

            Config.Save();
        }
        finally
        {
            Config.SaveOnConfigSet = originalSaveOnSet;
        }
    }

    private static ConfigEntry<Toggle> _serverConfigLocked = null!;

    private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
    {
        ConfigDescription extendedDescription = new(
            description.Description + (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"),
            description.AcceptableValues,
            description.Tags);

        ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);
        SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
        syncedConfigEntry.SynchronizedConfig = synchronizedSetting;
        return configEntry;
    }

    private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true)
    {
        return config(group, name, value, new ConfigDescription(description), synchronizedSetting);
    }
}
