using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace ServerSyncModTemplate;

internal static class UnderwaterVisualState
{
    private static readonly int DepthPropertyId = Shader.PropertyToID("_depth");
    private static readonly int UseGlobalWindPropertyId = Shader.PropertyToID("_UseGlobalWind");

    private sealed class WaterSurfaceState
    {
        public WaterSurfaceState(WaterVolume volume)
        {
            Volume = volume;
            Transform surfaceTransform = volume.m_waterSurface.transform;
            SurfaceTransform = surfaceTransform;
            Renderer = volume.m_waterSurface.GetComponent<MeshRenderer>();
            OriginalPosition = surfaceTransform.position;
            OriginalRotation = surfaceTransform.rotation;
            OriginalShadowCastingMode = volume.m_waterSurface.shadowCastingMode;
            OriginalMaterial = Renderer != null ? Renderer.sharedMaterial : null;
            OverrideMaterial = OriginalMaterial != null ? UnityEngine.Object.Instantiate(OriginalMaterial) : null;
        }

        public WaterVolume Volume { get; }
        public Transform SurfaceTransform { get; }
        public MeshRenderer? Renderer { get; }
        public Material? OriginalMaterial { get; }
        public Material? OverrideMaterial { get; private set; }
        public Vector3 OriginalPosition { get; }
        public Quaternion OriginalRotation { get; }
        public ShadowCastingMode OriginalShadowCastingMode { get; }

        public void ApplyOverrideMaterial()
        {
            if (Renderer != null && OverrideMaterial != null && Renderer.sharedMaterial != OverrideMaterial)
            {
                Renderer.sharedMaterial = OverrideMaterial;
            }
        }

        public void RestoreOriginalMaterial()
        {
            if (Renderer != null && OverrideMaterial != null && Renderer.sharedMaterial == OverrideMaterial)
            {
                Renderer.sharedMaterial = OriginalMaterial;
            }

            if (OverrideMaterial != null)
            {
                UnityEngine.Object.Destroy(OverrideMaterial);
                OverrideMaterial = null;
            }
        }
    }

    private static readonly Dictionary<int, WaterSurfaceState> WaterSurfaceStates = new();
    private static float? _originalMinWaterDistance;
    private static GameCamera? _cameraWithOverride;
    private static bool _fogOverrideActive;

    internal static void ApplyCameraOverride(GameCamera gameCamera)
    {
        float minWaterDistanceOverride = ServerSyncModTemplatePlugin.GetUnderwaterCameraMinWaterDistance();
        if (!_originalMinWaterDistance.HasValue)
        {
            _originalMinWaterDistance = gameCamera.m_minWaterDistance;
            _cameraWithOverride = gameCamera;
        }

        gameCamera.m_minWaterDistance = minWaterDistanceOverride;
    }

    internal static void ApplyFogOverride(PlayerDiveController diver)
    {
        if (EnvMan.instance == null)
        {
            return;
        }

        EnvSetup currentEnvironment = EnvMan.instance.GetCurrentEnvironment();
        if (currentEnvironment == null)
        {
            return;
        }

        Color waterColor = !EnvMan.IsNight() ? currentEnvironment.m_fogColorDay : currentEnvironment.m_fogColorNight;
        waterColor.a = 1f;
        float darknessAmount = GetUnderwaterDarknessAmount(diver.Player.m_swimDepth);
        float brightnessMultiplier = Mathf.Clamp01(1f - darknessAmount);
        waterColor = ApplyBrightnessMultiplier(waterColor, brightnessMultiplier);
        RenderSettings.fogColor = waterColor;

        float fogDensity = GetEnvironmentFogDensity(currentEnvironment) + (diver.Player.m_swimDepth * ServerSyncModTemplatePlugin.GetUnderwaterVisibilityFalloff());
        RenderSettings.fogDensity = Mathf.Clamp(
            fogDensity,
            ServerSyncModTemplatePlugin.GetMinimumUnderwaterMurkiness(),
            ServerSyncModTemplatePlugin.GetMaximumUnderwaterMurkiness());

        _fogOverrideActive = true;
    }

    internal static void ApplyWaterSurfaceOverride(WaterVolume volume, float waterLevel)
    {
        if (volume.m_waterSurface == null)
        {
            return;
        }

        int volumeId = volume.GetInstanceID();
        if (!WaterSurfaceStates.TryGetValue(volumeId, out WaterSurfaceState? state))
        {
            state = new WaterSurfaceState(volume);
            WaterSurfaceStates[volumeId] = state;
        }

        if (state.Renderer == null)
        {
            return;
        }

        volume.m_waterSurface.transform.SetPositionAndRotation(
            new Vector3(state.OriginalPosition.x, waterLevel, state.OriginalPosition.z),
            state.OriginalRotation * Quaternion.Euler(180f, 0f, 0f));
        volume.m_waterSurface.shadowCastingMode = ShadowCastingMode.TwoSided;
        state.ApplyOverrideMaterial();
        ApplyWaterMaterialOverride(state);
    }

    internal static void ResetWaterSurface(WaterVolume? volume)
    {
        if (volume == null)
        {
            return;
        }

        if (!WaterSurfaceStates.TryGetValue(volume.GetInstanceID(), out WaterSurfaceState? state))
        {
            return;
        }

        RestoreWaterSurfaceState(state);
        WaterSurfaceStates.Remove(volume.GetInstanceID());
    }

    internal static void ResetAll()
    {
        ResetCameraAndFog();
        ResetTrackedWaterSurfacesExcept(null);
    }

    internal static void ResetCameraAndFog()
    {
        ResetCamera();
        ResetFog();
    }

    internal static void ResetCamera()
    {
        GameCamera? camera = _cameraWithOverride != null ? _cameraWithOverride : GameCamera.instance;
        if (camera != null && _originalMinWaterDistance.HasValue)
        {
            camera.m_minWaterDistance = _originalMinWaterDistance.Value;
        }

        _originalMinWaterDistance = null;
        _cameraWithOverride = null;
    }

    internal static void ResetFog()
    {
        if (!_fogOverrideActive)
        {
            return;
        }

        _fogOverrideActive = false;
        RefreshEnvironment();
    }

    private static void ResetTrackedWaterSurfacesExcept(int? activeVolumeId)
    {
        if (WaterSurfaceStates.Count == 0)
        {
            return;
        }

        List<int> volumeIds = WaterSurfaceStates.Keys.ToList();
        foreach (int volumeId in volumeIds)
        {
            if (activeVolumeId.HasValue && volumeId == activeVolumeId.Value)
            {
                continue;
            }

            RestoreWaterSurfaceState(WaterSurfaceStates[volumeId]);
            WaterSurfaceStates.Remove(volumeId);
        }
    }

    private static void RestoreWaterSurfaceState(WaterSurfaceState state)
    {
        state.RestoreOriginalMaterial();

        if (state.Volume == null || state.Volume.m_waterSurface == null || state.SurfaceTransform == null)
        {
            return;
        }

        state.SurfaceTransform.SetPositionAndRotation(state.OriginalPosition, state.OriginalRotation);
        if (state.Renderer != null)
        {
            state.Volume.m_waterSurface.shadowCastingMode = state.OriginalShadowCastingMode;
        }
    }

    private static void ApplyWaterMaterialOverride(WaterSurfaceState state)
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
                material.SetFloatArray(
                    DepthPropertyId,
                    new[] { state.Volume.m_forceDepth, state.Volume.m_forceDepth, state.Volume.m_forceDepth, state.Volume.m_forceDepth });
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

    private static void RefreshEnvironment()
    {
        if (EnvMan.instance == null)
        {
            return;
        }

        EnvSetup currentEnvironment = EnvMan.instance.GetCurrentEnvironment();
        if (currentEnvironment == null)
        {
            return;
        }

        EnvMan.instance.SetForceEnvironment(currentEnvironment.m_name);
        EnvMan.instance.SetForceEnvironment(string.Empty);
    }

    private static float GetUnderwaterDarknessAmount(float swimDepth)
    {
        return Mathf.Clamp(
            swimDepth * ServerSyncModTemplatePlugin.GetUnderwaterDarknessFactor(),
            ServerSyncModTemplatePlugin.GetMinimumUnderwaterDarkness(),
            ServerSyncModTemplatePlugin.GetMaximumUnderwaterDarkness());
    }

    private static Color ApplyBrightnessMultiplier(Color color, float brightnessMultiplier)
    {
        float clampedBrightness = Mathf.Clamp01(brightnessMultiplier);
        return new Color(color.r * clampedBrightness, color.g * clampedBrightness, color.b * clampedBrightness, color.a);
    }

    private static float GetEnvironmentFogDensity(EnvSetup environment)
    {
        return !EnvMan.IsNight() ? environment.m_fogDensityDay : environment.m_fogDensityNight;
    }
}

[HarmonyPatch]
internal static class UnderwaterCameraPatches
{
    private static bool ShouldKeepCameraOverride(PlayerDiveController diver)
    {
        if (!diver.Player.InWater())
        {
            return false;
        }

        // Keep camera override stable through slow underwater movement and
        // brief swim-state transitions while the player is still submerged.
        return diver.ShouldForceSwimming()
               || diver.IsHeadUnderwater()
               || diver.IsUnderSurface()
               || (diver.Player.IsSwimming() && !diver.IsIdleInWater());
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateCamera))]
    private static void GameCameraUpdateCameraPrefix(GameCamera __instance)
    {
        if (__instance == null
            || PlayerDiveUtils.EnsureLocalDiver() is not PlayerDiveController diver)
        {
            UnderwaterVisualState.ResetAll();
            return;
        }

        Camera camera = __instance.m_camera;
        if (camera == null)
        {
            UnderwaterVisualState.ResetAll();
            return;
        }

        bool shouldKeepCameraOverride = ShouldKeepCameraOverride(diver);
        if (!shouldKeepCameraOverride)
        {
            UnderwaterVisualState.ResetAll();
            return;
        }

        UnderwaterVisualState.ApplyCameraOverride(__instance);

        if (!ServerSyncModTemplatePlugin.IsUnderwaterVisualStylingEnabled())
        {
            UnderwaterVisualState.ResetFog();
            return;
        }

        float waterLevelCamera = diver.Player.GetLiquidLevel();
        if (camera.transform.position.y < waterLevelCamera)
        {
            UnderwaterVisualState.ApplyFogOverride(diver);
            return;
        }

        UnderwaterVisualState.ResetFog();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.UpdateMaterials))]
    private static void WaterVolumeUpdateMaterialsPrefix(WaterVolume __instance)
    {
        if (GameCamera.instance == null
            || Player.m_localPlayer == null
            || __instance == null
            || __instance.m_waterSurface == null
            || !ServerSyncModTemplatePlugin.IsUnderwaterVisualStylingEnabled())
        {
            UnderwaterVisualState.ResetWaterSurface(__instance);
            return;
        }

        float waterLevelCamera = __instance.GetWaterSurface(GameCamera.instance.transform.position);
        bool cameraUnderwater = GameCamera.instance.transform.position.y < waterLevelCamera;
        if (!cameraUnderwater || !Player.m_localPlayer.IsSwimming())
        {
            UnderwaterVisualState.ResetWaterSurface(__instance);
            return;
        }

        UnderwaterVisualState.ApplyWaterSurfaceOverride(__instance, waterLevelCamera);
    }
}
