using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace ServerSyncModTemplate;

// UV initialization identified by MidnightsFX's ValheimCommunityPatch WaterColorSeamPatch (GPL-3.0).
// Keep this permanent material setup separate from the temporary underwater surface overrides below.
[HarmonyPatch(typeof(WaterVolume), "SetupMaterial")]
internal static class WaterColorSeamPatch
{
    private static void Postfix(WaterVolume __instance)
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || __instance.m_waterSurface == null)
        {
            return;
        }

        // Vanilla SetupMaterial has already created the per-renderer material instance.
        Material material = __instance.m_waterSurface.material;
        if (material == null || material.shader == null || material.shader.name != "Custom/Water"
            || material.HasProperty("_MainTex"))
        {
            return;
        }

        // This uniform is used by the shader but is absent from its exposed property list.
        // Do not guard it with HasProperty("_MainTex_ST"): that would skip the affected vanilla shader.
        // Only repair color UVs; leave colors, _depth, wave amplitude and CPU water data untouched.
        material.SetVector("_MainTex_ST", new Vector4(1f, 1f, 0f, 0f));
    }
}

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
        private readonly List<float> _currentDepth = new(4);
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
                WaterMaterial.GetFloatArray(DepthPropertyId, _currentDepth);
                if (_currentDepth.Count == 0)
                {
                    _depthOverrideActive = false;
                    _originalDepth = null;
                }
                else
                {
                    if (!_depthOverrideActive || !DepthValuesEqual(_currentDepth, _underwaterDepth))
                    {
                        if (_originalDepth == null || _originalDepth.Length != _currentDepth.Count)
                        {
                            _originalDepth = new float[_currentDepth.Count];
                        }

                        _currentDepth.CopyTo(_originalDepth);
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
                        _underwaterDepth[0] = GameAccess.WaterDepth(Volume)[3];
                        _underwaterDepth[1] = GameAccess.WaterDepth(Volume)[2];
                        _underwaterDepth[2] = GameAccess.WaterDepth(Volume)[1];
                        _underwaterDepth[3] = GameAccess.WaterDepth(Volume)[0];
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
                WaterMaterial.GetFloatArray(DepthPropertyId, _currentDepth);
                if (DepthValuesEqual(_currentDepth, _underwaterDepth))
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

        private static bool DepthValuesEqual(List<float> currentDepth, float[] expectedDepth)
        {
            if (currentDepth.Count != expectedDepth.Length)
            {
                return false;
            }

            for (int index = 0; index < currentDepth.Count; index++)
            {
                if (!currentDepth[index].Equals(expectedDepth[index]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
