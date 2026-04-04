using System;
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
            Material = volume.m_waterSurface.material;
            OriginalDepth = ReadMaterialDepth(Material);
            HasOriginalDepth = Material != null && Material.HasProperty(DepthPropertyId);
            OriginalUseGlobalWind = ReadMaterialFloat(Material, UseGlobalWindPropertyId);
            HasOriginalUseGlobalWind = Material != null && Material.HasProperty(UseGlobalWindPropertyId);
        }

        public WaterVolume Volume { get; }
        public Transform SurfaceTransform { get; }
        public MeshRenderer? Renderer { get; }
        public Material? Material { get; }
        public Vector3 OriginalPosition { get; }
        public Quaternion OriginalRotation { get; }
        public ShadowCastingMode OriginalShadowCastingMode { get; }
        public float[] OriginalDepth { get; }
        public bool HasOriginalDepth { get; }
        public float OriginalUseGlobalWind { get; }
        public bool HasOriginalUseGlobalWind { get; }
        public bool OverrideApplied { get; set; }
    }

    private static readonly Dictionary<int, WaterSurfaceState> WaterSurfaceStates = new();
    private static float? _originalMinWaterDistance;
    private static GameCamera? _cameraWithOverride;
    private static bool _cameraOverrideActive;
    private static bool _fogStateCaptured;
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

        if (!_cameraOverrideActive)
        {
            LogVisualState(
                "ApplyCamera",
                $"camera={DescribeGameObject(gameCamera.gameObject)}, originalMinWaterDistance={_originalMinWaterDistance:F2}, newMinWaterDistance={minWaterDistanceOverride:F2}, cameraPos={FormatVector3(gameCamera.transform.position)}");
            _cameraOverrideActive = true;
        }

        gameCamera.m_minWaterDistance = minWaterDistanceOverride;
    }

    internal static void ApplyFogOverride(PlayerDiveController diver)
    {
        if (!_fogStateCaptured)
        {
            _originalFogColor = RenderSettings.fogColor;
            _originalFogDensity = RenderSettings.fogDensity;
            _fogStateCaptured = true;
        }

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
        waterColor = ChangeColorBrightness(waterColor, diver.Player.m_swimDepth * -ServerSyncModTemplatePlugin.DefaultUnderwaterColorDarknessFactor);
        RenderSettings.fogColor = waterColor;

        float fogDensity = _originalFogDensity + (diver.Player.m_swimDepth * ServerSyncModTemplatePlugin.DefaultUnderwaterVisibilityFalloff);
        RenderSettings.fogDensity = Mathf.Clamp(
            fogDensity,
            ServerSyncModTemplatePlugin.DefaultMinimumUnderwaterMurkiness,
            ServerSyncModTemplatePlugin.DefaultMaximumUnderwaterMurkiness);

        if (!_fogOverrideActive)
        {
            LogVisualState(
                "ApplyFog",
                $"player={DescribeGameObject(diver.Player.gameObject)}, swimDepth={diver.Player.m_swimDepth:F2}, originalFogColor={FormatColor(_originalFogColor)}, newFogColor={FormatColor(RenderSettings.fogColor)}, originalFogDensity={_originalFogDensity:F4}, newFogDensity={RenderSettings.fogDensity:F4}, env={DescribeEnvironment()}");
            _fogOverrideActive = true;
        }
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
        ApplyWaterMaterialOverride(state);
        if (!state.OverrideApplied)
        {
            LogVisualState(
                "ApplyWaterSurface",
                $"volume={DescribeGameObject(volume.gameObject)}, waterSurface={DescribeGameObject(volume.m_waterSurface.gameObject)}, waterLevel={waterLevel:F2}, originalPos={FormatVector3(state.OriginalPosition)}, appliedPos={FormatVector3(volume.m_waterSurface.transform.position)}, originalEuler={FormatEuler(state.OriginalRotation.eulerAngles)}, appliedEuler={FormatEuler(volume.m_waterSurface.transform.rotation.eulerAngles)}, originalShadow={state.OriginalShadowCastingMode}, appliedShadow={volume.m_waterSurface.shadowCastingMode}, originalDepth={FormatFloatArray(state.OriginalDepth)}, appliedDepth={FormatFloatArray(ReadMaterialDepth(state.Material))}, originalUseGlobalWind={FormatMaterialFloat(state.HasOriginalUseGlobalWind ? state.OriginalUseGlobalWind : null)}, appliedUseGlobalWind={FormatMaterialFloat(ReadMaterialFloatOrNull(state.Material, UseGlobalWindPropertyId))}");
            state.OverrideApplied = true;
        }
    }

    internal static void ResetWaterSurface(WaterVolume? volume, string reason = "")
    {
        if (volume == null)
        {
            return;
        }

        if (!WaterSurfaceStates.TryGetValue(volume.GetInstanceID(), out WaterSurfaceState? state))
        {
            return;
        }

        RestoreWaterSurfaceState(state, reason);
        WaterSurfaceStates.Remove(volume.GetInstanceID());
    }

    internal static void ResetAll(string reason = "")
    {
        ResetCameraAndFog(reason);
        ResetTrackedWaterSurfacesExcept(null, reason);
    }

    internal static void ResetCameraAndFog(string reason = "")
    {
        ResetCamera(reason);
        ResetFog(reason);
    }

    internal static void ResetCamera(string reason = "")
    {
        GameCamera? camera = _cameraWithOverride != null ? _cameraWithOverride : GameCamera.instance;
        float currentMinWaterDistance = camera != null ? camera.m_minWaterDistance : float.NaN;
        if (camera != null && _originalMinWaterDistance.HasValue)
        {
            camera.m_minWaterDistance = _originalMinWaterDistance.Value;
        }

        if (_cameraOverrideActive)
        {
            LogVisualState(
                "ResetCamera",
                $"reason={reason}, camera={DescribeGameObject(camera != null ? camera.gameObject : null)}, currentMinWaterDistance={currentMinWaterDistance:F2}, restoredMinWaterDistance={(_originalMinWaterDistance ?? float.NaN):F2}");
        }

        _originalMinWaterDistance = null;
        _cameraWithOverride = null;
        _cameraOverrideActive = false;
    }

    internal static void ResetFog(string reason = "")
    {
        bool restoredFog = _fogStateCaptured;
        if (_fogStateCaptured)
        {
            if (_fogOverrideActive)
            {
                LogVisualState(
                    "ResetFog",
                    $"reason={reason}, currentFogColor={FormatColor(RenderSettings.fogColor)}, restoredFogColor={FormatColor(_originalFogColor)}, currentFogDensity={RenderSettings.fogDensity:F4}, restoredFogDensity={_originalFogDensity:F4}, env={DescribeEnvironment()}");
            }

            RenderSettings.fogColor = _originalFogColor;
            RenderSettings.fogDensity = _originalFogDensity;
            _fogStateCaptured = false;
        }

        _fogOverrideActive = false;
        if (restoredFog)
        {
            RefreshEnvironment();
        }
    }

    private static void ResetTrackedWaterSurfacesExcept(int? activeVolumeId, string reason = "")
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

            RestoreWaterSurfaceState(WaterSurfaceStates[volumeId], reason);
            WaterSurfaceStates.Remove(volumeId);
        }
    }

    private static void RestoreWaterSurfaceState(WaterSurfaceState state, string reason)
    {
        if (state.Volume == null || state.Volume.m_waterSurface == null || state.SurfaceTransform == null)
        {
            return;
        }

        if (state.OverrideApplied)
        {
            LogVisualState(
                "ResetWaterSurface",
                $"reason={reason}, volume={DescribeGameObject(state.Volume.gameObject)}, waterSurface={DescribeGameObject(state.Volume.m_waterSurface.gameObject)}, currentPos={FormatVector3(state.SurfaceTransform.position)}, restoredPos={FormatVector3(state.OriginalPosition)}, currentEuler={FormatEuler(state.SurfaceTransform.rotation.eulerAngles)}, restoredEuler={FormatEuler(state.OriginalRotation.eulerAngles)}, currentShadow={state.Volume.m_waterSurface.shadowCastingMode}, restoredShadow={state.OriginalShadowCastingMode}, currentDepth={FormatFloatArray(ReadMaterialDepth(state.Material))}, restoredDepth={FormatFloatArray(state.OriginalDepth)}, currentUseGlobalWind={FormatMaterialFloat(ReadMaterialFloatOrNull(state.Material, UseGlobalWindPropertyId))}, restoredUseGlobalWind={FormatMaterialFloat(state.HasOriginalUseGlobalWind ? state.OriginalUseGlobalWind : null)}");
        }

        state.SurfaceTransform.SetPositionAndRotation(state.OriginalPosition, state.OriginalRotation);
        if (state.Renderer != null)
        {
            state.Volume.m_waterSurface.shadowCastingMode = state.OriginalShadowCastingMode;
        }

        RestoreWaterMaterialState(state);
        state.OverrideApplied = false;
    }

    private static void ApplyWaterMaterialOverride(WaterSurfaceState state)
    {
        Material? material = state.Material;
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

    private static void RestoreWaterMaterialState(WaterSurfaceState state)
    {
        Material? material = state.Material;
        if (material == null)
        {
            return;
        }

        if (state.HasOriginalDepth)
        {
            material.SetFloatArray(DepthPropertyId, state.OriginalDepth);
        }

        if (state.HasOriginalUseGlobalWind)
        {
            material.SetFloat(UseGlobalWindPropertyId, state.OriginalUseGlobalWind);
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

    private static void LogVisualState(string stage, string message)
    {
        if (!ServerSyncModTemplatePlugin.IsUnderwaterVisualDebugLoggingEnabled())
        {
            return;
        }

        ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogInfo($"[UnderwaterVisual:{stage}] {message}");
    }

    private static string DescribeEnvironment()
    {
        if (EnvMan.instance == null)
        {
            return "null";
        }

        EnvSetup currentEnvironment = EnvMan.instance.GetCurrentEnvironment();
        return currentEnvironment != null ? currentEnvironment.m_name : "null";
    }

    private static string DescribeGameObject(GameObject? gameObject)
    {
        if (gameObject == null)
        {
            return "null";
        }

        return $"{Utils.GetPrefabName(gameObject)}#{gameObject.GetInstanceID()}";
    }

    private static string FormatVector3(Vector3 value)
    {
        return $"({value.x:F2},{value.y:F2},{value.z:F2})";
    }

    private static string FormatEuler(Vector3 value)
    {
        return $"({value.x:F2},{value.y:F2},{value.z:F2})";
    }

    private static string FormatColor(Color value)
    {
        return $"({value.r:F3},{value.g:F3},{value.b:F3},{value.a:F3})";
    }

    private static string FormatFloatArray(float[] values)
    {
        if (values.Length == 0)
        {
            return "[]";
        }

        return $"[{string.Join(",", values.Select(value => value.ToString("F2")))}]";
    }

    private static string FormatMaterialFloat(float? value)
    {
        return value.HasValue ? value.Value.ToString("F2") : "n/a";
    }

    private static float[] ReadMaterialDepth(Material? material)
    {
        if (material == null || !material.HasProperty(DepthPropertyId))
        {
            return Array.Empty<float>();
        }

        float[]? depth = material.GetFloatArray(DepthPropertyId);
        return depth != null ? depth.ToArray() : Array.Empty<float>();
    }

    private static float ReadMaterialFloat(Material? material, int propertyId)
    {
        return material != null && material.HasProperty(propertyId) ? material.GetFloat(propertyId) : 0f;
    }

    private static float? ReadMaterialFloatOrNull(Material? material, int propertyId)
    {
        return material != null && material.HasProperty(propertyId) ? material.GetFloat(propertyId) : null;
    }

    private static Color ChangeColorBrightness(Color color, float correctionFactor)
    {
        if (correctionFactor >= 0f)
        {
            return color;
        }

        correctionFactor *= -1f;
        float red = Mathf.Max(0f, color.r - (color.r * correctionFactor));
        float green = Mathf.Max(0f, color.g - (color.g * correctionFactor));
        float blue = Mathf.Max(0f, color.b - (color.b * correctionFactor));
        return new Color(red, green, blue, color.a);
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
            || !ServerSyncModTemplatePlugin.IsUnderwaterCameraFollowEnabled()
            || PlayerDiveUtils.EnsureLocalDiver() is not PlayerDiveController diver)
        {
            UnderwaterVisualState.ResetAll("camera update: feature disabled or local diver unavailable");
            return;
        }

        Camera camera = __instance.m_camera;
        if (camera == null)
        {
            UnderwaterVisualState.ResetAll("camera update: game camera has no Camera component");
            return;
        }

        bool shouldKeepCameraOverride = ShouldKeepCameraOverride(diver);
        if (!shouldKeepCameraOverride)
        {
            UnderwaterVisualState.ResetAll(
                $"camera update: shouldKeepCameraOverride={shouldKeepCameraOverride}, inWater={diver.Player.InWater()}, isSwimming={diver.Player.IsSwimming()}, shouldForceSwimming={diver.ShouldForceSwimming()}, headUnderwater={diver.IsHeadUnderwater()}, underSurface={diver.IsUnderSurface()}, isIdleInWater={diver.IsIdleInWater()}");
            return;
        }

        UnderwaterVisualState.ApplyCameraOverride(__instance);

        if (!ServerSyncModTemplatePlugin.IsUnderwaterVisualStylingEnabled())
        {
            UnderwaterVisualState.ResetFog("camera update: underwater visual styling disabled");
            return;
        }

        float waterLevelCamera = diver.Player.GetLiquidLevel();
        if (camera.transform.position.y < waterLevelCamera)
        {
            UnderwaterVisualState.ApplyFogOverride(diver);
            return;
        }

        UnderwaterVisualState.ResetFog($"camera update: camera above water surface (cameraY={camera.transform.position.y:F2}, waterLevel={waterLevelCamera:F2})");
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
            UnderwaterVisualState.ResetWaterSurface(__instance, "water volume update: prerequisites failed or feature disabled");
            return;
        }

        float waterLevelCamera = __instance.GetWaterSurface(GameCamera.instance.transform.position);
        bool cameraUnderwater = GameCamera.instance.transform.position.y < waterLevelCamera;
        if (!cameraUnderwater || !Player.m_localPlayer.IsSwimming())
        {
            UnderwaterVisualState.ResetWaterSurface(__instance, $"water volume update: cameraUnderwater={cameraUnderwater}, playerSwimming={Player.m_localPlayer.IsSwimming()}");
            return;
        }

        UnderwaterVisualState.ApplyWaterSurfaceOverride(__instance, waterLevelCamera);
    }
}
