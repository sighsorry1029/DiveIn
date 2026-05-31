using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace ServerSyncModTemplate;

internal static class UnderwaterSurfaceRenderer
{
    private static readonly int DepthPropertyId = Shader.PropertyToID("_depth");
    private static readonly int UseGlobalWindPropertyId = Shader.PropertyToID("_UseGlobalWind");
    private static readonly Dictionary<int, UnderwaterSurfaceState> SurfaceStates = new();

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
        ApplyWaterMaterialOverride(state);
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

        List<int> volumeIds = SurfaceStates.Keys.ToList();
        foreach (int volumeId in volumeIds)
        {
            SurfaceStates[volumeId].Restore();
            SurfaceStates.Remove(volumeId);
        }
    }

    private static void ApplyWaterMaterialOverride(UnderwaterSurfaceState state)
    {
        Material? material = state.OverrideMaterial;
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
            OriginalMaterial = Renderer != null ? Renderer.sharedMaterial : null;
            OverrideMaterial = OriginalMaterial != null ? Object.Instantiate(OriginalMaterial) : null;
        }

        public WaterVolume Volume { get; }
        public Transform SurfaceTransform { get; }
        public MeshRenderer? Renderer { get; }
        public Material? OriginalMaterial { get; }
        public Material? OverrideMaterial { get; private set; }
        public Vector3 OriginalPosition { get; }
        public Quaternion OriginalRotation { get; }
        public ShadowCastingMode OriginalShadowCastingMode { get; }
        private readonly float[] _forcedDepth = new float[4];

        public bool CanRender()
        {
            return Volume != null
                   && Volume.m_waterSurface != null
                   && SurfaceTransform != null
                   && Renderer != null
                   && OriginalMaterial != null
                   && OverrideMaterial != null;
        }

        public void Apply(float waterLevel)
        {
            if (!CanRender())
            {
                return;
            }

            Vector3 position = SurfaceTransform.position;
            SurfaceTransform.SetPositionAndRotation(
                new Vector3(position.x, waterLevel, position.z),
                OriginalRotation * Quaternion.Euler(180f, 0f, 0f));
            Volume.m_waterSurface.shadowCastingMode = ShadowCastingMode.TwoSided;

            if (Renderer!.sharedMaterial != OverrideMaterial)
            {
                Renderer.sharedMaterial = OverrideMaterial;
            }
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

            if (Renderer != null && OriginalMaterial != null)
            {
                Renderer.sharedMaterial = OriginalMaterial;
            }

            if (OverrideMaterial != null)
            {
                Object.Destroy(OverrideMaterial);
                OverrideMaterial = null;
            }
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
