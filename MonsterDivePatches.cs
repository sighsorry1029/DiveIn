using HarmonyLib;
using UnityEngine;

namespace ServerSyncModTemplate;

public partial class ServerSyncModTemplatePlugin
{
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
        }
    }

    [HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.UpdateAI))]
    private static class MonsterAIUpdateAIPatch
    {
        private static bool Prefix(MonsterAI __instance, float dt, ref bool __result)
        {
            if (!IsConfiguredMonster(__instance))
            {
                return true;
            }

            EnsureDiveFlags(__instance);
            if (!TryFleeFromShallowWater(__instance, dt))
            {
                return true;
            }

            __result = true;
            return false;
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

            __result = BuildUnderwaterNavigationPlan(__instance, character, target).HasRoute;
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

            SwimDepthGoal depthGoal = CalculateSwimDepthGoal(monsterAI, character, point);
            UnderwaterNavigationPlan navigationPlan = BuildUnderwaterNavigationPlan(__instance, character, point);
            ApplySwimDepthGoal(character, depthGoal, dt);

            float stopDist = Mathf.Max(dist, run ? 1f : 0.5f);
            float horizontalDist = Utils.DistanceXZ(point, __instance.transform.position);
            float verticalToRequested = Mathf.Abs(point.y - __instance.transform.position.y);
            float verticalToClamped = Mathf.Abs(depthGoal.ClampedTargetY - __instance.transform.position.y);
            bool verticalReached = verticalToRequested < 0.75f || (depthGoal.RequestedOutsideRange && verticalToClamped < 0.35f);
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

            if (navigationPlan.Direction.sqrMagnitude <= 0.0001f)
            {
                __instance.StopMoving();
                __result = true;
                return false;
            }

            __instance.MoveTowards(navigationPlan.Direction, run);
            __result = false;
            return false;
        }
    }
}
