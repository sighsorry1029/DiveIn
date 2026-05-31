using UnityEngine;

namespace ServerSyncModTemplate;

internal static class UnderwaterDepthUtils
{
    internal const float BottomSwimDepthClearance = 0.05f;
    private const float BottomContactProbeDistance = 0.75f;

    internal static float ClampDepthAboveBottom(Character character, float desiredDepth, float minimumDepth)
    {
        float currentLiquidDepth = character.InLiquidDepth();
        if (currentLiquidDepth <= 0f || !IsAtUnderwaterBottom(character))
        {
            return desiredDepth;
        }

        float reachableDepth = Mathf.Max(minimumDepth, currentLiquidDepth - BottomSwimDepthClearance);
        return Mathf.Min(desiredDepth, reachableDepth);
    }

    internal static bool IsAtUnderwaterBottom(Character character)
    {
        if (character == null || !character.InWater())
        {
            return false;
        }

        if (character.IsOnGround())
        {
            return true;
        }

        if (ZoneSystem.instance == null)
        {
            return false;
        }

        float liquidLevel = character.GetLiquidLevel();
        Vector3 position = character.transform.position;
        float solidHeight = ZoneSystem.instance.GetSolidHeight(position);
        if (solidHeight >= liquidLevel)
        {
            return false;
        }

        float distanceAboveBottom = position.y - solidHeight;
        float probeDistance = Mathf.Max(BottomContactProbeDistance, character.GetRadius() * 0.5f);
        return distanceAboveBottom >= -0.25f && distanceAboveBottom <= probeDistance;
    }
}
