using System.Collections.Generic;
using UnityEngine;

namespace ServerSyncModTemplate;

public partial class ServerSyncModTemplatePlugin
{
    private const float PassiveWavePeriodSeconds = 12f;
    private const float ActiveSwimDepthMin = 0.25f;
    private const float ActiveSwimDepthMax = 300f;
    private const float SwimDepthAdjustSpeed = 2f;
    private const float MovePlanCacheSeconds = 0.1f;
    private const float MovePlanCacheCellSize = 0.1f;
    private const int AvoidanceSampleCount = 8;
    private static readonly float[] SteerAngles = { 0f, -35f, 35f, -70f, 70f, -120f, 120f, 180f };
    private static readonly Dictionary<int, MovePlanCacheEntry> MovePlanCache = new();

    private readonly struct MovePlanCacheEntry
    {
        public readonly float Time;
        public readonly Vector3Int PositionBucket;
        public readonly Vector3Int TargetBucket;
        public readonly bool HasRoute;
        public readonly Vector3 Direction;

        public MovePlanCacheEntry(float time, Vector3Int positionBucket, Vector3Int targetBucket, bool hasRoute, Vector3 direction)
        {
            Time = time;
            PositionBucket = positionBucket;
            TargetBucket = targetBucket;
            HasRoute = hasRoute;
            Direction = direction;
        }
    }

    private readonly struct UnderwaterNavigationPlan
    {
        public readonly bool HasRoute;
        public readonly Vector3 Direction;

        public UnderwaterNavigationPlan(bool hasRoute, Vector3 direction)
        {
            HasRoute = hasRoute;
            Direction = direction;
        }
    }

    private readonly struct SwimDepthGoal
    {
        public readonly float DesiredDepth;
        public readonly float ClampedTargetY;
        public readonly bool RequestedOutsideRange;
        public readonly float AdjustSpeed;

        public SwimDepthGoal(float desiredDepth, float clampedTargetY, bool requestedOutsideRange, float adjustSpeed)
        {
            DesiredDepth = desiredDepth;
            ClampedTargetY = clampedTargetY;
            RequestedOutsideRange = requestedOutsideRange;
            AdjustSpeed = adjustSpeed;
        }
    }

    private static Vector3Int ToCacheBucket(Vector3 value)
    {
        return new Vector3Int(
            Mathf.RoundToInt(value.x / MovePlanCacheCellSize),
            Mathf.RoundToInt(value.y / MovePlanCacheCellSize),
            Mathf.RoundToInt(value.z / MovePlanCacheCellSize));
    }

    private static void ClearRuntimeCaches()
    {
        MovePlanCache.Clear();
    }

    private static void TrimCachesIfNeeded()
    {
        if (MovePlanCache.Count > MaxCacheEntries)
        {
            MovePlanCache.Clear();
        }
    }

    private static float GetPassiveDesiredDepth(MonsterAI monsterAI, PassiveDepthProfile profile)
    {
        int instanceId = Mathf.Abs(monsterAI.GetInstanceID());
        float phasedTime = Time.time + (instanceId % 997) * 0.173f;
        float wave = Mathf.Sin(Mathf.Repeat(phasedTime, PassiveWavePeriodSeconds) / PassiveWavePeriodSeconds * Mathf.PI * 2f);
        float surfaceAmplitude = Mathf.Max(0f, profile.CenterDepth - profile.MinDepth);
        float deepAmplitude = Mathf.Max(0f, profile.MaxDepth - profile.CenterDepth);
        return wave >= 0f
            ? profile.CenterDepth + wave * deepAmplitude
            : profile.CenterDepth + wave * surfaceAmplitude;
    }

    private static SwimDepthGoal CalculateSwimDepthGoal(MonsterAI monsterAI, Character character, Vector3 point)
    {
        TryGetConfiguredDiveProfile(monsterAI, out ConfiguredDiveProfile configuredDiveProfile);
        float liquidLevel = character.GetLiquidLevel();
        bool passiveDive = IsPassiveDiveState(monsterAI);
        float desiredDepth;
        bool requestedOutsideRange;
        float adjustSpeed;
        if (passiveDive)
        {
            desiredDepth = GetPassiveDesiredDepth(monsterAI, configuredDiveProfile.PassiveDepthProfile);
            requestedOutsideRange = true;
            adjustSpeed = SwimDepthAdjustSpeed;
        }
        else
        {
            float requestedDepth = liquidLevel - point.y;
            desiredDepth = Mathf.Clamp(requestedDepth, ActiveSwimDepthMin, ActiveSwimDepthMax);
            requestedOutsideRange = requestedDepth < ActiveSwimDepthMin || requestedDepth > ActiveSwimDepthMax;
            adjustSpeed = configuredDiveProfile.ActiveDepthAdjustSpeed;
        }

        float unclampedBottomDepth = desiredDepth;
        desiredDepth = ClampSwimDepthForBottomContact(character, desiredDepth);
        requestedOutsideRange |= desiredDepth < unclampedBottomDepth - 0.001f;

        float clampedTargetY = liquidLevel - desiredDepth;
        return new SwimDepthGoal(desiredDepth, clampedTargetY, requestedOutsideRange, adjustSpeed);
    }

    private static void ApplySwimDepthGoal(Character character, SwimDepthGoal goal, float dt)
    {
        if (goal.AdjustSpeed <= 0f)
        {
            character.m_swimDepth = goal.DesiredDepth;
            return;
        }

        float step = goal.AdjustSpeed * Mathf.Max(dt, 0.01f);
        character.m_swimDepth = Mathf.MoveTowards(character.m_swimDepth, goal.DesiredDepth, step);
    }

    private static float ClampSwimDepthForBottomContact(Character character, float desiredDepth)
    {
        return UnderwaterDepthUtils.ClampDepthAboveBottom(character, desiredDepth, ActiveSwimDepthMin);
    }

    private static UnderwaterNavigationPlan BuildUnderwaterNavigationPlan(BaseAI ai, Character character, Vector3 targetPoint)
    {
        int instanceId = ai.GetInstanceID();
        Vector3Int currentPosBucket = ToCacheBucket(ai.transform.position);
        Vector3Int targetBucket = ToCacheBucket(targetPoint);
        float now = Time.time;

        if (MovePlanCache.TryGetValue(instanceId, out MovePlanCacheEntry cachedPlan) &&
            now - cachedPlan.Time <= MovePlanCacheSeconds &&
            cachedPlan.PositionBucket == currentPosBucket &&
            cachedPlan.TargetBucket == targetBucket)
        {
            return new UnderwaterNavigationPlan(cachedPlan.HasRoute, cachedPlan.Direction);
        }

        UnderwaterNavigationPlan navigationPlan = CalculateUnderwaterNavigationPlan(ai, character, targetPoint);
        TrimCachesIfNeeded();
        MovePlanCache[instanceId] = new MovePlanCacheEntry(
            now,
            currentPosBucket,
            targetBucket,
            navigationPlan.HasRoute,
            navigationPlan.Direction);

        return navigationPlan;
    }

    private static UnderwaterNavigationPlan CalculateUnderwaterNavigationPlan(BaseAI ai, Character character, Vector3 targetPoint)
    {
        Vector3 desiredDir = targetPoint - ai.transform.position;
        if (desiredDir.sqrMagnitude <= 0.0001f)
        {
            return new UnderwaterNavigationPlan(hasRoute: true, Vector3.zero);
        }

        desiredDir.Normalize();
        Vector3 horizontal = new(desiredDir.x, 0f, desiredDir.z);
        float radius = character.GetRadius();
        float horizontalDistance = Utils.DistanceXZ(targetPoint, ai.transform.position);
        float checkDistance = Mathf.Clamp(horizontalDistance, radius + 1f, 6f);

        if (horizontalDistance <= radius + 0.6f)
        {
            return new UnderwaterNavigationPlan(hasRoute: true, desiredDir);
        }

        if (horizontal.sqrMagnitude <= 0.0001f)
        {
            return new UnderwaterNavigationPlan(hasRoute: true, desiredDir);
        }

        horizontal.Normalize();
        Vector3 center = character.GetCenterPoint();
        int sampleCount = Mathf.Clamp(AvoidanceSampleCount, 3, SteerAngles.Length);
        Vector3 bestHorizontal = horizontal;
        bool bestHasRoute = false;
        float bestScore = float.NegativeInfinity;

        for (int i = 0; i < sampleCount; ++i)
        {
            float angle = SteerAngles[i];
            Vector3 candidate = Quaternion.Euler(0f, angle, 0f) * horizontal;
            bool clear = ai.CanMove(candidate, radius, checkDistance);
            float score;
            bool candidateHasRoute;
            if (clear)
            {
                score = 1000f - Mathf.Abs(angle);
                candidateHasRoute = true;
            }
            else
            {
                float freeDistance = ai.Raycast(center, candidate, checkDistance * 2f, 0.1f);
                score = freeDistance - Mathf.Abs(angle) * 0.01f;
                candidateHasRoute = freeDistance >= checkDistance * 0.9f;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestHorizontal = candidate;
                bestHasRoute = candidateHasRoute;
            }
        }

        Vector3 steer = new(bestHorizontal.x, desiredDir.y, bestHorizontal.z);
        Vector3 result = steer.sqrMagnitude > 0.0001f ? steer.normalized : desiredDir;
        return new UnderwaterNavigationPlan(bestHasRoute, result);
    }
}
