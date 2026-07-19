using System.Collections.Generic;
using UnityEngine;

namespace ServerSyncModTemplate;

public partial class ServerSyncModTemplatePlugin
{
    private const float PassiveWavePeriodSeconds = 12f;
    private const float DefaultActiveSwimDepthMin = 0.25f;
    private const float DefaultShallowWaterFleeDepth = 0f;
    private const float ActiveSwimDepthMax = 300f;
    private const float SwimDepthAdjustSpeed = 2f;
    private const float SteeringMemorySeconds = 0.1f;
    private const float SteeringMemoryDistance = 1f;
    private const float PreferredSteerAngleMax = 70f;
    private const int AvoidanceSampleCount = 8;
    private static readonly float[] SteerAngles = { 0f, -35f, 35f, -70f, 70f, -120f, 120f, 180f };
    private static readonly Dictionary<int, SteeringMemoryEntry> SteeringMemory = new();

    private readonly struct SteeringMemoryEntry
    {
        public readonly float Time;
        public readonly Vector3 Position;
        public readonly Vector3 Target;
        public readonly float Side;

        public SteeringMemoryEntry(float time, Vector3 position, Vector3 target, float side)
        {
            Time = time;
            Position = position;
            Target = target;
            Side = side;
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

    private static bool IsWithinSteeringMemoryRange(Vector3 a, Vector3 b)
    {
        float x = a.x - b.x;
        float z = a.z - b.z;
        return x * x + z * z <= SteeringMemoryDistance * SteeringMemoryDistance;
    }

    private static void ClearRuntimeCaches()
    {
        SteeringMemory.Clear();
    }

    private static void TrimCachesIfNeeded()
    {
        if (SteeringMemory.Count > MaxCacheEntries)
        {
            SteeringMemory.Clear();
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

    private static float GetActiveSwimDepthMin(ConfiguredDiveProfile profile)
    {
        return Mathf.Clamp(profile.ActiveMinDepth, 0f, ActiveSwimDepthMax);
    }

    private static SwimDepthGoal CalculateSwimDepthGoal(MonsterAI monsterAI, Character character, Vector3 point)
    {
        TryGetConfiguredDiveProfile(monsterAI, out ConfiguredDiveProfile configuredDiveProfile);
        float activeSwimDepthMin = GetActiveSwimDepthMin(configuredDiveProfile);
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
            desiredDepth = Mathf.Clamp(requestedDepth, activeSwimDepthMin, ActiveSwimDepthMax);
            requestedOutsideRange = requestedDepth < activeSwimDepthMin || requestedDepth > ActiveSwimDepthMax;
            adjustSpeed = configuredDiveProfile.ActiveDepthAdjustSpeed;
        }

        float unclampedBottomDepth = desiredDepth;
        desiredDepth = ClampSwimDepthForBottomContact(character, desiredDepth, activeSwimDepthMin);
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

    private static float ClampSwimDepthForBottomContact(Character character, float desiredDepth, float minimumDepth)
    {
        return UnderwaterDepthUtils.ClampDepthAboveBottom(character, desiredDepth, minimumDepth);
    }

    private static UnderwaterNavigationPlan BuildUnderwaterNavigationPlan(BaseAI ai, Character character, Vector3 targetPoint)
    {
        int instanceId = ai.GetInstanceID();
        Vector3 currentPosition = ai.transform.position;
        if (ai is MonsterAI configuredMonster &&
            TryGetConfiguredDiveProfile(configuredMonster, out ConfiguredDiveProfile profile) &&
            !profile.AvoidanceSteering)
        {
            SteeringMemory.Remove(instanceId);
            Vector3 directDirection = targetPoint - currentPosition;
            return new UnderwaterNavigationPlan(
                hasRoute: true,
                directDirection.sqrMagnitude > 0.0001f ? directDirection.normalized : Vector3.zero);
        }

        float now = Time.time;
        float preferredSide = 0f;
        bool allowSteeringMemory = !(ai is MonsterAI monsterAI && monsterAI.m_serpentMovement);
        if (allowSteeringMemory &&
            SteeringMemory.TryGetValue(instanceId, out SteeringMemoryEntry memory))
        {
            bool memoryValid = now - memory.Time <= SteeringMemorySeconds &&
                               IsWithinSteeringMemoryRange(currentPosition, memory.Position) &&
                               IsWithinSteeringMemoryRange(targetPoint, memory.Target);
            if (memoryValid)
            {
                preferredSide = memory.Side;
            }
            else
            {
                SteeringMemory.Remove(instanceId);
            }
        }

        UnderwaterNavigationPlan navigationPlan = CalculateUnderwaterNavigationPlan(
            ai,
            character,
            targetPoint,
            preferredSide,
            out float selectedAngle);

        float selectedSide = navigationPlan.HasRoute &&
                             Mathf.Abs(selectedAngle) > 0.1f &&
                             Mathf.Abs(selectedAngle) <= PreferredSteerAngleMax
            ? Mathf.Sign(selectedAngle)
            : 0f;
        if (allowSteeringMemory && selectedSide != 0f)
        {
            TrimCachesIfNeeded();
            SteeringMemory[instanceId] = new SteeringMemoryEntry(
                now,
                currentPosition,
                targetPoint,
                selectedSide);
        }

        return navigationPlan;
    }

    private static UnderwaterNavigationPlan CalculateUnderwaterNavigationPlan(
        BaseAI ai,
        Character character,
        Vector3 targetPoint,
        float preferredSide,
        out float selectedAngle)
    {
        selectedAngle = 0f;
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

        if (EvaluateSteerCandidate(
                ai,
                center,
                horizontal,
                SteerAngles[0],
                radius,
                checkDistance,
                ref bestHorizontal,
                ref bestHasRoute,
                ref bestScore,
                ref selectedAngle,
                out _))
        {
            return new UnderwaterNavigationPlan(hasRoute: true, desiredDir);
        }

        bool foundClearRoute = false;
        if (preferredSide != 0f)
        {
            for (int i = 1; i < sampleCount; ++i)
            {
                float angle = SteerAngles[i];
                if (!IsPreferredSteerAngle(angle, preferredSide))
                {
                    continue;
                }

                if (!EvaluateSteerCandidate(
                        ai,
                        center,
                        horizontal,
                        angle,
                        radius,
                        checkDistance,
                        ref bestHorizontal,
                        ref bestHasRoute,
                        ref bestScore,
                        ref selectedAngle,
                        out Vector3 candidate))
                {
                    continue;
                }

                bestHorizontal = candidate;
                bestHasRoute = true;
                selectedAngle = angle;
                foundClearRoute = true;
                break;
            }
        }

        if (!foundClearRoute)
        {
            for (int i = 1; i < sampleCount; ++i)
            {
                float angle = SteerAngles[i];
                if (preferredSide != 0f && IsPreferredSteerAngle(angle, preferredSide))
                {
                    continue;
                }

                if (!EvaluateSteerCandidate(
                        ai,
                        center,
                        horizontal,
                        angle,
                        radius,
                        checkDistance,
                        ref bestHorizontal,
                        ref bestHasRoute,
                        ref bestScore,
                        ref selectedAngle,
                        out Vector3 candidate))
                {
                    continue;
                }

                bestHorizontal = candidate;
                bestHasRoute = true;
                selectedAngle = angle;
                break;
            }
        }

        Vector3 steer = new(bestHorizontal.x, desiredDir.y, bestHorizontal.z);
        Vector3 result = steer.sqrMagnitude > 0.0001f ? steer.normalized : desiredDir;
        return new UnderwaterNavigationPlan(bestHasRoute, result);
    }

    private static bool IsPreferredSteerAngle(float angle, float preferredSide)
    {
        return Mathf.Abs(angle) <= PreferredSteerAngleMax && Mathf.Sign(angle) == preferredSide;
    }

    private static bool EvaluateSteerCandidate(
        BaseAI ai,
        Vector3 center,
        Vector3 horizontal,
        float angle,
        float radius,
        float checkDistance,
        ref Vector3 bestHorizontal,
        ref bool bestHasRoute,
        ref float bestScore,
        ref float selectedAngle,
        out Vector3 candidate)
    {
        candidate = Quaternion.Euler(0f, angle, 0f) * horizontal;
        if (ai.CanMove(candidate, radius, checkDistance))
        {
            return true;
        }

        float freeDistance = ai.Raycast(center, candidate, checkDistance * 2f, 0.1f);
        float score = freeDistance - Mathf.Abs(angle) * 0.01f;
        if (score <= bestScore)
        {
            return false;
        }

        bestScore = score;
        bestHorizontal = candidate;
        bestHasRoute = freeDistance >= checkDistance * 0.9f;
        selectedAngle = angle;
        return false;
    }
}
