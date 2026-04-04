﻿using System;
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

[BepInPlugin(ModGUID, ModName, ModVersion)]
public partial class ServerSyncModTemplatePlugin : BaseUnityPlugin
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

    private static ConfigEntry<float> _diveAiQuality = null!;

    private static readonly object PrefabSetLock = new();
    private static Dictionary<string, ConfiguredDiveProfile> _configuredDiveProfilesByPrefabName = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher _watcher = null!;
    private readonly object _reloadLock = new();
    private DateTime _lastConfigReloadTime;
    private const long RELOAD_DELAY = 10000000;
    private const float PassiveWavePeriodSeconds = 12f;
    private static readonly float[] SteerAngles = { 0f, -35f, 35f, -70f, 70f, -120f, 120f, 180f };
    private static readonly float[] RouteAngles = { 0f, -35f, 35f, -70f, 70f };

    private readonly struct RouteCacheEntry
    {
        public readonly float Time;
        public readonly Vector3Int PositionBucket;
        public readonly Vector3Int TargetBucket;
        public readonly bool Result;

        public RouteCacheEntry(float time, Vector3Int positionBucket, Vector3Int targetBucket, bool result)
        {
            Time = time;
            PositionBucket = positionBucket;
            TargetBucket = targetBucket;
            Result = result;
        }
    }

    private readonly struct SteerCacheEntry
    {
        public readonly float Time;
        public readonly Vector3Int PositionBucket;
        public readonly Vector3Int TargetBucket;
        public readonly Vector3 Direction;

        public SteerCacheEntry(float time, Vector3Int positionBucket, Vector3Int targetBucket, Vector3 direction)
        {
            Time = time;
            PositionBucket = positionBucket;
            TargetBucket = targetBucket;
            Direction = direction;
        }
    }

    private readonly struct PassiveDepthProfile
    {
        public readonly float CenterDepth;
        public readonly float MinDepth;
        public readonly float MaxDepth;

        public PassiveDepthProfile(float centerDepth, float minDepth, float maxDepth)
        {
            CenterDepth = centerDepth;
            MinDepth = minDepth;
            MaxDepth = maxDepth;
        }
    }

    private readonly struct ConfiguredDiveProfile
    {
        public readonly string GroupName;
        public readonly PassiveDepthProfile PassiveDepthProfile;

        public ConfiguredDiveProfile(string groupName, PassiveDepthProfile passiveDepthProfile)
        {
            GroupName = groupName;
            PassiveDepthProfile = passiveDepthProfile;
        }
    }

    private readonly struct OriginalDiveFlags
    {
        public readonly MonsterAI MonsterAI;
        public readonly bool AvoidWater;
        public readonly bool CanSwim;

        public OriginalDiveFlags(MonsterAI monsterAI, bool avoidWater, bool canSwim)
        {
            MonsterAI = monsterAI;
            AvoidWater = avoidWater;
            CanSwim = canSwim;
        }
    }

    private static readonly Dictionary<int, RouteCacheEntry> RouteCache = new();
    private static readonly Dictionary<int, SteerCacheEntry> SteerCache = new();
    private static readonly Dictionary<int, OriginalDiveFlags> OriginalDiveFlagsByInstance = new();
    private const int MaxCacheEntries = 2048;

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public void Awake()
    {
        bool saveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;

        _serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
        _ = ConfigSync.AddLockingConfigEntry(_serverConfigLocked);

        _diveAiQuality = config(
            "3 - Performance",
            "Dive AI Quality",
            50f,
            new ConfigDescription("Single quality slider for underwater AI behavior. 0 = minimum CPU/minimum smoothness, 100 = maximum CPU/maximum smoothness. Internally adjusts route check cache time, steer cache time, cache cell size, and avoidance sample count.", new AcceptableValueRange<float>(0f, 100f)));

        InitializePlayerDiveConfig();

        InitializeMonsterDiveYaml();
        ClearRuntimeCaches();

        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        SetupWatcher();
        SetupMonsterDiveYamlWatcher();

        Config.Save();
        if (saveOnSet)
        {
            Config.SaveOnConfigSet = saveOnSet;
        }
    }

    private void OnDestroy()
    {
        UnderwaterVisualState.ResetAll("plugin destroy");
        SaveWithRespectToConfigSet();
        _watcher?.Dispose();
        DisposeMonsterDiveYamlWatcher();
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
        if (now.Ticks - _lastConfigReloadTime.Ticks < RELOAD_DELAY)
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
                SaveWithRespectToConfigSet(reload: true);
                UnderwaterVisualState.ResetAll("config reload");
                ClearRuntimeCaches();
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
    }

    private static bool TryGetConfiguredDiveProfile(MonsterAI monsterAI, out ConfiguredDiveProfile configuredDiveProfile)
    {
        if (monsterAI == null)
        {
            configuredDiveProfile = default;
            return false;
        }

        string prefabName = Utils.GetPrefabName(monsterAI.gameObject);
        return _configuredDiveProfilesByPrefabName.TryGetValue(prefabName, out configuredDiveProfile);
    }

    private static bool IsConfiguredMonster(MonsterAI monsterAI)
    {
        return TryGetConfiguredDiveProfile(monsterAI, out _);
    }

    private static bool TryGetConfiguredMonster(BaseAI ai, out MonsterAI monsterAI)
    {
        if (ai is MonsterAI typedMonster && IsConfiguredMonster(typedMonster))
        {
            monsterAI = typedMonster;
            return true;
        }

        monsterAI = null!;
        return false;
    }

    private static bool ShouldUseWaterDiveMode(MonsterAI monsterAI)
    {
        Character character = monsterAI.m_character;
        if (character == null)
        {
            return false;
        }

        return character.InWater() && character.InLiquidDepth() > 0.05f;
    }

    private static bool IsPassiveDiveState(MonsterAI monsterAI)
    {
        return !monsterAI.IsAlerted() && monsterAI.m_targetCreature == null && monsterAI.m_targetStatic == null;
    }

    private static void EnsureDiveFlags(MonsterAI monsterAI)
    {
        TrackOriginalDiveFlags(monsterAI);

        if (monsterAI.m_avoidWater)
        {
            monsterAI.m_avoidWater = false;
        }

        Character character = monsterAI.m_character;
        if (character != null && !character.m_canSwim)
        {
            character.m_canSwim = true;
        }
    }

    private static void TrackOriginalDiveFlags(MonsterAI monsterAI)
    {
        if (!monsterAI)
        {
            return;
        }

        int instanceId = monsterAI.GetInstanceID();
        if (OriginalDiveFlagsByInstance.ContainsKey(instanceId))
        {
            return;
        }

        Character character = monsterAI.m_character;
        OriginalDiveFlagsByInstance[instanceId] = new OriginalDiveFlags(
            monsterAI,
            monsterAI.m_avoidWater,
            character != null && character.m_canSwim);

        if (OriginalDiveFlagsByInstance.Count > MaxCacheEntries)
        {
            PruneTrackedOriginalDiveFlags();
        }
    }

    private static int RestoreRemovedMonsterDiveFlags()
    {
        if (OriginalDiveFlagsByInstance.Count == 0)
        {
            return 0;
        }

        List<int> instanceIdsToRemove = new();
        int restoredCount = 0;

        foreach (KeyValuePair<int, OriginalDiveFlags> entry in OriginalDiveFlagsByInstance)
        {
            int instanceId = entry.Key;
            OriginalDiveFlags originalFlags = entry.Value;
            MonsterAI monsterAI = originalFlags.MonsterAI;
            if (!monsterAI)
            {
                instanceIdsToRemove.Add(instanceId);
                continue;
            }

            if (IsConfiguredMonster(monsterAI))
            {
                continue;
            }

            monsterAI.m_avoidWater = originalFlags.AvoidWater;
            Character character = monsterAI.m_character;
            if (character != null)
            {
                character.m_canSwim = originalFlags.CanSwim;
            }

            instanceIdsToRemove.Add(instanceId);
            restoredCount++;
        }

        foreach (int instanceId in instanceIdsToRemove)
        {
            OriginalDiveFlagsByInstance.Remove(instanceId);
        }

        return restoredCount;
    }

    private static void PruneTrackedOriginalDiveFlags()
    {
        if (OriginalDiveFlagsByInstance.Count == 0)
        {
            return;
        }

        List<int> instanceIdsToRemove = new();
        foreach (KeyValuePair<int, OriginalDiveFlags> entry in OriginalDiveFlagsByInstance)
        {
            int instanceId = entry.Key;
            OriginalDiveFlags originalFlags = entry.Value;
            if (!originalFlags.MonsterAI)
            {
                instanceIdsToRemove.Add(instanceId);
            }
        }

        foreach (int instanceId in instanceIdsToRemove)
        {
            OriginalDiveFlagsByInstance.Remove(instanceId);
        }
    }

    private static Vector3Int ToCacheBucket(Vector3 value)
    {
        float cellSize = GetCacheCellSize();
        return new Vector3Int(
            Mathf.RoundToInt(value.x / cellSize),
            Mathf.RoundToInt(value.y / cellSize),
            Mathf.RoundToInt(value.z / cellSize));
    }

    private static void ClearRuntimeCaches()
    {
        RouteCache.Clear();
        SteerCache.Clear();
    }

    private static void TrimCachesIfNeeded()
    {
        if (RouteCache.Count > MaxCacheEntries)
        {
            RouteCache.Clear();
        }

        if (SteerCache.Count > MaxCacheEntries)
        {
            SteerCache.Clear();
        }

    }

    private static float GetPassiveDesiredDepth(MonsterAI monsterAI)
    {
        if (!TryGetConfiguredDiveProfile(monsterAI, out ConfiguredDiveProfile configuredDiveProfile))
        {
            return 0f;
        }

        PassiveDepthProfile profile = configuredDiveProfile.PassiveDepthProfile;
        int instanceId = Mathf.Abs(monsterAI.GetInstanceID());
        float phasedTime = Time.time + (instanceId % 997) * 0.173f;
        float wave = Mathf.Sin(Mathf.Repeat(phasedTime, PassiveWavePeriodSeconds) / PassiveWavePeriodSeconds * Mathf.PI * 2f);
        float surfaceAmplitude = Mathf.Max(0f, profile.CenterDepth - profile.MinDepth);
        float deepAmplitude = Mathf.Max(0f, profile.MaxDepth - profile.CenterDepth);
        return wave >= 0f
            ? profile.CenterDepth + wave * deepAmplitude
            : profile.CenterDepth + wave * surfaceAmplitude;
    }

    private static void TryLogOverlapFields(MonsterAI monsterAI, string stage)
    {
        // Debug-only overlap logging removed from config surface.
    }

    private static float GetQuality01()
    {
        return Mathf.Clamp01(_diveAiQuality.Value / 100f);
    }

    private static float GetRouteCacheSeconds()
    {
        return Mathf.Lerp(1f, 0f, GetQuality01());
    }

    private static float GetSteerCacheSeconds()
    {
        return Mathf.Lerp(1f, 0f, GetQuality01());
    }

    private static float GetCacheCellSize()
    {
        return Mathf.Lerp(3f, 0.1f, GetQuality01());
    }

    private static int GetAvoidanceSampleCount()
    {
        return Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(3f, 8f, GetQuality01())), 3, 8);
    }

    private readonly struct SwimDepthGoal
    {
        public readonly float ClampedTargetY;
        public readonly bool RequestedOutsideRange;

        public SwimDepthGoal(float clampedTargetY, bool requestedOutsideRange)
        {
            ClampedTargetY = clampedTargetY;
            RequestedOutsideRange = requestedOutsideRange;
        }
    }

    private static SwimDepthGoal UpdateSwimDepthTowardsTarget(MonsterAI monsterAI, Character character, Vector3 point, float dt)
    {
        float minDepth = _monsterDiveGlobalSettings.SwimDepthMin;
        float maxDepth = _monsterDiveGlobalSettings.SwimDepthMax;

        float liquidLevel = character.GetLiquidLevel();
        float requestedDepth = liquidLevel - point.y;
        bool passiveDive = IsPassiveDiveState(monsterAI);
        float desiredDepth = passiveDive ? GetPassiveDesiredDepth(monsterAI) : requestedDepth;
        bool requestedOutsideRange = passiveDive || requestedDepth < minDepth || requestedDepth > maxDepth;
        desiredDepth = Mathf.Clamp(desiredDepth, minDepth, maxDepth);
        float clampedTargetY = liquidLevel - desiredDepth;

        float adjustSpeed = _monsterDiveGlobalSettings.SwimDepthAdjustSpeed;
        if (adjustSpeed <= 0f)
        {
            character.m_swimDepth = desiredDepth;
            return new SwimDepthGoal(clampedTargetY, requestedOutsideRange);
        }

        float step = adjustSpeed * Mathf.Max(dt, 0.01f);
        character.m_swimDepth = Mathf.Clamp(Mathf.MoveTowards(character.m_swimDepth, desiredDepth, step), minDepth, maxDepth);
        return new SwimDepthGoal(clampedTargetY, requestedOutsideRange);
    }

    private static Vector3 BuildSteerDirectionWithAvoidance(BaseAI ai, Character character, Vector3 targetPoint)
    {
        float steerCacheSeconds = GetSteerCacheSeconds();
        int instanceId = ai.GetInstanceID();
        Vector3Int currentPosBucket = ToCacheBucket(ai.transform.position);
        Vector3Int targetBucket = ToCacheBucket(targetPoint);
        float now = Time.time;

        if (steerCacheSeconds > 0f &&
            SteerCache.TryGetValue(instanceId, out SteerCacheEntry cachedSteer) &&
            now - cachedSteer.Time <= steerCacheSeconds &&
            cachedSteer.PositionBucket == currentPosBucket &&
            cachedSteer.TargetBucket == targetBucket)
        {
            return cachedSteer.Direction;
        }

        Vector3 desiredDir = targetPoint - ai.transform.position;
        if (desiredDir.sqrMagnitude <= 0.0001f)
        {
            return Vector3.zero;
        }

        desiredDir.Normalize();
        Vector3 horizontal = new(desiredDir.x, 0f, desiredDir.z);
        float radius = character.GetRadius();
        float checkDistance = Mathf.Clamp(Utils.DistanceXZ(targetPoint, ai.transform.position), radius + 1f, 6f);

        if (horizontal.sqrMagnitude > 0.0001f)
        {
            horizontal.Normalize();
            Vector3 center = character.GetCenterPoint();
            int sampleCount = Mathf.Clamp(GetAvoidanceSampleCount(), 3, SteerAngles.Length);
            Vector3 bestHorizontal = horizontal;
            float bestScore = float.NegativeInfinity;

            for (int i = 0; i < sampleCount; ++i)
            {
                float angle = SteerAngles[i];
                Vector3 candidate = Quaternion.Euler(0f, angle, 0f) * horizontal;
                bool clear = ai.CanMove(candidate, radius, checkDistance);
                float score;
                if (clear)
                {
                    score = 1000f - Mathf.Abs(angle);
                }
                else
                {
                    float freeDistance = ai.Raycast(center, candidate, checkDistance * 2f, 0.1f);
                    score = freeDistance - Mathf.Abs(angle) * 0.01f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestHorizontal = candidate;
                }
            }

            Vector3 steer = new(bestHorizontal.x, desiredDir.y, bestHorizontal.z);
            Vector3 result = steer.sqrMagnitude > 0.0001f ? steer.normalized : desiredDir;
            if (steerCacheSeconds > 0f)
            {
                TrimCachesIfNeeded();
                SteerCache[instanceId] = new SteerCacheEntry(now, currentPosBucket, targetBucket, result);
            }

            return result;
        }

        if (steerCacheSeconds > 0f)
        {
            TrimCachesIfNeeded();
            SteerCache[instanceId] = new SteerCacheEntry(now, currentPosBucket, targetBucket, desiredDir);
        }

        return desiredDir;
    }

    private static bool HasReasonableUnderwaterRoute(BaseAI ai, Character character, Vector3 targetPoint)
    {
        float routeCacheSeconds = GetRouteCacheSeconds();
        int instanceId = ai.GetInstanceID();
        Vector3Int currentPosBucket = ToCacheBucket(ai.transform.position);
        Vector3Int targetBucket = ToCacheBucket(targetPoint);
        float now = Time.time;

        if (routeCacheSeconds > 0f &&
            RouteCache.TryGetValue(instanceId, out RouteCacheEntry cachedRoute) &&
            now - cachedRoute.Time <= routeCacheSeconds &&
            cachedRoute.PositionBucket == currentPosBucket &&
            cachedRoute.TargetBucket == targetBucket)
        {
            return cachedRoute.Result;
        }

        float radius = character.GetRadius();
        float horizontalDistance = Utils.DistanceXZ(targetPoint, ai.transform.position);
        if (horizontalDistance <= radius + 0.6f)
        {
            if (routeCacheSeconds > 0f)
            {
                TrimCachesIfNeeded();
                RouteCache[instanceId] = new RouteCacheEntry(now, currentPosBucket, targetBucket, true);
            }
            return true;
        }

        Vector3 toTarget = targetPoint - ai.transform.position;
        Vector3 horizontal = new(toTarget.x, 0f, toTarget.z);
        if (horizontal.sqrMagnitude <= 0.0001f)
        {
            if (routeCacheSeconds > 0f)
            {
                TrimCachesIfNeeded();
                RouteCache[instanceId] = new RouteCacheEntry(now, currentPosBucket, targetBucket, true);
            }
            return true;
        }

        horizontal.Normalize();
        Vector3 center = character.GetCenterPoint();
        float checkDistance = Mathf.Clamp(horizontalDistance, radius + 1f, 6f);
        int sampleCount = Mathf.Clamp(GetAvoidanceSampleCount(), 3, RouteAngles.Length);
        for (int i = 0; i < sampleCount; ++i)
        {
            float angle = RouteAngles[i];
            Vector3 candidate = Quaternion.Euler(0f, angle, 0f) * horizontal;
            float freeDistance = ai.Raycast(center, candidate, checkDistance, 0.1f);
            if (freeDistance >= checkDistance * 0.9f)
            {
                if (routeCacheSeconds > 0f)
                {
                    TrimCachesIfNeeded();
                    RouteCache[instanceId] = new RouteCacheEntry(now, currentPosBucket, targetBucket, true);
                }
                return true;
            }
        }

        if (routeCacheSeconds > 0f)
        {
            TrimCachesIfNeeded();
            RouteCache[instanceId] = new RouteCacheEntry(now, currentPosBucket, targetBucket, false);
        }

        return false;
    }

    [HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.Awake))]
    private static class MonsterAIAwakePatch
    {
        private static void Postfix(MonsterAI __instance)
        {
            if (!IsConfiguredMonster(__instance))
            {
                return;
            }

            EnsureDiveFlags(__instance);
            TryLogOverlapFields(__instance, "MonsterAI.Awake");
        }
    }

    [HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.UpdateAI))]
    private static class MonsterAIUpdateAIPatch
    {
        private static void Prefix(MonsterAI __instance)
        {
            if (!IsConfiguredMonster(__instance))
            {
                return;
            }

            EnsureDiveFlags(__instance);
            TryLogOverlapFields(__instance, "MonsterAI.UpdateAI");
        }
    }

    [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.HavePath))]
    private static class BaseAIHavePathPatch
    {
        private static bool Prefix(BaseAI __instance, Vector3 target, ref bool __result)
        {
            if (!TryGetConfiguredMonster(__instance, out MonsterAI monsterAI) || !ShouldUseWaterDiveMode(monsterAI))
            {
                return true;
            }

            Character character = monsterAI.m_character;
            if (character == null)
            {
                return true;
            }

            // Underwater route check: avoid unconditional true to keep AI target logic consistent.
            __result = HasReasonableUnderwaterRoute(__instance, character, target);
            TryLogOverlapFields(monsterAI, "BaseAI.HavePath");
            return false;
        }
    }

    [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.MoveTo))]
    private static class BaseAIMoveToPatch
    {
        private static bool Prefix(BaseAI __instance, float dt, Vector3 point, float dist, bool run, ref bool __result)
        {
            if (!TryGetConfiguredMonster(__instance, out MonsterAI monsterAI) || !ShouldUseWaterDiveMode(monsterAI))
            {
                return true;
            }

            Character character = monsterAI.m_character;
            if (character == null)
            {
                return true;
            }

            SwimDepthGoal goal = UpdateSwimDepthTowardsTarget(monsterAI, character, point, dt);
            TryLogOverlapFields(monsterAI, "BaseAI.MoveTo");

            float stopDist = Mathf.Max(dist, run ? 1f : 0.5f);
            float horizontalDist = Utils.DistanceXZ(point, __instance.transform.position);
            float verticalToRequested = Mathf.Abs(point.y - __instance.transform.position.y);
            float verticalToClamped = Mathf.Abs(goal.ClampedTargetY - __instance.transform.position.y);
            bool verticalReached = verticalToRequested < 0.75f || (goal.RequestedOutsideRange && verticalToClamped < 0.35f);
            if (horizontalDist < stopDist && verticalReached)
            {
                __instance.StopMoving();
                __result = true;
                return false;
            }

            Vector3 moveDir = point - __instance.transform.position;
            if (moveDir.sqrMagnitude <= 0.0001f)
            {
                __instance.StopMoving();
                __result = true;
                return false;
            }

            Vector3 steerDir = BuildSteerDirectionWithAvoidance(__instance, character, point);
            if (steerDir.sqrMagnitude <= 0.0001f)
            {
                __instance.StopMoving();
                __result = true;
                return false;
            }

            __instance.MoveTowards(steerDir, run);
            __result = false;
            return false;
        }
    }

    #region ConfigOptions

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
