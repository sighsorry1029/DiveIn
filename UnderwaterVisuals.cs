using HarmonyLib;
using UnityEngine;

namespace ServerSyncModTemplate;

internal static class UnderwaterVisualState
{
    private static float? _originalMinWaterDistance;
    private static GameCamera? _cameraWithOverride;
    private static bool _fogOverrideActive;
    private static Color _originalFogColor;
    private static float _originalFogDensity;

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

        if (!_fogOverrideActive)
        {
            _originalFogColor = RenderSettings.fogColor;
            _originalFogDensity = RenderSettings.fogDensity;
            _fogOverrideActive = true;
        }

        Color waterColor = !EnvMan.IsNight() ? currentEnvironment.m_fogColorDay : currentEnvironment.m_fogColorNight;
        waterColor.a = 1f;
        float darknessAmount = GetUnderwaterDarknessAmount(diver.Player.m_swimDepth);
        float brightnessMultiplier = Mathf.Clamp01(1f - darknessAmount);
        waterColor = ApplyBrightnessMultiplier(waterColor, brightnessMultiplier);
        RenderSettings.fogColor = waterColor;

        float fogDensity = GetEnvironmentFogDensity(currentEnvironment) + (diver.Player.m_swimDepth * ServerSyncModTemplatePlugin.GetUnderwaterVisibilityFalloff());
        RenderSettings.fogDensity = Mathf.Max(0f, fogDensity);
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
        RenderSettings.fogColor = _originalFogColor;
        RenderSettings.fogDensity = _originalFogDensity;
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
    private const float UnderwaterCameraSurfaceClearance = 1f;

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

    private static void ClampSubmergedCameraBelowSurface(GameCamera gameCamera, PlayerDiveController? diver)
    {
        Camera? camera = gameCamera.m_camera;
        if (diver == null
            || camera == null
            || diver.Player.m_eye == null
            || !ShouldUseUnderwaterVisuals(diver)
            || !ShouldAllowUnderwaterCamera(diver)
            || !diver.IsHeadUnderwater())
        {
            return;
        }

        float waterLevel = diver.Player.GetLiquidLevel();
        Vector3 eyePosition = diver.Player.m_eye.position;
        float eyeDepth = waterLevel - eyePosition.y;
        if (eyeDepth <= 0f)
        {
            return;
        }

        float clearance = Mathf.Min(
            UnderwaterCameraSurfaceClearance,
            eyeDepth * 0.5f);
        float maximumCameraY = waterLevel - clearance;
        Transform cameraTransform = camera.transform;
        Vector3 cameraPosition = cameraTransform.position;
        if (cameraPosition.y <= maximumCameraY)
        {
            return;
        }

        float verticalSpan = cameraPosition.y - eyePosition.y;
        if (verticalSpan <= 0f)
        {
            return;
        }

        float distanceFraction = Mathf.Clamp01((maximumCameraY - eyePosition.y) / verticalSpan);
        cameraTransform.position = Vector3.Lerp(eyePosition, cameraPosition, distanceFraction);
        camera.nearClipPlane = Mathf.Min(camera.nearClipPlane, gameCamera.m_nearClipPlaneMin);
        gameCamera.m_waterClipping = true;
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

    private static void ApplyCameraVisualMode(
        GameCamera gameCamera,
        VisualMode mode,
        PlayerDiveController? diver,
        bool beforeCameraUpdate)
    {
        if (beforeCameraUpdate)
        {
            UnderwaterSurfaceRenderer.ResetStale();
            if (mode == VisualMode.Disabled)
            {
                UnderwaterVisualState.ResetAll();
                return;
            }

            UnderwaterVisualState.ApplyCameraOverride(gameCamera);
            return;
        }

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

        UnderwaterVisualState.ApplyCameraOverride(gameCamera);
        UnderwaterVisualState.ApplyFogOverride(diver);
    }

    private static bool ShouldRenderUnderwaterSurface(WaterVolume? waterVolume)
    {
        if (waterVolume == null || waterVolume.m_waterSurface == null)
        {
            UnderwaterSurfaceRenderer.Reset(waterVolume);
            return false;
        }

        if (GetVisualMode(GameCamera.instance, out _, out _) != VisualMode.Underwater)
        {
            UnderwaterSurfaceRenderer.Reset(waterVolume);
            return false;
        }

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateCamera))]
    private static void GameCameraUpdateCameraPrefix(GameCamera __instance)
    {
        ApplyCameraVisualMode(
            __instance,
            GetVisualMode(__instance, out PlayerDiveController? diver, out _),
            diver,
            beforeCameraUpdate: true);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateCamera))]
    private static void GameCameraUpdateCameraPostfix(GameCamera __instance)
    {
        ClampSubmergedCameraBelowSurface(__instance, PlayerDiveUtils.EnsureLocalDiver());
        ApplyCameraVisualMode(
            __instance,
            GetVisualMode(__instance, out PlayerDiveController? diver, out _),
            diver,
            beforeCameraUpdate: false);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.UpdateMaterials))]
    private static void WaterVolumeUpdateMaterialsPrefix(WaterVolume __instance)
    {
        if (!ShouldRenderUnderwaterSurface(__instance))
        {
            return;
        }

        UnderwaterSurfaceRenderer.Apply(__instance);
    }
}
