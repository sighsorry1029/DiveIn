using HarmonyLib;
using UnityEngine;

namespace ServerSyncModTemplate;

internal static class UnderwaterVisualState
{
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
        RenderSettings.fogDensity = Mathf.Max(0f, fogDensity);

        _fogOverrideActive = true;
    }

    internal static void ResetAll()
    {
        ResetCameraAndFog();
        UnderwaterSurfaceRenderer.ResetAll();
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
        return Mathf.Clamp01(swimDepth * ServerSyncModTemplatePlugin.GetUnderwaterDarknessFactor());
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
    private enum VisualMode
    {
        Disabled,
        Surface,
        Underwater
    }

    private static bool ShouldUseUnderwaterVisuals(PlayerDiveController diver)
    {
        return diver.Player.InWater() && diver.ShouldTreatAsSwimming();
    }

    private static bool ShouldAllowUnderwaterCamera(PlayerDiveController diver)
    {
        return diver.IsUnderSurface() || diver.ShouldForceSwimming();
    }

    private static bool IsCameraUnderwater(Camera camera, PlayerDiveController diver)
    {
        return camera.transform.position.y < diver.Player.GetLiquidLevel();
    }

    private static VisualMode GetVisualMode(GameCamera? gameCamera, out PlayerDiveController? diver, out Camera? camera)
    {
        diver = null;
        camera = null;
        if (gameCamera == null || !ServerSyncModTemplatePlugin.IsUnderwaterVisualStylingEnabled())
        {
            return VisualMode.Disabled;
        }

        diver = PlayerDiveUtils.EnsureLocalDiver();
        camera = gameCamera.m_camera;
        if (diver == null
            || camera == null
            || !ShouldUseUnderwaterVisuals(diver)
            || !ShouldAllowUnderwaterCamera(diver))
        {
            return VisualMode.Disabled;
        }

        return IsCameraUnderwater(camera, diver) ? VisualMode.Underwater : VisualMode.Surface;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateCamera))]
    private static void GameCameraUpdateCameraPrefix(GameCamera __instance)
    {
        VisualMode mode = GetVisualMode(__instance, out _, out _);
        if (mode == VisualMode.Disabled)
        {
            UnderwaterVisualState.ResetAll();
            return;
        }

        UnderwaterVisualState.ApplyCameraOverride(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateCamera))]
    private static void GameCameraUpdateCameraPostfix(GameCamera __instance)
    {
        VisualMode mode = GetVisualMode(__instance, out PlayerDiveController? diver, out _);
        if (mode == VisualMode.Disabled || diver == null)
        {
            UnderwaterVisualState.ResetAll();
            return;
        }

        if (mode == VisualMode.Surface)
        {
            UnderwaterVisualState.ResetFog();
            UnderwaterSurfaceRenderer.ResetAll();
            return;
        }

        UnderwaterVisualState.ApplyCameraOverride(__instance);
        UnderwaterVisualState.ApplyFogOverride(diver);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.UpdateMaterials))]
    private static void WaterVolumeUpdateMaterialsPrefix(WaterVolume __instance)
    {
        if (__instance == null || __instance.m_waterSurface == null)
        {
            UnderwaterSurfaceRenderer.Reset(__instance);
            return;
        }

        VisualMode mode = GetVisualMode(GameCamera.instance, out _, out Camera? camera);
        if (mode != VisualMode.Underwater || camera == null)
        {
            UnderwaterSurfaceRenderer.Reset(__instance);
            return;
        }

        float waterLevelCamera = __instance.GetWaterSurface(camera.transform.position, 1f);
        if (camera.transform.position.y >= waterLevelCamera)
        {
            UnderwaterSurfaceRenderer.Reset(__instance);
            return;
        }

        UnderwaterSurfaceRenderer.Apply(__instance, waterLevelCamera);
    }
}
