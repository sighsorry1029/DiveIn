using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ServerSyncModTemplate;

internal static class UnderwaterSurfaceRenderer
{
    private const int StaleSurfaceResetFrameDelay = 5;
    private static readonly int DepthPropertyId = Shader.PropertyToID("_depth");
    private static readonly int UseGlobalWindPropertyId = Shader.PropertyToID("_UseGlobalWind");
    private static readonly Dictionary<int, UnderwaterSurfaceState> SurfaceStates = new();
    private static readonly List<int> SurfaceIdsToReset = new();

    internal static void Apply(WaterVolume volume, float waterLevel)
    {
        if (volume.m_waterSurface == null)
        {
            return;
        }

        int volumeId = volume.GetInstanceID();
        if (!SurfaceStates.TryGetValue(volumeId, out UnderwaterSurfaceState? state))
        {
            state = new UnderwaterSurfaceState(volume);
            SurfaceStates[volumeId] = state;
        }

        if (!state.CanRender())
        {
            state.Restore();
            SurfaceStates.Remove(volumeId);
            return;
        }

        state.Apply(waterLevel);
        ApplyWaterMaterialProperties(state);
    }

    internal static void Reset(WaterVolume? volume)
    {
        if (volume == null)
        {
            return;
        }

        if (!SurfaceStates.TryGetValue(volume.GetInstanceID(), out UnderwaterSurfaceState? state))
        {
            return;
        }

        state.Restore();
        SurfaceStates.Remove(volume.GetInstanceID());
    }

    internal static void ResetAll()
    {
        if (SurfaceStates.Count == 0)
        {
            return;
        }

        SurfaceIdsToReset.Clear();
        foreach (int volumeId in SurfaceStates.Keys)
        {
            SurfaceIdsToReset.Add(volumeId);
        }

        foreach (int volumeId in SurfaceIdsToReset)
        {
            RestoreAndRemove(volumeId);
        }

        SurfaceIdsToReset.Clear();
    }

    internal static void ResetStale()
    {
        if (SurfaceStates.Count == 0)
        {
            return;
        }

        int currentFrame = Time.frameCount;
        SurfaceIdsToReset.Clear();
        foreach (KeyValuePair<int, UnderwaterSurfaceState> entry in SurfaceStates)
        {
            if (entry.Value.ShouldResetAsStale(currentFrame, StaleSurfaceResetFrameDelay))
            {
                SurfaceIdsToReset.Add(entry.Key);
            }
        }

        foreach (int volumeId in SurfaceIdsToReset)
        {
            RestoreAndRemove(volumeId);
        }

        SurfaceIdsToReset.Clear();
    }

    private static void RestoreAndRemove(int volumeId)
    {
        if (!SurfaceStates.TryGetValue(volumeId, out UnderwaterSurfaceState? state))
        {
            return;
        }

        state.Restore();
        SurfaceStates.Remove(volumeId);
    }

    private static void ApplyWaterMaterialProperties(UnderwaterSurfaceState state)
    {
        Material? material = state.GetWaterMaterial();
        if (material == null)
        {
            return;
        }

        if (material.HasProperty(DepthPropertyId))
        {
            if (state.Volume.m_forceDepth >= 0f)
            {
                material.SetFloatArray(DepthPropertyId, state.GetForcedDepthArray(state.Volume.m_forceDepth));
            }
            else
            {
                material.SetFloatArray(DepthPropertyId, state.Volume.m_normalizedDepth);
            }
        }

        if (material.HasProperty(UseGlobalWindPropertyId))
        {
            material.SetFloat(UseGlobalWindPropertyId, state.Volume.m_useGlobalWind ? 1f : 0f);
        }
    }

    private sealed class UnderwaterSurfaceState
    {
        public UnderwaterSurfaceState(WaterVolume volume)
        {
            Volume = volume;
            SurfaceTransform = volume.m_waterSurface.transform;
            Renderer = volume.m_waterSurface.GetComponent<MeshRenderer>();
            OriginalPosition = SurfaceTransform.position;
            OriginalRotation = SurfaceTransform.rotation;
            OriginalShadowCastingMode = volume.m_waterSurface.shadowCastingMode;
        }

        public WaterVolume Volume { get; }
        public Transform SurfaceTransform { get; }
        public MeshRenderer? Renderer { get; }
        public Vector3 OriginalPosition { get; }
        public Quaternion OriginalRotation { get; }
        public ShadowCastingMode OriginalShadowCastingMode { get; }
        public int LastAppliedFrame { get; private set; } = Time.frameCount;
        private readonly float[] _forcedDepth = new float[4];

        public bool CanRender()
        {
            return Volume != null
                   && Volume.m_waterSurface != null
                   && SurfaceTransform != null
                   && Renderer != null;
        }

        public void Apply(float waterLevel)
        {
            if (!CanRender())
            {
                return;
            }

            LastAppliedFrame = Time.frameCount;
            Vector3 position = SurfaceTransform.position;
            SurfaceTransform.SetPositionAndRotation(
                new Vector3(position.x, waterLevel, position.z),
                OriginalRotation * Quaternion.Euler(180f, 0f, 0f));
            Volume.m_waterSurface.shadowCastingMode = ShadowCastingMode.TwoSided;
        }

        public void Restore()
        {
            if (SurfaceTransform != null)
            {
                Vector3 position = SurfaceTransform.position;
                SurfaceTransform.SetPositionAndRotation(
                    new Vector3(position.x, OriginalPosition.y, position.z),
                    OriginalRotation);
            }

            if (Volume != null && Volume.m_waterSurface != null)
            {
                Volume.m_waterSurface.shadowCastingMode = OriginalShadowCastingMode;
            }
        }

        public bool ShouldResetAsStale(int currentFrame, int maxFrameAge)
        {
            return !CanRender() || currentFrame - LastAppliedFrame > maxFrameAge;
        }

        public Material? GetWaterMaterial()
        {
            return Renderer != null ? Renderer.material : null;
        }

        public float[] GetForcedDepthArray(float depth)
        {
            _forcedDepth[0] = depth;
            _forcedDepth[1] = depth;
            _forcedDepth[2] = depth;
            _forcedDepth[3] = depth;
            return _forcedDepth;
        }
    }
}
