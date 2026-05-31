using System;
using System.Collections.Generic;
using UnityEngine;

namespace ServerSyncModTemplate;

public partial class ServerSyncModTemplatePlugin
{
    private static readonly object PrefabSetLock = new();
    private static Dictionary<string, ConfiguredDiveProfile> _configuredDiveProfilesByPrefabName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, OriginalDiveFlags> OriginalDiveFlagsByInstance = new();
    private const int MaxCacheEntries = 2048;

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
        public readonly float ActiveDepthAdjustSpeed;

        public ConfiguredDiveProfile(string groupName, PassiveDepthProfile passiveDepthProfile, float activeDepthAdjustSpeed)
        {
            GroupName = groupName;
            PassiveDepthProfile = passiveDepthProfile;
            ActiveDepthAdjustSpeed = activeDepthAdjustSpeed;
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

    private static void EnsureDiveFlags(MonsterAI monsterAI)
    {
        TrackOriginalDiveFlags(monsterAI);

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
                continue;
            }

            if (IsConfiguredMonster(monsterAI))
            {
                continue;
            }

            RestoreOriginalDiveFlags(originalFlags);
            instanceIdsToRemove.Add(instanceId);
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
            }
        }

        foreach (int instanceId in instanceIdsToRemove)
        {
            OriginalDiveFlagsByInstance.Remove(instanceId);
        }
    }
}
