using HarmonyLib;
using UnityEngine;

namespace ServerSyncModTemplate;

internal static class UnderwaterVisualState
{
    private const float UnderwaterCameraMinWaterDistance = -5000f;
    private static float? _originalMinWaterDistance;
    private static GameCamera? _cameraWithOverride;
    private static float _appliedMinWaterDistance;
    private static bool _fogOverrideActive;
    private static Color _originalFogColor;
    private static float _originalFogDensity;
    private static Color _appliedFogColor;
    private static float _appliedFogDensity;

    internal static void ApplyCameraOverride(GameCamera gameCamera)
    {
        if (_cameraWithOverride != gameCamera)
        {
            ResetCamera();
            _originalMinWaterDistance = gameCamera.m_minWaterDistance;
            _cameraWithOverride = gameCamera;
        }
        else if (_originalMinWaterDistance.HasValue
                 && !gameCamera.m_minWaterDistance.Equals(_appliedMinWaterDistance))
        {
            _originalMinWaterDistance = gameCamera.m_minWaterDistance;
        }

        _appliedMinWaterDistance = UnderwaterCameraMinWaterDistance;
        gameCamera.m_minWaterDistance = _appliedMinWaterDistance;
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

        Color currentFogColor = RenderSettings.fogColor;
        float currentFogDensity = RenderSettings.fogDensity;
        if (!_fogOverrideActive)
        {
            _originalFogColor = currentFogColor;
            _originalFogDensity = currentFogDensity;
            _fogOverrideActive = true;
        }
        else
        {
            if (!currentFogColor.Equals(_appliedFogColor))
            {
                _originalFogColor = currentFogColor;
            }

            if (!currentFogDensity.Equals(_appliedFogDensity))
            {
                _originalFogDensity = currentFogDensity;
            }
        }

        bool isNight = EnvMan.IsNight();
        Color waterColor = !isNight ? currentEnvironment.m_fogColorDay : currentEnvironment.m_fogColorNight;
        waterColor.a = 1f;
        float brightnessMultiplier = 1f - Mathf.Clamp01(
            diver.Player.m_swimDepth * ServerSyncModTemplatePlugin.GetUnderwaterDarknessFactor());
        _appliedFogColor = new Color(
            waterColor.r * brightnessMultiplier,
            waterColor.g * brightnessMultiplier,
            waterColor.b * brightnessMultiplier,
            waterColor.a);
        _appliedFogDensity = Mathf.Max(
            0f,
            (!isNight ? currentEnvironment.m_fogDensityDay : currentEnvironment.m_fogDensityNight)
            + (diver.Player.m_swimDepth * ServerSyncModTemplatePlugin.GetUnderwaterVisibilityFalloff()));
        RenderSettings.fogColor = _appliedFogColor;
        RenderSettings.fogDensity = _appliedFogDensity;
    }

    internal static void ResetAll()
    {
        ResetCamera();
        ResetFog();
        UnderwaterSurfaceRenderer.ResetAll();
    }

    internal static void ResetCamera()
    {
        if (_cameraWithOverride != null
            && _originalMinWaterDistance.HasValue
            && _cameraWithOverride.m_minWaterDistance.Equals(_appliedMinWaterDistance))
        {
            _cameraWithOverride.m_minWaterDistance = _originalMinWaterDistance.Value;
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

        if (RenderSettings.fogColor.Equals(_appliedFogColor))
        {
            RenderSettings.fogColor = _originalFogColor;
        }

        if (RenderSettings.fogDensity.Equals(_appliedFogDensity))
        {
            RenderSettings.fogDensity = _originalFogDensity;
        }

        _fogOverrideActive = false;
    }
}

[HarmonyPatch]
internal static class UnderwaterCameraPatches
{
    private const float UnderwaterCameraSurfaceClearance = 1f;

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

    private static PlayerDiveController? GetVisualDiver(
        GameCamera? gameCamera,
        PlayerDiveController? diver)
    {
        if (gameCamera == null || gameCamera.m_camera == null)
        {
            return null;
        }

        if (diver == null
            || !ShouldUseUnderwaterVisuals(diver)
            || !ShouldAllowUnderwaterCamera(diver))
        {
            return null;
        }

        return diver;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateCamera))]
    private static void GameCameraUpdateCameraPrefix(GameCamera __instance)
    {
        UnderwaterSurfaceRenderer.ResetStale();
        PlayerDiveController? diver = __instance.m_camera != null
            ? PlayerDiveUtils.EnsureLocalDiver()
            : null;
        if (GetVisualDiver(__instance, diver) == null)
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
        Camera? camera = __instance.m_camera;
        PlayerDiveController? diver = camera != null
            ? PlayerDiveUtils.EnsureLocalDiver()
            : null;
        ClampSubmergedCameraBelowSurface(__instance, diver);
        diver = GetVisualDiver(__instance, diver);
        if (diver == null || camera == null)
        {
            UnderwaterVisualState.ResetAll();
            return;
        }

        if (!IsCameraUnderwater(camera, diver))
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
        if (__instance.m_waterSurface == null)
        {
            UnderwaterSurfaceRenderer.Reset(__instance);
            return;
        }

        GameCamera? gameCamera = GameCamera.instance;
        Camera? camera = gameCamera != null ? gameCamera.m_camera : null;
        PlayerDiveController? diver = camera != null
            ? PlayerDiveUtils.EnsureLocalDiver()
            : null;
        diver = GetVisualDiver(gameCamera, diver);
        if (camera == null
            || diver == null
            || !IsCameraUnderwater(camera, diver))
        {
            UnderwaterSurfaceRenderer.Reset(__instance);
            return;
        }

        UnderwaterSurfaceRenderer.Apply(__instance);
    }
}
