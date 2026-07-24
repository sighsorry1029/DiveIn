using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using ServerSync;
using UnityEngine;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ServerSyncModTemplate;

public partial class ServerSyncModTemplatePlugin
{
    private static readonly string MonsterDiveYamlFileName = $"{ModName}.yaml";
    private static readonly string MonsterDiveYamlFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + MonsterDiveYamlFileName;
    private static readonly object MonsterDiveYamlLock = new();
    private static readonly IDeserializer MonsterDiveYamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .Build();

    private sealed class MonsterDiveYamlRoot : Dictionary<string, MonsterDiveYamlGroup>
    {
        public MonsterDiveYamlRoot()
            : base(StringComparer.OrdinalIgnoreCase)
        {
        }
    }

    private sealed class MonsterDiveYamlGroup
    {
        public float PassiveMinDepth { get; set; }
        public float PassiveCenterDepth { get; set; }
        public float PassiveMaxDepth { get; set; }
        public float? ActiveMinDepth { get; set; }
        public float? ActiveDepthAdjustSpeed { get; set; }
        public float? ShallowWaterFleeDepth { get; set; }
        public bool? PreserveSpawnDepth { get; set; }
        public bool? AvoidanceSteering { get; set; }
        public List<string> Prefabs { get; set; } = new();
    }

    private FileSystemWatcher _monsterDiveYamlWatcher = null!;
    private static CustomSyncedValue<string> _monsterDiveYamlSync = null!;
    private static string? _lastAppliedMonsterDiveYamlText;

    private void InitializeMonsterDiveYaml()
    {
        _monsterDiveYamlSync = new CustomSyncedValue<string>(ConfigSync, "MonsterDiveYaml", string.Empty, 0);
        _monsterDiveYamlSync.ValueChanged += OnMonsterDiveYamlValueChanged;
        ConfigSync.SourceOfTruthChanged += OnMonsterDiveSourceOfTruthChanged;

        if (ConfigSync.IsSourceOfTruth)
        {
            LoadMonsterDiveYamlFromDisk("startup");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_monsterDiveYamlSync.Value))
        {
            ApplyMonsterDiveYaml(_monsterDiveYamlSync.Value, "startup synced value");
        }
    }

    private void SetupMonsterDiveYamlWatcher()
    {
        _monsterDiveYamlWatcher = new FileSystemWatcher(Paths.ConfigPath, MonsterDiveYamlFileName);
        _monsterDiveYamlWatcher.Changed += ReadMonsterDiveYamlValues;
        _monsterDiveYamlWatcher.Created += ReadMonsterDiveYamlValues;
        _monsterDiveYamlWatcher.Renamed += ReadMonsterDiveYamlValues;
        _monsterDiveYamlWatcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _monsterDiveYamlWatcher.EnableRaisingEvents = true;
    }

    private void DisposeMonsterDiveYamlWatcher()
    {
        if (_monsterDiveYamlSync != null)
        {
            _monsterDiveYamlSync.ValueChanged -= OnMonsterDiveYamlValueChanged;
        }

        ConfigSync.SourceOfTruthChanged -= OnMonsterDiveSourceOfTruthChanged;
        if (_monsterDiveYamlWatcher == null)
        {
            return;
        }

        _monsterDiveYamlWatcher.Changed -= ReadMonsterDiveYamlValues;
        _monsterDiveYamlWatcher.Created -= ReadMonsterDiveYamlValues;
        _monsterDiveYamlWatcher.Renamed -= ReadMonsterDiveYamlValues;
        _monsterDiveYamlWatcher.Dispose();
        _monsterDiveYamlWatcher = null!;
    }

    private void ReadMonsterDiveYamlValues(object sender, FileSystemEventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        lock (_reloadLock)
        {
            if (!ConfigSync.IsSourceOfTruth)
            {
                ServerSyncModTemplateLogger.LogInfo("Ignoring local monster dive YAML reload because remote synced values are active.");
                return;
            }

            try
            {
                string? previouslyAppliedYamlText = _lastAppliedMonsterDiveYamlText;
                if (LoadMonsterDiveYamlFromDisk("yaml reload") &&
                    !string.Equals(previouslyAppliedYamlText, _lastAppliedMonsterDiveYamlText, StringComparison.Ordinal))
                {
                    ServerSyncModTemplateLogger.LogInfo("Monster dive YAML reload complete.");
                }
            }
            catch (Exception ex)
            {
                ServerSyncModTemplateLogger.LogError($"Error reloading monster dive YAML: {ex.Message}");
            }
        }
    }

    private void OnMonsterDiveSourceOfTruthChanged(bool isSourceOfTruth)
    {
        if (isSourceOfTruth)
        {
            LoadMonsterDiveYamlFromDisk("source of truth changed to local");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_monsterDiveYamlSync.Value))
        {
            ApplyMonsterDiveYaml(_monsterDiveYamlSync.Value, "source of truth changed to remote");
        }
    }

    private void OnMonsterDiveYamlValueChanged()
    {
        if (ConfigSync.IsSourceOfTruth)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_monsterDiveYamlSync.Value))
        {
            return;
        }

        ApplyMonsterDiveYaml(_monsterDiveYamlSync.Value, "synced value changed");
    }

    private bool LoadMonsterDiveYamlFromDisk(string reason)
    {
        lock (MonsterDiveYamlLock)
        {
            if (!File.Exists(MonsterDiveYamlFileFullPath))
            {
                string defaultYaml = BuildDefaultMonsterDiveYaml();
                Directory.CreateDirectory(Paths.ConfigPath);
                File.WriteAllText(MonsterDiveYamlFileFullPath, defaultYaml);
            }

            string yamlText = File.ReadAllText(MonsterDiveYamlFileFullPath);
            if (!ApplyMonsterDiveYaml(yamlText, reason))
            {
                return false;
            }

            if (_monsterDiveYamlSync.Value != yamlText)
            {
                _monsterDiveYamlSync.Value = yamlText;
            }

            return true;
        }
    }

    private static bool ApplyMonsterDiveYaml(string yamlText, string reason)
    {
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            ServerSyncModTemplateLogger.LogWarning($"Monster dive YAML is empty during {reason}. Keeping previous settings.");
            return false;
        }

        if (string.Equals(_lastAppliedMonsterDiveYamlText, yamlText, StringComparison.Ordinal))
        {
            return true;
        }

        MonsterDiveYamlRoot root;
        try
        {
            root = MonsterDiveYamlDeserializer.Deserialize<MonsterDiveYamlRoot>(yamlText) ?? new MonsterDiveYamlRoot();
        }
        catch (YamlException ex)
        {
            string location = ex.Start.Line > 0
                ? $" at line {ex.Start.Line}, column {ex.Start.Column}"
                : string.Empty;
            ServerSyncModTemplateLogger.LogError($"Failed to parse monster dive YAML during {reason}{location}: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            ServerSyncModTemplateLogger.LogError($"Failed to parse monster dive YAML during {reason}: {ex.Message}");
            return false;
        }

        Dictionary<string, MonsterDiveYamlGroup> definedGroups = GetDefinedGroups(root);
        Dictionary<string, ConfiguredDiveProfile> configuredProfilesByPrefabName = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, MonsterDiveYamlGroup> groupEntry in definedGroups)
        {
            string groupName = groupEntry.Key;
            MonsterDiveYamlGroup group = groupEntry.Value;
            if (!TryNormalizePassiveDepthProfile(
                    groupName,
                    group.PassiveMinDepth,
                    group.PassiveCenterDepth,
                    group.PassiveMaxDepth,
                    out PassiveDepthProfile passiveProfile) ||
                !TryNormalizeActiveMinDepth(groupName, group.ActiveMinDepth, out float activeMinDepth) ||
                !TryNormalizeActiveDepthAdjustSpeed(groupName, group.ActiveDepthAdjustSpeed, out float activeDepthAdjustSpeed) ||
                !TryNormalizeShallowWaterFleeDepth(groupName, group.ShallowWaterFleeDepth, out float shallowWaterFleeDepth))
            {
                return false;
            }

            bool preserveSpawnDepth = group.PreserveSpawnDepth ?? DefaultPreserveSpawnDepth;
            bool avoidanceSteering = group.AvoidanceSteering ?? DefaultAvoidanceSteering;
            ConfiguredDiveProfile configuredDiveProfile = new(groupName, passiveProfile, activeMinDepth, activeDepthAdjustSpeed, shallowWaterFleeDepth, preserveSpawnDepth, avoidanceSteering);
            AddYamlGroupEntries(configuredProfilesByPrefabName, group.Prefabs, configuredDiveProfile);
        }

        if (configuredProfilesByPrefabName.Count == 0)
        {
            ServerSyncModTemplateLogger.LogWarning($"Monster dive YAML loaded during {reason}, but no prefabs are assigned to any group.");
        }

        _configuredDiveProfilesByPrefabName = configuredProfilesByPrefabName;
        _lastAppliedMonsterDiveYamlText = yamlText;

        int restoredMonsterCount = RestoreRemovedMonsterDiveFlags();
        ClearSteeringMemory();
        ServerSyncModTemplateLogger.LogInfo(
            $"Loaded monster dive YAML ({reason}). passiveGroups={definedGroups.Count}, prefabs={configuredProfilesByPrefabName.Count}, active[defaultMin={DefaultActiveSwimDepthMin:F2}, max={ActiveSwimDepthMax:F2}, defaultAdjust={SwimDepthAdjustSpeed:F2}], shallowFleeDefault={DefaultShallowWaterFleeDepth:F2}, preserveSpawnDefault={DefaultPreserveSpawnDepth}, restoredRemovedInstances={restoredMonsterCount}.");
        return true;
    }

    private static bool TryNormalizeActiveMinDepth(
        string groupName,
        float? activeMinDepth,
        out float normalizedMinDepth)
    {
        if (!activeMinDepth.HasValue)
        {
            normalizedMinDepth = DefaultActiveSwimDepthMin;
            return true;
        }

        float requestedMinDepth = activeMinDepth.Value;
        if (!ValidateFiniteYamlNumber(groupName, "active_min_depth", requestedMinDepth))
        {
            normalizedMinDepth = default;
            return false;
        }

        normalizedMinDepth = Mathf.Clamp(requestedMinDepth, 0f, ActiveSwimDepthMax);
        if (!Mathf.Approximately(requestedMinDepth, normalizedMinDepth))
        {
            ServerSyncModTemplateLogger.LogWarning(
                $"Monster dive YAML normalized active profile '{groupName}': active_min_depth {requestedMinDepth.ToString("0.###", CultureInfo.InvariantCulture)} -> {normalizedMinDepth.ToString("0.###", CultureInfo.InvariantCulture)}.");
        }

        return true;
    }

    private static bool TryNormalizeActiveDepthAdjustSpeed(
        string groupName,
        float? activeDepthAdjustSpeed,
        out float normalizedAdjustSpeed)
    {
        if (!activeDepthAdjustSpeed.HasValue)
        {
            normalizedAdjustSpeed = SwimDepthAdjustSpeed;
            return true;
        }

        float requestedAdjustSpeed = activeDepthAdjustSpeed.Value;
        if (!ValidateFiniteYamlNumber(groupName, "active_depth_adjust_speed", requestedAdjustSpeed))
        {
            normalizedAdjustSpeed = default;
            return false;
        }

        normalizedAdjustSpeed = Mathf.Max(0f, requestedAdjustSpeed);
        if (!Mathf.Approximately(normalizedAdjustSpeed, requestedAdjustSpeed))
        {
            ServerSyncModTemplateLogger.LogWarning(
                $"Monster dive YAML normalized active profile '{groupName}': active_depth_adjust_speed {requestedAdjustSpeed.ToString("0.###", CultureInfo.InvariantCulture)} -> {normalizedAdjustSpeed.ToString("0.###", CultureInfo.InvariantCulture)}.");
        }

        return true;
    }

    private static bool TryNormalizeShallowWaterFleeDepth(
        string groupName,
        float? shallowWaterFleeDepth,
        out float normalizedFleeDepth)
    {
        if (!shallowWaterFleeDepth.HasValue)
        {
            normalizedFleeDepth = DefaultShallowWaterFleeDepth;
            return true;
        }

        float requestedFleeDepth = shallowWaterFleeDepth.Value;
        if (!ValidateFiniteYamlNumber(groupName, "shallow_water_flee_depth", requestedFleeDepth))
        {
            normalizedFleeDepth = default;
            return false;
        }

        normalizedFleeDepth = Mathf.Clamp(requestedFleeDepth, 0f, ActiveSwimDepthMax);
        if (!Mathf.Approximately(requestedFleeDepth, normalizedFleeDepth))
        {
            ServerSyncModTemplateLogger.LogWarning(
                $"Monster dive YAML normalized active profile '{groupName}': shallow_water_flee_depth {requestedFleeDepth.ToString("0.###", CultureInfo.InvariantCulture)} -> {normalizedFleeDepth.ToString("0.###", CultureInfo.InvariantCulture)}.");
        }

        return true;
    }

    private static bool TryNormalizePassiveDepthProfile(
        string groupName,
        float minDepth,
        float centerDepth,
        float maxDepth,
        out PassiveDepthProfile passiveDepthProfile)
    {
        if (!ValidateFiniteYamlNumber(groupName, "passive_min_depth", minDepth) ||
            !ValidateFiniteYamlNumber(groupName, "passive_center_depth", centerDepth) ||
            !ValidateFiniteYamlNumber(groupName, "passive_max_depth", maxDepth))
        {
            passiveDepthProfile = default;
            return false;
        }

        float requestedMin = minDepth;
        float requestedCenter = centerDepth;
        float requestedMax = maxDepth;
        float normalizedMin = Mathf.Clamp(requestedMin, 0f, ActiveSwimDepthMax);
        float normalizedMax = Mathf.Clamp(requestedMax, 0f, ActiveSwimDepthMax);
        if (normalizedMax < normalizedMin)
        {
            (normalizedMin, normalizedMax) = (normalizedMax, normalizedMin);
        }

        float normalizedCenter = Mathf.Clamp(requestedCenter, normalizedMin, normalizedMax);
        if (!Mathf.Approximately(normalizedMin, requestedMin) ||
            !Mathf.Approximately(normalizedMax, requestedMax) ||
            !Mathf.Approximately(normalizedCenter, requestedCenter))
        {
            ServerSyncModTemplateLogger.LogWarning(
                $"Monster dive YAML normalized passive profile '{groupName}': passive_min_depth {requestedMin.ToString("0.###", CultureInfo.InvariantCulture)} -> {normalizedMin.ToString("0.###", CultureInfo.InvariantCulture)}, " +
                $"passive_center_depth {requestedCenter.ToString("0.###", CultureInfo.InvariantCulture)} -> {normalizedCenter.ToString("0.###", CultureInfo.InvariantCulture)}, " +
                $"passive_max_depth {requestedMax.ToString("0.###", CultureInfo.InvariantCulture)} -> {normalizedMax.ToString("0.###", CultureInfo.InvariantCulture)}.");
        }

        passiveDepthProfile = new PassiveDepthProfile(normalizedCenter, normalizedMin, normalizedMax);
        return true;
    }

    private static bool ValidateFiniteYamlNumber(string groupName, string fieldName, float value)
    {
        if (!float.IsNaN(value) && !float.IsInfinity(value))
        {
            return true;
        }

        ServerSyncModTemplateLogger.LogError(
            $"Monster dive YAML contains non-finite {fieldName} in profile '{groupName}'. Keeping previous settings.");
        return false;
    }

    private static Dictionary<string, MonsterDiveYamlGroup> GetDefinedGroups(MonsterDiveYamlRoot root)
    {
        Dictionary<string, MonsterDiveYamlGroup> groups = new(StringComparer.OrdinalIgnoreCase);
        if (root.Count == 0)
        {
            return groups;
        }

        foreach (KeyValuePair<string, MonsterDiveYamlGroup> entry in root)
        {
            string groupName = entry.Key?.Trim() ?? string.Empty;
            if (groupName.Length == 0)
            {
                ServerSyncModTemplateLogger.LogWarning("Monster dive YAML contains an empty top-level group name. Skipping it.");
                continue;
            }

            groups[groupName] = entry.Value ?? new MonsterDiveYamlGroup();
        }

        return groups;
    }

    private static void AddYamlGroupEntries(Dictionary<string, ConfiguredDiveProfile> configuredProfilesByPrefabName, IEnumerable<string>? mobs, ConfiguredDiveProfile configuredDiveProfile)
    {
        if (mobs == null)
        {
            return;
        }

        foreach (string? rawMob in mobs)
        {
            string prefab = rawMob?.Trim() ?? string.Empty;
            if (prefab.Length == 0)
            {
                continue;
            }

            if (configuredProfilesByPrefabName.ContainsKey(prefab))
            {
                ServerSyncModTemplateLogger.LogWarning($"Monster dive YAML duplicate mob '{prefab}' found in {configuredDiveProfile.GroupName}. Keeping first assignment.");
                continue;
            }

            configuredProfilesByPrefabName[prefab] = configuredDiveProfile;
        }
    }

    private static string BuildDefaultMonsterDiveYaml()
    {
        StringBuilder builder = new();
        builder.AppendLine("# Monster dive configuration for DiveIn.");
        builder.AppendLine("# Unknown keys and duplicate keys are treated as errors and keep the previous applied settings.");
        builder.AppendLine();
        AppendDefaultGroup(builder, "surface_patrol", 0f, 10f, 20f, SwimDepthAdjustSpeed, includeGroupHeaderComment: true, includeFieldComments: true, examplePrefabs: new[]
        {
            "Leech",
            "Abomination",
            "Serpent",
            "BonemawSerpent"
        });
        builder.AppendLine();
        AppendDefaultGroup(builder, "mid_water", 0f, 15f, 30f, SwimDepthAdjustSpeed);
        builder.AppendLine();
        AppendDefaultGroup(builder, "deep_patrol", 10f, 20f, 30f, SwimDepthAdjustSpeed);
        builder.AppendLine();
        builder.AppendLine("## Mod prefabs sample");
        builder.AppendLine();
        AppendDefaultGroup(builder, "mods_surface", 0f, 10f, 20f, SwimDepthAdjustSpeed, examplePrefabs: new[]
        {
            "Neck_RtD",
            "Animal_Dolphin_RtD",
            "Animal_Cod_RtD",
            "Monster_GreatWhiteShark_RtD",
            "Animal_Turtle_RtD",
            "Mirmaid_RtD",
            "BoneFish_RtD",
            "BoneSquid_RtD",
            "LuminousLooker_RtD",
            "MurkPod_RtD",
            "Animal_HumpbackWhale_RtD",
            "RDB_crocodile",
            "RDB_white_shark",
            "RDB_turtle",
            "Shark_TW",
            "ArcticSerpent_TW",
            "SA_Orca",
            "SA_Dolphin",
            "SA_HumboldtSquid",
            "SA_LeatherbackSeaTurtle",
            "SA_RightWhale",
            "SA_WhaleShark",
            "SA_BlueShark",
            "SA_HammerHeadShark",
            "SA_TigerShark",
            "SA_BlueTurtle",
            "SA_GreenTurtle",
            "SA_RedTurtle",
            "SA_YellowTurtle"
        });
        builder.AppendLine();
        builder.AppendLine("# Disable angled avoidance only for prefabs that jitter while diving.");
        AppendDefaultGroup(builder, "mods_surface_nosteering", 0f, 10f, 20f, SwimDepthAdjustSpeed, avoidanceSteering: false, examplePrefabs: new[]
        {
            "SA_WhiteShark"
        });
        builder.AppendLine();
        AppendDefaultGroup(builder, "mods_midwater", 0f, 15f, 30f, SwimDepthAdjustSpeed, examplePrefabs: new[]
        {
            "Belzor_RtD",
            "Monster_HammerheadShark_RtD",
            "Animal_Marlin_RtD",
            "Shark_RtD",
            "Animal_SpermWhale_RtD",
            "Monster_Orca_RtD"
        });
        builder.AppendLine();
        AppendDefaultGroup(builder, "mods_deep", 10f, 20f, 30f, SwimDepthAdjustSpeed, examplePrefabs: new[]
        {
            "Animal_Tuna_RtD",
            "Animal_Squid_RtD"
        });
        builder.AppendLine();
        AppendDefaultGroup(builder, "mods_bottom", 20f, 30f, 40f, SwimDepthAdjustSpeed, examplePrefabs: new[]
        {
            "CatFish_RtD",
            "Reptile_RtD",
            "MirRake_RtD",
            "Animal_Manta_RtD"
        });
        return builder.ToString();
    }

    private static void AppendDefaultGroup(StringBuilder builder, string groupName, float minDepth, float centerDepth, float maxDepth, float activeDepthAdjustSpeed, bool includeGroupHeaderComment = false, bool includeFieldComments = false, bool avoidanceSteering = DefaultAvoidanceSteering, IEnumerable<string>? examplePrefabs = null)
    {
        string groupHeaderComment = includeGroupHeaderComment
            ? " # You can use any group name. Add your own groups"
            : string.Empty;
        string minDepthComment = includeFieldComments ? " # Shallowest passive dive depth used while the monster has no target and is not alerted." : string.Empty;
        string centerDepthComment = includeFieldComments ? " # Center depth used by the passive sine-wave swimming pattern." : string.Empty;
        string maxDepthComment = includeFieldComments ? " # Deepest passive dive depth used while the monster has no target and is not alerted." : string.Empty;
        string activeMinDepthComment = includeFieldComments ? " # Shallowest active target depth used while alerted or chasing a target." : string.Empty;
        string activeAdjustComment = includeFieldComments ? " # How quickly this group adjusts swim depth while alerted or chasing a target." : string.Empty;
        string shallowFleeComment = includeFieldComments ? " # Current terrain water depth below this value makes the monster flee from its target. 0 disables it." : string.Empty;
        string preserveSpawnComment = includeFieldComments ? " # If true, monsters spawned underwater keep their initial spawn depth instead of surfacing first." : string.Empty;
        string avoidanceSteeringComment = includeFieldComments ? " # If false, skips DiveIn's angled obstacle avoidance and swims directly toward the current target." : string.Empty;
        string prefabsComment = includeFieldComments ? " # Monster prefab names assigned to this passive profile group." : string.Empty;
        builder.AppendLine($"{groupName}:{groupHeaderComment}");
        builder.AppendLine($"  passive_min_depth: {FormatYamlFloat(minDepth)}{minDepthComment}");
        builder.AppendLine($"  passive_center_depth: {FormatYamlFloat(centerDepth)}{centerDepthComment}");
        builder.AppendLine($"  passive_max_depth: {FormatYamlFloat(maxDepth)}{maxDepthComment}");
        builder.AppendLine($"  active_min_depth: {FormatYamlFloat(DefaultActiveSwimDepthMin)}{activeMinDepthComment}");
        builder.AppendLine($"  active_depth_adjust_speed: {FormatYamlFloat(activeDepthAdjustSpeed)}{activeAdjustComment}");
        builder.AppendLine($"  shallow_water_flee_depth: {FormatYamlFloat(DefaultShallowWaterFleeDepth)}{shallowFleeComment}");
        builder.AppendLine($"  preserve_spawn_depth: {FormatYamlBool(DefaultPreserveSpawnDepth)}{preserveSpawnComment}");
        builder.AppendLine($"  avoidance_steering: {FormatYamlBool(avoidanceSteering)}{avoidanceSteeringComment}");
        if (examplePrefabs != null)
        {
            string[] prefabArray = examplePrefabs.Where(static prefab => !string.IsNullOrWhiteSpace(prefab)).ToArray();
            if (prefabArray.Length > 0)
            {
                builder.AppendLine($"  prefabs:{prefabsComment}");
                foreach (string prefab in prefabArray)
                {
                    builder.AppendLine($"    - {prefab}");
                }

                return;
            }
        }

        builder.AppendLine($"  prefabs: []{prefabsComment}");
    }

    private static string FormatYamlFloat(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string FormatYamlBool(bool value)
    {
        return value ? "true" : "false";
    }
}
