using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace ServerSyncModTemplate;

public partial class ServerSyncModTemplatePlugin
{
    internal const float DefaultUnderwaterColorDarknessFactor = 0.02f;
    internal const float DefaultUnderwaterVisibilityFalloff = 0f;
    internal const float DefaultMinimumUnderwaterMurkiness = 0.05f;
    internal const float DefaultMaximumUnderwaterMurkiness = 1f;

    internal static ConfigEntry<string> _waterEquipmentBlacklist = null!;
    internal static ConfigEntry<float> _waterStaminaRegenRateMultiplier = null!;
    internal static ConfigEntry<float> _waterDepthStaminaDrainStart = null!;
    internal static ConfigEntry<float> _waterDepthStaminaDrainFull = null!;
    internal static ConfigEntry<float> _waterDepthStaminaDrainMaxMultiplier = null!;
    internal static ConfigEntry<float> _playerSwimRunSpeedMultiplier = null!;
    internal static ConfigEntry<float> _underwaterCameraMinWaterDistance = null!;
    internal static ConfigEntry<Toggle> _debugUnderwaterVisualLogging = null!;

    private static readonly object WaterEquipmentBlacklistLock = new();
    private static string _lastWaterEquipmentBlacklistRaw = string.Empty;
    private static HashSet<string> _waterEquipmentBlacklistSet = new(StringComparer.OrdinalIgnoreCase);

    private void InitializePlayerDiveConfig()
    {
        _waterEquipmentBlacklist = config(
            "2a - Player Diving",
            "Water Equipment Blacklist",
            "",
            new ConfigDescription(
                "Comma-separated item prefab names that remain restricted in water. Everything not listed is allowed in water by default. Example: BowFineWood,ShieldBronzeBuckler.",
                null,
                new ConfigurationManagerAttributes { Order = 100 }));
        _waterStaminaRegenRateMultiplier = config(
            "2a - Player Diving",
            "Water Stamina Regen Rate",
            0.5f,
            new ConfigDescription(
                "Multiplier applied to vanilla stamina regeneration while swimming or diving in water. 0 matches vanilla swimming behavior (effective stamina regeneration stays at 0), 1 matches vanilla normal non-swimming stamina regeneration timing and rate.",
                new AcceptableValueRange<float>(0f, 2f),
                new ConfigurationManagerAttributes { Order = 99 }));
        _waterDepthStaminaDrainStart = config(
            "2a - Player Diving",
            "Water Depth Stamina Drain Start",
            2.5f,
            new ConfigDescription(
                "Depth in meters below the surface where extra swim stamina drain begins.",
                new AcceptableValueRange<float>(0f, 50f),
                new ConfigurationManagerAttributes { Order = 98 }));
        _waterDepthStaminaDrainFull = config(
            "2a - Player Diving",
            "Water Depth Stamina Drain Full",
            30f,
            new ConfigDescription(
                "Depth in meters below the surface where the maximum extra swim stamina drain multiplier is reached.",
                new AcceptableValueRange<float>(0.25f, 100f),
                new ConfigurationManagerAttributes { Order = 97 }));
        _waterDepthStaminaDrainMaxMultiplier = config(
            "2a - Player Diving",
            "Water Depth Stamina Drain Max Multiplier",
            2f,
            new ConfigDescription(
                "Maximum multiplier applied to vanilla moving swim stamina drain at or below the full depth.",
                new AcceptableValueRange<float>(1f, 5f),
                new ConfigurationManagerAttributes { Order = 96 }));
        _playerSwimRunSpeedMultiplier = config(
            "2a - Player Diving",
            "Swim Run Speed Multiplier",
            1.5f,
            new ConfigDescription(
                "Multiplier applied to vanilla base swim speed while holding the run key underwater. 1 matches vanilla swim speed, 1.5 matches swim speed 3 with vanilla base swim speed 2.",
                new AcceptableValueRange<float>(1f, 3f),
                new ConfigurationManagerAttributes { Order = 95 }));
        _underwaterCameraMinWaterDistance = config(
            "4 - Debug",
            "Underwater Camera Min Water Distance",
            -5000f,
            new ConfigDescription(
                "Client-only underwater camera follow override. More negative values force the camera further through the water surface, while less negative values can reduce surface glitches. Test values like -1000 if needed.",
                new AcceptableValueRange<float>(-5000f, -100f),
                new ConfigurationManagerAttributes { Order = 11 }),
            synchronizedSetting: false);
        _debugUnderwaterVisualLogging = config(
            "4 - Debug",
            "Log Underwater Visual State",
            Toggle.Off,
            new ConfigDescription(
                "Logs underwater camera, fog, and water surface apply/reset state for debugging.",
                null,
                new ConfigurationManagerAttributes { Order = 10 }),
            synchronizedSetting: false);
    }

    internal static bool IsPlayerDivingEnabled()
    {
        return true;
    }

    internal static bool IsUnderwaterCameraFollowEnabled()
    {
        return IsPlayerDiveEnvAllowed();
    }

    internal static bool IsUnderwaterVisualStylingEnabled()
    {
        return IsPlayerDiveEnvAllowed();
    }

    internal static bool IsUnderwaterVisualDebugLoggingEnabled()
    {
        return _debugUnderwaterVisualLogging.Value == Toggle.On;
    }

    internal static float GetUnderwaterCameraMinWaterDistance()
    {
        return Mathf.Clamp(_underwaterCameraMinWaterDistance.Value, -5000f, -100f);
    }

    internal static bool IsPlayerDiveEnvAllowed()
    {
        return true;
    }

    internal static bool IsWaterRestrictedItem(ItemDrop.ItemData? item)
    {
        if (item == null || item.m_dropPrefab == null)
        {
            return false;
        }

        RefreshWaterEquipmentBlacklistIfNeeded();
        string prefabName = Utils.GetPrefabName(item.m_dropPrefab);
        return !string.IsNullOrEmpty(prefabName) && _waterEquipmentBlacklistSet.Contains(prefabName);
    }

    internal static bool HumanoidHasWaterRestrictedEquipment(Humanoid? humanoid)
    {
        if (humanoid == null)
        {
            return false;
        }

        return IsWaterRestrictedItem(humanoid.m_rightItem)
               || IsWaterRestrictedItem(humanoid.m_hiddenRightItem)
               || IsWaterRestrictedItem(humanoid.m_leftItem)
               || IsWaterRestrictedItem(humanoid.m_hiddenLeftItem)
               || IsWaterRestrictedItem(humanoid.m_chestItem)
               || IsWaterRestrictedItem(humanoid.m_legItem)
               || IsWaterRestrictedItem(humanoid.m_helmetItem)
               || IsWaterRestrictedItem(humanoid.m_shoulderItem)
               || IsWaterRestrictedItem(humanoid.m_utilityItem)
               || IsWaterRestrictedItem(humanoid.m_trinketItem);
    }

    private static void RefreshWaterEquipmentBlacklistIfNeeded(bool force = false)
    {
        string raw = _waterEquipmentBlacklist?.Value ?? string.Empty;
        if (!force && string.Equals(raw, _lastWaterEquipmentBlacklistRaw, StringComparison.Ordinal))
        {
            return;
        }

        lock (WaterEquipmentBlacklistLock)
        {
            if (!force && string.Equals(raw, _lastWaterEquipmentBlacklistRaw, StringComparison.Ordinal))
            {
                return;
            }

            _waterEquipmentBlacklistSet = raw
                .Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _lastWaterEquipmentBlacklistRaw = raw;
        }
    }
}
