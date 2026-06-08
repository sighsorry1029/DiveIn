using UnityEngine;

namespace ServerSyncModTemplate;

internal static class SwimResourceAdjustments
{
    internal static bool TryGetDrain(float before, float after, out float drain)
    {
        drain = Mathf.Max(0f, before - after);
        return drain > 0f;
    }

    internal static bool TryGetGain(float before, float after, out float gain)
    {
        gain = Mathf.Max(0f, after - before);
        return gain > 0f;
    }

    internal static float GetScaledDrainValue(float before, float max, float drain, float multiplier)
    {
        float scaledDrain = drain * Mathf.Max(0f, multiplier);
        return Mathf.Clamp(before - scaledDrain, 0f, max);
    }

    internal static float GetScaledGainValue(float before, float max, float gain, float multiplier)
    {
        float scaledGain = gain * Mathf.Clamp01(multiplier);
        return Mathf.Clamp(before + scaledGain, 0f, max);
    }
}
