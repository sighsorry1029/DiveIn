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

    internal static void Apply(WaterVolume volume)
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

        state.Apply();
    }

    internal static void Reset(WaterVolume? volume)
    {
        if (volume == null)
        {
            return;
        }

        int volumeId = volume.GetInstanceID();
        if (!SurfaceStates.TryGetValue(volumeId, out UnderwaterSurfaceState? state))
        {
            return;
        }

        state.Restore();
        SurfaceStates.Remove(volumeId);
    }

    internal static void ResetAll()
    {
        foreach (UnderwaterSurfaceState state in SurfaceStates.Values)
        {
            state.Restore();
        }

        SurfaceStates.Clear();
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

    private sealed class UnderwaterSurfaceState
    {
        public UnderwaterSurfaceState(WaterVolume volume)
        {
            Volume = volume;
            Renderer = volume.m_waterSurface;
            SurfaceTransform = Renderer.transform;
            OriginalPosition = SurfaceTransform.position;
            OriginalRotation = SurfaceTransform.rotation;
            OriginalShadowCastingMode = Renderer.shadowCastingMode;
            WaterMaterial = Renderer.material;
        }

        public WaterVolume Volume { get; }
        public Transform SurfaceTransform { get; }
        public MeshRenderer Renderer { get; }
        private Material? WaterMaterial { get; }
        public Vector3 OriginalPosition { get; }
        public Quaternion OriginalRotation { get; }
        public ShadowCastingMode OriginalShadowCastingMode { get; }
        public int LastAppliedFrame { get; private set; } = Time.frameCount;
        private readonly float[] _underwaterDepth = new float[4];
        private bool _depthOverrideActive;
        private float[]? _originalDepth;
        private bool _globalWindOverrideActive;
        private float _originalUseGlobalWind;
        private float _lastAppliedUseGlobalWind;

        public bool CanRender()
        {
            return Volume != null
                   && SurfaceTransform != null
                   && Renderer != null
                   && Volume.m_waterSurface == Renderer
                   && object.ReferenceEquals(Renderer.material, WaterMaterial);
        }

        public void Apply()
        {
            LastAppliedFrame = Time.frameCount;
            Vector3 position = SurfaceTransform.position;
            SurfaceTransform.SetPositionAndRotation(
                new Vector3(position.x, OriginalPosition.y, position.z),
                OriginalRotation * Quaternion.Euler(180f, 0f, 0f));
            Renderer.shadowCastingMode = ShadowCastingMode.TwoSided;
            ApplyWaterMaterialProperties();
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

            if (Renderer != null)
            {
                Renderer.shadowCastingMode = OriginalShadowCastingMode;
            }

            RestoreWaterMaterialProperties();
        }

        public bool ShouldResetAsStale(int currentFrame, int maxFrameAge)
        {
            return !CanRender() || currentFrame - LastAppliedFrame > maxFrameAge;
        }

        private void ApplyWaterMaterialProperties()
        {
            if (WaterMaterial == null)
            {
                return;
            }

            if (WaterMaterial.HasProperty(DepthPropertyId))
            {
                float[]? currentDepth = WaterMaterial.GetFloatArray(DepthPropertyId);
                if (currentDepth == null)
                {
                    _depthOverrideActive = false;
                    _originalDepth = null;
                }
                else
                {
                    if (!_depthOverrideActive || !FloatArraysEqual(currentDepth, _underwaterDepth))
                    {
                        _originalDepth = currentDepth;
                    }

                    if (Volume.m_forceDepth >= 0f)
                    {
                        _underwaterDepth[0] = Volume.m_forceDepth;
                        _underwaterDepth[1] = Volume.m_forceDepth;
                        _underwaterDepth[2] = Volume.m_forceDepth;
                        _underwaterDepth[3] = Volume.m_forceDepth;
                    }
                    else
                    {
                        _underwaterDepth[0] = Volume.m_normalizedDepth[3];
                        _underwaterDepth[1] = Volume.m_normalizedDepth[2];
                        _underwaterDepth[2] = Volume.m_normalizedDepth[1];
                        _underwaterDepth[3] = Volume.m_normalizedDepth[0];
                    }

                    WaterMaterial.SetFloatArray(DepthPropertyId, _underwaterDepth);
                    _depthOverrideActive = true;
                }
            }

            if (WaterMaterial.HasProperty(UseGlobalWindPropertyId))
            {
                float currentUseGlobalWind = WaterMaterial.GetFloat(UseGlobalWindPropertyId);
                if (!_globalWindOverrideActive || !currentUseGlobalWind.Equals(_lastAppliedUseGlobalWind))
                {
                    _originalUseGlobalWind = currentUseGlobalWind;
                }

                _lastAppliedUseGlobalWind = Volume.m_useGlobalWind ? 1f : 0f;
                WaterMaterial.SetFloat(UseGlobalWindPropertyId, _lastAppliedUseGlobalWind);
                _globalWindOverrideActive = true;
            }
        }

        private void RestoreWaterMaterialProperties()
        {
            if (WaterMaterial == null)
            {
                _depthOverrideActive = false;
                _globalWindOverrideActive = false;
                return;
            }

            if (_depthOverrideActive
                && _originalDepth != null
                && WaterMaterial.HasProperty(DepthPropertyId))
            {
                float[]? currentDepth = WaterMaterial.GetFloatArray(DepthPropertyId);
                if (FloatArraysEqual(currentDepth, _underwaterDepth))
                {
                    WaterMaterial.SetFloatArray(DepthPropertyId, _originalDepth);
                }
            }

            if (_globalWindOverrideActive && WaterMaterial.HasProperty(UseGlobalWindPropertyId))
            {
                float currentUseGlobalWind = WaterMaterial.GetFloat(UseGlobalWindPropertyId);
                if (currentUseGlobalWind.Equals(_lastAppliedUseGlobalWind))
                {
                    WaterMaterial.SetFloat(UseGlobalWindPropertyId, _originalUseGlobalWind);
                }
            }

            _depthOverrideActive = false;
            _originalDepth = null;
            _globalWindOverrideActive = false;
        }

        private static bool FloatArraysEqual(float[]? left, float[]? right)
        {
            if (object.ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            for (int index = 0; index < left.Length; index++)
            {
                if (!left[index].Equals(right[index]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
