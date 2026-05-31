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
        public float? ActiveDepthAdjustSpeed { get; set; }
        public List<string> Prefabs { get; set; } = new();
    }

    private FileSystemWatcher _monsterDiveYamlWatcher = null!;
    private DateTime _lastMonsterDiveYamlReloadTime;
    private static CustomSyncedValue<string> _monsterDiveYamlSync = null!;

    private void InitializeMonsterDiveYaml()
    {
        _monsterDiveYamlSync = new CustomSyncedValue<string>(ConfigSync, "MonsterDiveYaml", string.Empty, 0);
        _monsterDiveYamlSync.ValueChanged += OnMonsterDiveYamlValueChanged;
        ConfigSync.SourceOfTruthChanged += OnMonsterDiveSourceOfTruthChanged;

        if (ConfigSync.IsSourceOfTruth)
        {
            LoadMonsterDiveYamlFromDisk(forceWriteDefaultIfMissing: true, syncToPeers: true, reason: "startup");
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
        _monsterDiveYamlWatcher.IncludeSubdirectories = true;
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
        DateTime now = DateTime.Now;
        if (now.Ticks - _lastMonsterDiveYamlReloadTime.Ticks < RELOAD_DELAY)
        {
            return;
        }

        _lastMonsterDiveYamlReloadTime = now;
        lock (_reloadLock)
        {
            if (!ConfigSync.IsSourceOfTruth)
            {
                ServerSyncModTemplateLogger.LogInfo("Ignoring local monster dive YAML reload because remote synced values are active.");
                return;
            }

            try
            {
                LoadMonsterDiveYamlFromDisk(forceWriteDefaultIfMissing: true, syncToPeers: true, reason: "yaml reload");
                ServerSyncModTemplateLogger.LogInfo("Monster dive YAML reload complete.");
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
            LoadMonsterDiveYamlFromDisk(forceWriteDefaultIfMissing: true, syncToPeers: true, reason: "source of truth changed to local");
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

    private void LoadMonsterDiveYamlFromDisk(bool forceWriteDefaultIfMissing, bool syncToPeers, string reason)
    {
        lock (MonsterDiveYamlLock)
        {
            if (!File.Exists(MonsterDiveYamlFileFullPath))
            {
                if (!forceWriteDefaultIfMissing)
                {
                    return;
                }

                string defaultYaml = BuildDefaultMonsterDiveYaml();
                Directory.CreateDirectory(Paths.ConfigPath);
                File.WriteAllText(MonsterDiveYamlFileFullPath, defaultYaml);
            }

            string yamlText = File.ReadAllText(MonsterDiveYamlFileFullPath);
            if (!ApplyMonsterDiveYaml(yamlText, reason))
            {
                return;
            }

            if (syncToPeers && _monsterDiveYamlSync.Value != yamlText)
            {
                _monsterDiveYamlSync.Value = yamlText;
            }
        }
    }

    private static bool ApplyMonsterDiveYaml(string yamlText, string reason)
    {
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            ServerSyncModTemplateLogger.LogWarning($"Monster dive YAML is empty during {reason}. Keeping previous settings.");
            return false;
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
            PassiveDepthProfile passiveProfile = NormalizePassiveDepthProfile(groupName, group.PassiveMinDepth, group.PassiveCenterDepth, group.PassiveMaxDepth);
            float activeDepthAdjustSpeed = NormalizeActiveDepthAdjustSpeed(groupName, group.ActiveDepthAdjustSpeed);
            ConfiguredDiveProfile configuredDiveProfile = new(groupName, passiveProfile, activeDepthAdjustSpeed);
            AddYamlGroupEntries(configuredProfilesByPrefabName, group.Prefabs, configuredDiveProfile);
        }

        if (configuredProfilesByPrefabName.Count == 0)
        {
            ServerSyncModTemplateLogger.LogWarning($"Monster dive YAML loaded during {reason}, but no prefabs are assigned to any group.");
        }

        lock (PrefabSetLock)
        {
            _configuredDiveProfilesByPrefabName = configuredProfilesByPrefabName;
        }

        int restoredMonsterCount = RestoreRemovedMonsterDiveFlags();
        ClearRuntimeCaches();
        ServerSyncModTemplateLogger.LogInfo(
            $"Loaded monster dive YAML ({reason}). passiveGroups={definedGroups.Count}, prefabs={configuredProfilesByPrefabName.Count}, active[min={ActiveSwimDepthMin:F2}, max={ActiveSwimDepthMax:F2}, defaultAdjust={SwimDepthAdjustSpeed:F2}], restoredRemovedInstances={restoredMonsterCount}.");
        return true;
    }

    private static float NormalizeActiveDepthAdjustSpeed(string groupName, float? activeDepthAdjustSpeed)
    {
        if (!activeDepthAdjustSpeed.HasValue)
        {
            return SwimDepthAdjustSpeed;
        }

        float requestedAdjustSpeed = activeDepthAdjustSpeed.Value;
        float normalizedAdjustSpeed = Mathf.Max(0f, requestedAdjustSpeed);
        if (!Mathf.Approximately(normalizedAdjustSpeed, requestedAdjustSpeed))
        {
            ServerSyncModTemplateLogger.LogWarning(
                $"Monster dive YAML normalized active profile '{groupName}': active_depth_adjust_speed {requestedAdjustSpeed.ToString("0.###", CultureInfo.InvariantCulture)} -> {normalizedAdjustSpeed.ToString("0.###", CultureInfo.InvariantCulture)}.");
        }

        return normalizedAdjustSpeed;
    }

    private static PassiveDepthProfile NormalizePassiveDepthProfile(string groupName, float minDepth, float centerDepth, float maxDepth)
    {
        float requestedMin = minDepth;
        float requestedCenter = centerDepth;
        float requestedMax = maxDepth;
        float normalizedMin = Mathf.Max(0f, requestedMin);
        float normalizedMax = Mathf.Max(0f, requestedMax);
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

        return new PassiveDepthProfile(normalizedCenter, normalizedMin, normalizedMax);
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
            "SA_WhiteShark",
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

    private static void AppendDefaultGroup(StringBuilder builder, string groupName, float minDepth, float centerDepth, float maxDepth, float activeDepthAdjustSpeed, bool includeGroupHeaderComment = false, bool includeFieldComments = false, IEnumerable<string>? examplePrefabs = null)
    {
        string groupHeaderComment = includeGroupHeaderComment
            ? " # You can use any group name. Add your own groups"
            : string.Empty;
        string minDepthComment = includeFieldComments ? " # Shallowest passive dive depth used while the monster has no target and is not alerted." : string.Empty;
        string centerDepthComment = includeFieldComments ? " # Center depth used by the passive sine-wave swimming pattern." : string.Empty;
        string maxDepthComment = includeFieldComments ? " # Deepest passive dive depth used while the monster has no target and is not alerted." : string.Empty;
        string activeAdjustComment = includeFieldComments ? " # How quickly this group adjusts swim depth while alerted or chasing a target." : string.Empty;
        string prefabsComment = includeFieldComments ? " # Monster prefab names assigned to this passive profile group." : string.Empty;
        builder.AppendLine($"{groupName}:{groupHeaderComment}");
        builder.AppendLine($"  passive_min_depth: {FormatYamlFloat(minDepth)}{minDepthComment}");
        builder.AppendLine($"  passive_center_depth: {FormatYamlFloat(centerDepth)}{centerDepthComment}");
        builder.AppendLine($"  passive_max_depth: {FormatYamlFloat(maxDepth)}{maxDepthComment}");
        builder.AppendLine($"  active_depth_adjust_speed: {FormatYamlFloat(activeDepthAdjustSpeed)}{activeAdjustComment}");
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
}
