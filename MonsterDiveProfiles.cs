using System;
using System.Collections.Generic;
using UnityEngine;

namespace ServerSyncModTemplate;

public partial class ServerSyncModTemplatePlugin
{
    private static IReadOnlyDictionary<string, ConfiguredDiveProfile> _configuredDiveProfilesByPrefabName =
        new Dictionary<string, ConfiguredDiveProfile>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, OriginalDiveFlags> OriginalDiveFlagsByInstance = new();
    private static readonly HashSet<int> InitialSpawnDepthPreservedByInstance = new();
    private static readonly HashSet<int> ShallowWaterFleeingByInstance = new();
    private const int MaxCacheEntries = 2048;
    private const bool DefaultPreserveSpawnDepth = false;
    private const float ShallowWaterFleeExitBuffer = 1f;
    private const float ShallowWaterRetargetDelay = 3f;

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
        public readonly float ActiveMinDepth;
        public readonly float ActiveDepthAdjustSpeed;
        public readonly float ShallowWaterFleeDepth;
        public readonly bool PreserveSpawnDepth;

        public ConfiguredDiveProfile(string groupName, PassiveDepthProfile passiveDepthProfile, float activeMinDepth, float activeDepthAdjustSpeed, float shallowWaterFleeDepth, bool preserveSpawnDepth)
        {
            GroupName = groupName;
            PassiveDepthProfile = passiveDepthProfile;
            ActiveMinDepth = activeMinDepth;
            ActiveDepthAdjustSpeed = activeDepthAdjustSpeed;
            ShallowWaterFleeDepth = shallowWaterFleeDepth;
            PreserveSpawnDepth = preserveSpawnDepth;
        }
    }

    private readonly struct OriginalDiveFlags
    {
        public readonly MonsterAI MonsterAI;
        public readonly bool AvoidWater;
        public readonly bool AvoidLand;
        public readonly bool CanSwim;

        public OriginalDiveFlags(MonsterAI monsterAI, bool avoidWater, bool avoidLand, bool canSwim)
        {
            MonsterAI = monsterAI;
            AvoidWater = avoidWater;
            AvoidLand = avoidLand;
            CanSwim = canSwim;
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

    private static bool TryFleeFromShallowWater(MonsterAI monsterAI, float dt)
    {
        if (!ShouldFleeFromShallowWater(monsterAI))
        {
            return false;
        }

        Vector3 fleeFrom = monsterAI.m_targetCreature != null
            ? monsterAI.m_targetCreature.transform.position
            : monsterAI.m_lastKnownTargetPos;
        if (monsterAI.m_targetCreature != null)
        {
            monsterAI.m_lastKnownTargetPos = fleeFrom;
        }

        monsterAI.m_targetCreature = null;
        monsterAI.m_targetStatic = null;
        monsterAI.m_updateTargetTimer = Mathf.Max(monsterAI.m_updateTargetTimer, ShallowWaterRetargetDelay);
        monsterAI.Flee(dt, fleeFrom);
        return true;
    }

    private static bool ShouldFleeFromShallowWater(MonsterAI monsterAI)
    {
        if (monsterAI == null || monsterAI.m_character == null || !TryGetConfiguredDiveProfile(monsterAI, out ConfiguredDiveProfile profile))
        {
            if (monsterAI != null)
            {
                ShallowWaterFleeingByInstance.Remove(monsterAI.GetInstanceID());
            }

            return false;
        }

        float shallowWaterFleeDepth = profile.ShallowWaterFleeDepth;
        if (shallowWaterFleeDepth <= 0f)
        {
            ShallowWaterFleeingByInstance.Remove(monsterAI.GetInstanceID());
            return false;
        }

        if (monsterAI.m_nview == null || !monsterAI.m_nview.IsOwner())
        {
            return false;
        }

        if (IsPassiveDiveState(monsterAI) || !ShouldUseWaterDiveMode(monsterAI))
        {
            return false;
        }

        int instanceId = monsterAI.GetInstanceID();
        if (!TryGetTerrainWaterDepth(monsterAI.m_character, out float terrainWaterDepth))
        {
            ShallowWaterFleeingByInstance.Remove(instanceId);
            return false;
        }

        if (terrainWaterDepth < shallowWaterFleeDepth)
        {
            ShallowWaterFleeingByInstance.Add(instanceId);
            return true;
        }

        if (!ShallowWaterFleeingByInstance.Contains(instanceId))
        {
            return false;
        }

        float exitDepth = shallowWaterFleeDepth + Mathf.Max(ShallowWaterFleeExitBuffer, shallowWaterFleeDepth * 0.2f);
        if (terrainWaterDepth < exitDepth)
        {
            return true;
        }

        ShallowWaterFleeingByInstance.Remove(instanceId);
        monsterAI.m_updateTargetTimer = Mathf.Max(monsterAI.m_updateTargetTimer, ShallowWaterRetargetDelay);
        return false;
    }

    private static bool TryGetTerrainWaterDepth(Character character, out float terrainWaterDepth)
    {
        terrainWaterDepth = 0f;
        if (character == null || !character.InWater() || ZoneSystem.instance == null)
        {
            return false;
        }

        float liquidLevel = character.GetLiquidLevel();
        float solidHeight = ZoneSystem.instance.GetSolidHeight(character.transform.position);
        if (solidHeight >= liquidLevel)
        {
            return false;
        }

        terrainWaterDepth = liquidLevel - solidHeight;
        return true;
    }

    private static void EnsureDiveFlags(MonsterAI monsterAI)
    {
        TrackOriginalDiveFlags(monsterAI);
        if (TryGetConfiguredDiveProfile(monsterAI, out ConfiguredDiveProfile profile))
        {
            PreserveInitialUnderwaterSpawnDepth(monsterAI, profile);
        }

        if (monsterAI.m_avoidWater)
        {
            monsterAI.m_avoidWater = false;
        }

        EnsureAvoidLandForCurrentDiveState(monsterAI);

        Character character = monsterAI.m_character;
        if (character != null && !character.m_canSwim)
        {
            character.m_canSwim = true;
        }
    }

    private static void PreserveInitialUnderwaterSpawnDepth(MonsterAI monsterAI, ConfiguredDiveProfile profile)
    {
        if (monsterAI == null || monsterAI.m_character == null)
        {
            return;
        }

        int instanceId = monsterAI.GetInstanceID();
        if (!profile.PreserveSpawnDepth)
        {
            return;
        }

        if (InitialSpawnDepthPreservedByInstance.Contains(instanceId))
        {
            return;
        }

        InitialSpawnDepthPreservedByInstance.Add(instanceId);
        Character character = monsterAI.m_character;
        if (!TryGetCurrentWaterDepth(character, out float currentWaterDepth))
        {
            return;
        }

        if (currentWaterDepth > character.m_swimDepth)
        {
            character.m_swimDepth = currentWaterDepth;
        }
    }

    private static bool TryGetCurrentWaterDepth(Character character, out float currentWaterDepth)
    {
        currentWaterDepth = 0f;
        if (character == null)
        {
            return false;
        }

        Vector3 position = character.transform.position;
        float liquidLevel = Floating.GetLiquidLevel(position, 1f, LiquidType.Water);
        if (liquidLevel <= -10000f)
        {
            liquidLevel = character.GetLiquidLevel();
        }

        if (liquidLevel <= -10000f || position.y >= liquidLevel)
        {
            return false;
        }

        currentWaterDepth = liquidLevel - position.y;
        return currentWaterDepth > 0f;
    }

    private static void EnsureAvoidLandForCurrentDiveState(MonsterAI monsterAI)
    {
        bool underwaterMode = ShouldUseWaterDiveMode(monsterAI);
        if (underwaterMode)
        {
            if (monsterAI.m_avoidLand)
            {
                monsterAI.m_avoidLand = false;
            }

            return;
        }

        int instanceId = monsterAI.GetInstanceID();
        if (OriginalDiveFlagsByInstance.TryGetValue(instanceId, out OriginalDiveFlags originalFlags) &&
            monsterAI.m_avoidLand != originalFlags.AvoidLand)
        {
            monsterAI.m_avoidLand = originalFlags.AvoidLand;
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
            monsterAI.m_avoidLand,
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
                InitialSpawnDepthPreservedByInstance.Remove(instanceId);
                ShallowWaterFleeingByInstance.Remove(instanceId);
                continue;
            }

            if (IsConfiguredMonster(monsterAI))
            {
                continue;
            }

            RestoreOriginalDiveFlags(originalFlags);
            instanceIdsToRemove.Add(instanceId);
            InitialSpawnDepthPreservedByInstance.Remove(instanceId);
            ShallowWaterFleeingByInstance.Remove(instanceId);
            restoredCount++;
        }

        foreach (int instanceId in instanceIdsToRemove)
        {
            OriginalDiveFlagsByInstance.Remove(instanceId);
        }

        return restoredCount;
    }

    private static int RestoreAllTrackedMonsterDiveFlags()
    {
        if (OriginalDiveFlagsByInstance.Count == 0)
        {
            return 0;
        }

        int restoredCount = 0;
        foreach (OriginalDiveFlags originalFlags in OriginalDiveFlagsByInstance.Values)
        {
            if (!originalFlags.MonsterAI)
            {
                continue;
            }

            RestoreOriginalDiveFlags(originalFlags);
            restoredCount++;
        }

        OriginalDiveFlagsByInstance.Clear();
        InitialSpawnDepthPreservedByInstance.Clear();
        ShallowWaterFleeingByInstance.Clear();
        return restoredCount;
    }

    private static void RestoreOriginalDiveFlags(OriginalDiveFlags originalFlags)
    {
        MonsterAI monsterAI = originalFlags.MonsterAI;
        if (!monsterAI)
        {
            return;
        }

        monsterAI.m_avoidWater = originalFlags.AvoidWater;
        monsterAI.m_avoidLand = originalFlags.AvoidLand;
        Character character = monsterAI.m_character;
        if (character != null)
        {
            character.m_canSwim = originalFlags.CanSwim;
        }
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
                InitialSpawnDepthPreservedByInstance.Remove(instanceId);
                ShallowWaterFleeingByInstance.Remove(instanceId);
            }
        }

        foreach (int instanceId in instanceIdsToRemove)
        {
            OriginalDiveFlagsByInstance.Remove(instanceId);
        }
    }
}
