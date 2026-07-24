using HarmonyLib;
using UnityEngine;

namespace ServerSyncModTemplate;

public partial class ServerSyncModTemplatePlugin
{
    [HarmonyPatch(typeof(BaseAI), "OnDestroy")]
    private static class BaseAIOnDestroyPatch
    {
        private static void Postfix(BaseAI __instance)
        {
            if (__instance is not MonsterAI monsterAI)
            {
                return;
            }

            int instanceId = monsterAI.GetInstanceID();
            SteeringMemory.Remove(instanceId);
            if (OriginalDiveFlagsByInstance.TryGetValue(instanceId, out OriginalDiveFlags originalFlags) &&
                object.ReferenceEquals(originalFlags.MonsterAI, monsterAI))
            {
                RemoveTrackedMonsterState(instanceId);
            }
        }
    }

    [HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.Awake))]
    private static class MonsterAIAwakePatch
    {
        private static void Postfix(MonsterAI __instance)
        {
            if (!TryGetConfiguredDiveProfile(__instance, out ConfiguredDiveProfile configuredDiveProfile))
            {
                return;
            }

            EnsureDiveFlags(__instance, configuredDiveProfile);
        }
    }

    [HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.UpdateAI))]
    private static class MonsterAIUpdateAIPatch
    {
        private static void Prefix(MonsterAI __instance, out ShallowWaterFleeRequest __state)
        {
            __state = default;
            if (!TryGetConfiguredDiveProfile(__instance, out ConfiguredDiveProfile configuredDiveProfile))
            {
                return;
            }

            EnsureDiveFlags(__instance, configuredDiveProfile);
            __state = GetShallowWaterFleeRequest(__instance, configuredDiveProfile);
        }

        private static void Postfix(
            MonsterAI __instance,
            float dt,
            ShallowWaterFleeRequest __state,
            ref bool __result)
        {
            if (!__state.ShouldFlee
                || !__result)
            {
                return;
            }

            if (HasVanillaMonsterAIPriority(__instance))
            {
                ShallowWaterFleeingByInstance.Remove(__instance.GetInstanceID());
                return;
            }

            ApplyShallowWaterFlee(__instance, dt, __state);
            __result = true;
        }
    }

    [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.HavePath))]
    private static class BaseAIHavePathPatch
    {
        private static bool Prefix(BaseAI __instance, Vector3 target, ref bool __result)
        {
            if (!TryGetConfiguredMonster(
                    __instance,
                    out MonsterAI monsterAI,
                    out ConfiguredDiveProfile configuredDiveProfile) ||
                !ShouldUseWaterDiveMode(monsterAI))
            {
                return true;
            }

            Character character = monsterAI.m_character;
            if (character == null)
            {
                return true;
            }

            __result = BuildUnderwaterNavigationPlan(__instance, character, target, configuredDiveProfile).HasRoute;
            return false;
        }
    }

    [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.MoveTo))]
    private static class BaseAIMoveToPatch
    {
        private static bool Prefix(BaseAI __instance, float dt, Vector3 point, float dist, bool run, ref bool __result)
        {
            if (!TryGetConfiguredMonster(
                    __instance,
                    out MonsterAI monsterAI,
                    out ConfiguredDiveProfile configuredDiveProfile) ||
                !ShouldUseWaterDiveMode(monsterAI))
            {
                return true;
            }

            Character character = monsterAI.m_character;
            if (character == null)
            {
                return true;
            }

            SwimDepthGoal depthGoal = CalculateSwimDepthGoal(monsterAI, character, point, configuredDiveProfile);
            UnderwaterNavigationPlan navigationPlan =
                BuildUnderwaterNavigationPlan(__instance, character, point, configuredDiveProfile);
            ApplySwimDepthGoal(character, depthGoal, dt);

            float minimumStopDistance = monsterAI.m_serpentMovement ? 3f : run ? 1f : 0.5f;
            float stopDist = Mathf.Max(dist, minimumStopDistance);
            float horizontalDist = Utils.DistanceXZ(point, __instance.transform.position);
            float verticalToRequested = Mathf.Abs(point.y - __instance.transform.position.y);
            float verticalToClamped = Mathf.Abs(depthGoal.ClampedTargetY - __instance.transform.position.y);
            bool verticalReached = verticalToRequested < 0.75f || (depthGoal.RequestedOutsideRange && verticalToClamped < 0.35f);
            if (horizontalDist < stopDist && (monsterAI.m_serpentMovement || verticalReached))
            {
                __instance.StopMoving();
                __result = true;
                return false;
            }

            if (navigationPlan.Direction.sqrMagnitude <= 0.0001f)
            {
                __instance.StopMoving();
                __result = true;
                return false;
            }

            if (monsterAI.m_serpentMovement)
            {
                __instance.MoveTowardsSwoop(navigationPlan.Direction, run, navigationPlan.LookaheadDistance);
            }
            else
            {
                __instance.MoveTowards(navigationPlan.Direction, run);
            }

            __result = false;
            return false;
        }
    }
}
