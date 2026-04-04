// Legacy implementation: kept for reference only, not compiled. Active plugin is Plugin2.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using UnityEngine;

namespace ServerSyncModTemplate;

[ㅇBepInPlugin(ModGUID, ModName, ModVersion)]
public class ServerSyncModTemplatePlugin : BaseUnityPlugin
{
    internal const string ModName = "DiveIn";
    internal const string ModVersion = "1.0.0";
    internal const string Author = "sighsorry";
    private const string ModGUID = $"{Author}.{ModName}";
    private static readonly string ConfigFileName = $"{ModGUID}.cfg";
    private static readonly string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
    internal static string ConnectionError = "";
    private readonly Harmony _harmony = new(ModGUID);
    public static readonly ManualLogSource ServerSyncModTemplateLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
    private static readonly ConfigSync ConfigSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

    private static ConfigEntry<string> _divingMonsterPrefabs = null!;
    private static ConfigEntry<float> _minSwimDepth = null!;
    private static ConfigEntry<float> _maxSwimDepth = null!;
    private static ConfigEntry<float> _targetDepthOffset = null!;
    private static ConfigEntry<float> _depthAdjustSpeed = null!;

    private static readonly object PrefabSetLock = new();
    private static string _lastPrefabConfig = string.Empty;
    private static HashSet<string> _divingPrefabSet = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher _watcher = null!;
    private readonly object _reloadLock = new();
    private DateTime _lastConfigReloadTime;
    private const long RELOAD_DELAY = 10000000; // One second

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public void Awake()
    {
        bool saveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;

        // Uncomment the line below to use the LocalizationManager for localizing your mod.
        // Make sure to populate the English.yml file in the translation folder with your keys to be localized and the values associated before uncommenting!.
        //Localizer.Load(); // Use this to initialize the LocalizationManager (for more information on LocalizationManager, see the LocalizationManager documentation https://github.com/blaxxun-boop/LocalizationManager#example-project).

        _serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
        _ = ConfigSync.AddLockingConfigEntry(_serverConfigLocked);

        _divingMonsterPrefabs = config(
            "2 - Underwater Dive",
            "Diving Monster Prefabs",
            "",
            "Comma-separated MonsterAI prefab names that are allowed to dive/swim underwater. Example: Serpent,Leech,Troll.");
        _minSwimDepth = config(
            "2 - Underwater Dive",
            "Minimum Swim Depth",
            1.4f,
            new ConfigDescription("Minimum underwater swim depth used for configured MonsterAI prefabs.", new AcceptableValueRange<float>(0.25f, 20f)));
        _maxSwimDepth = config(
            "2 - Underwater Dive",
            "Maximum Swim Depth",
            100f,
            new ConfigDescription("Maximum underwater swim depth used for configured MonsterAI prefabs.", new AcceptableValueRange<float>(2f, 500f)));
        _targetDepthOffset = config(
            "2 - Underwater Dive",
            "Target Depth Offset",
            0f,
            new ConfigDescription("Additional depth offset when diving toward a target point. Positive values dive deeper.", new AcceptableValueRange<float>(-20f, 50f)));
        _depthAdjustSpeed = config(
            "2 - Underwater Dive",
            "Depth Adjust Speed",
            8f,
            new ConfigDescription("How quickly AI adapts to the target swim depth.", new AcceptableValueRange<float>(0f, 60f)));

        RefreshDivingPrefabCacheIfNeeded(force: true);

        Assembly assembly = Assembly.GetExecutingAssembly();
        _harmony.PatchAll(assembly);
        SetupWatcher();

        Config.Save();
        if (saveOnSet)
        {
            Config.SaveOnConfigSet = saveOnSet;
        }
    }

    private void OnDestroy()
    {
        SaveWithRespectToConfigSet();
        _watcher?.Dispose();
    }

    private void SetupWatcher()
    {
        _watcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName);
        _watcher.Changed += ReadConfigValues;
        _watcher.Created += ReadConfigValues;
        _watcher.Renamed += ReadConfigValues;
        _watcher.IncludeSubdirectories = true;
        _watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _watcher.EnableRaisingEvents = true;
    }

    private void ReadConfigValues(object sender, FileSystemEventArgs e)
    {
        DateTime now = DateTime.Now;
        long time = now.Ticks - _lastConfigReloadTime.Ticks;
        if (time < RELOAD_DELAY)
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
                ServerSyncModTemplateLogger.LogDebug("Reloading configuration...");
                SaveWithRespectToConfigSet(true);
                RefreshDivingPrefabCacheIfNeeded(force: true);
                ServerSyncModTemplateLogger.LogInfo("Configuration reload complete.");
            }
            catch (Exception ex)
            {
                ServerSyncModTemplateLogger.LogError($"Error reloading configuration: {ex.Message}");
            }
        }

        _lastConfigReloadTime = now;
    }

    private void SaveWithRespectToConfigSet(bool reload = false)
    {
        bool originalSaveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        if (reload)
        {
            Config.Reload();
        }
        Config.Save();
        if (originalSaveOnSet)
        {
            Config.SaveOnConfigSet = originalSaveOnSet;
        }

        // If you want to do something once localization completes, LocalizationManager has a hook for that.
        /*Localizer.OnLocalizationComplete += () =>
        {
            // Do something
            ItemManagerModTemplateLogger.LogDebug("OnLocalizationComplete called");
        };*/
    }

    private static void RefreshDivingPrefabCacheIfNeeded(bool force = false)
    {
        string raw = _divingMonsterPrefabs?.Value ?? string.Empty;
        if (!force && string.Equals(raw, _lastPrefabConfig, StringComparison.Ordinal))
        {
            return;
        }

        lock (PrefabSetLock)
        {
            if (!force && string.Equals(raw, _lastPrefabConfig, StringComparison.Ordinal))
            {
                return;
            }

            _divingPrefabSet = raw
                .Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(prefab => prefab.Trim())
                .Where(prefab => prefab.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _lastPrefabConfig = raw;
        }
    }

    private static bool IsDivingMonster(MonsterAI monsterAI)
    {
        if (monsterAI == null)
        {
            return false;
        }

        RefreshDivingPrefabCacheIfNeeded();

        string prefabName = Utils.GetPrefabName(monsterAI.gameObject);
        return _divingPrefabSet.Contains(prefabName);
    }

    private static bool TryGetConfiguredMonster(BaseAI ai, out MonsterAI monsterAI)
    {
        if (ai is MonsterAI typedMonster && IsDivingMonster(typedMonster))
        {
            monsterAI = typedMonster;
            return true;
        }

        monsterAI = null!;
        return false;
    }

    private struct MoveToPatchState
    {
        public bool Active;
        public bool PreviousFlying;
    }

    [HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.Awake))]
    private static class MonsterAIAwakePatch
    {
        private static void Postfix(MonsterAI __instance)
        {
            if (!IsDivingMonster(__instance))
            {
                return;
            }

            __instance.m_avoidWater = false;
        }
    }

    [HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.UpdateAI))]
    private static class MonsterAIUpdateAIPatch
    {
        private static void Prefix(MonsterAI __instance)
        {
            if (!IsDivingMonster(__instance))
            {
                return;
            }

            __instance.m_avoidWater = false;
        }
    }

    [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.HavePath))]
    private static class BaseAIHavePathPatch
    {
        private static bool Prefix(BaseAI __instance, ref bool __result)
        {
            if (!TryGetConfiguredMonster(__instance, out MonsterAI monsterAI))
            {
                return true;
            }

            Character character = monsterAI.m_character;
            if (character == null || !character.InLiquid())
            {
                return true;
            }

            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.MoveTo))]
    private static class BaseAIMoveToPatch
    {
        private static void Prefix(BaseAI __instance, ref Vector3 point, float dt, ref MoveToPatchState __state)
        {
            __state = default;
            if (!TryGetConfiguredMonster(__instance, out MonsterAI monsterAI))
            {
                return;
            }

            Character character = monsterAI.m_character;
            if (character == null || !character.InLiquid())
            {
                return;
            }

            __state.Active = true;
            __state.PreviousFlying = character.m_flying;

            float minDepth = Mathf.Max(0.25f, _minSwimDepth.Value);
            float maxDepth = Mathf.Max(minDepth, _maxSwimDepth.Value);
            float liquidLevel = character.GetLiquidLevel();
            float desiredDepth = liquidLevel - point.y + _targetDepthOffset.Value;
            desiredDepth = Mathf.Clamp(desiredDepth, minDepth, maxDepth);

            float depthAdjustSpeed = Mathf.Max(0f, _depthAdjustSpeed.Value);
            if (depthAdjustSpeed <= 0f)
            {
                character.m_swimDepth = desiredDepth;
            }
            else
            {
                float step = depthAdjustSpeed * Mathf.Max(dt, 0.01f);
                character.m_swimDepth = Mathf.Clamp(Mathf.MoveTowards(character.m_swimDepth, desiredDepth, step), minDepth, maxDepth);
            }

            // Treat underwater navigation as flying for this MoveTo call so AI uses 3D steering.
            character.m_flying = true;
        }

        private static void Postfix(BaseAI __instance, MoveToPatchState __state)
        {
            if (!__state.Active || !TryGetConfiguredMonster(__instance, out MonsterAI monsterAI))
            {
                return;
            }

            Character character = monsterAI.m_character;
            if (character == null)
            {
                return;
            }

            character.m_flying = __state.PreviousFlying;
        }
    }

    #region ConfigOptions

    private static ConfigEntry<Toggle> _serverConfigLocked = null!;

    private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
    {
        ConfigDescription extendedDescription = new(description.Description + (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"), description.AcceptableValues, description.Tags);
        ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);
        //var configEntry = Config.Bind(group, name, value, description);

        SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
        syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

        return configEntry;
    }

    private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true)
    {
        return config(group, name, value, new ConfigDescription(description), synchronizedSetting);
    }

    private class ConfigurationManagerAttributes
    {
        [UsedImplicitly] public int? Order = null!;
        [UsedImplicitly] public bool? Browsable = null!;
        [UsedImplicitly] public string? Category = null!;
        [UsedImplicitly] public Action<ConfigEntryBase>? CustomDrawer = null!;
    }

    class AcceptableShortcuts() : AcceptableValueBase(typeof(KeyboardShortcut))
    {
        public override object Clamp(object value) => value;
        public override bool IsValid(object value) => true;

        public override string ToDescriptionString() => $"# Acceptable values: {string.Join(", ", UnityInput.Current.SupportedKeyCodes)}";
    }

    #endregion
}

public static class KeyboardExtensions
{
    extension(KeyboardShortcut shortcut)
    {
        public bool IsKeyDown()
        {
            return shortcut.MainKey != KeyCode.None && Input.GetKeyDown(shortcut.MainKey) && shortcut.Modifiers.All(Input.GetKey);
        }

        public bool IsKeyHeld()
        {
            return shortcut.MainKey != KeyCode.None && Input.GetKey(shortcut.MainKey) && shortcut.Modifiers.All(Input.GetKey);
        }
    }
}

public static class ToggleExtentions
{
    extension(ServerSyncModTemplatePlugin.Toggle value)
    {
        public bool IsOn()
        {
            return value == ServerSyncModTemplatePlugin.Toggle.On;
        }

        public bool IsOff()
        {
            return value == ServerSyncModTemplatePlugin.Toggle.Off;
        }
    }
}
