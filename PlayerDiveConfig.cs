using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace ServerSyncModTemplate;

public partial class ServerSyncModTemplatePlugin
{
    internal const float DefaultUnderwaterDarknessFactor = 2f;
    internal const float DefaultMinimumUnderwaterDarkness = 0f;
    internal const float DefaultMaximumUnderwaterDarkness = 1f;
    internal const float DefaultUnderwaterVisibilityFalloff = 0f;
    internal const float DefaultMinimumUnderwaterMurkiness = 0.05f;
    internal const float DefaultMaximumUnderwaterMurkiness = 1f;
    internal const float DefaultUnderwaterCameraMinWaterDistance = -5000f;

    internal static ConfigEntry<string> _waterEquipmentBlacklist = null!;
    internal static ConfigEntry<float> _waterStaminaRegenRateMultiplier = null!;
    internal static ConfigEntry<float> _waterDepthStaminaDrainStart = null!;
    internal static ConfigEntry<float> _waterDepthStaminaDrainFull = null!;
    internal static ConfigEntry<float> _waterDepthStaminaDrainMaxMultiplier = null!;
    internal static ConfigEntry<float> _playerSwimRunSpeedMultiplier = null!;
    internal static ConfigEntry<KeyboardShortcut> _playerDiveAscendShortcut = null!;
    internal static ConfigEntry<KeyboardShortcut> _playerDiveDescendShortcut = null!;
    internal static ConfigEntry<Toggle> _enableUnderwaterVisualStyling = null!;
    internal static ConfigEntry<float> _underwaterDarknessFactor = null!;
    internal static ConfigEntry<float> _minimumUnderwaterDarkness = null!;
    internal static ConfigEntry<float> _maximumUnderwaterDarkness = null!;
    internal static ConfigEntry<float> _underwaterVisibilityFalloff = null!;
    internal static ConfigEntry<float> _minimumUnderwaterMurkiness = null!;
    internal static ConfigEntry<float> _maximumUnderwaterMurkiness = null!;

    private static readonly object WaterEquipmentBlacklistLock = new();
    private static string _lastWaterEquipmentBlacklistRaw = string.Empty;
    private static HashSet<string> _waterEquipmentBlacklistSet = new(StringComparer.OrdinalIgnoreCase);

    private void InitializePlayerDiveConfig()
    {
        _waterEquipmentBlacklist = config(
            "2 - Player Diving",
            "Water Equipment Blacklist",
            "",
            new ConfigDescription(
                "Comma-separated item prefab names that remain restricted in water. Everything not listed is allowed in water by default. Example: BowFineWood,ShieldBronzeBuckler.",
                null,
                new ConfigurationManagerAttributes { Order = 100 }));
        _waterStaminaRegenRateMultiplier = config(
            "2 - Player Diving",
            "Water Stamina Regen Rate",
            0.5f,
            new ConfigDescription(
                "Multiplier applied to vanilla stamina regeneration while swimming or diving in water. 0 matches vanilla swimming behavior (effective stamina regeneration stays at 0), 1 matches vanilla normal non-swimming stamina regeneration timing and rate.",
                new AcceptableValueRange<float>(0f, 2f),
                new ConfigurationManagerAttributes { Order = 99 }));
        _waterDepthStaminaDrainStart = config(
            "2 - Player Diving",
            "Water Depth Stamina Drain Start",
            3f,
            new ConfigDescription(
                "Depth in meters below the surface where extra swim stamina drain begins.",
                new AcceptableValueRange<float>(0f, 50f),
                new ConfigurationManagerAttributes { Order = 98 }));
        _waterDepthStaminaDrainFull = config(
            "2 - Player Diving",
            "Water Depth Stamina Drain Full",
            30f,
            new ConfigDescription(
                "Depth in meters below the surface where the maximum extra swim stamina drain multiplier is reached.",
                new AcceptableValueRange<float>(0.25f, 300f),
                new ConfigurationManagerAttributes { Order = 97 }));
        _waterDepthStaminaDrainMaxMultiplier = config(
            "2 - Player Diving",
            "Water Depth Stamina Drain Max Multiplier",
            1.5f,
            new ConfigDescription(
                "Maximum multiplier applied to vanilla moving swim stamina drain at or below the full depth.",
                new AcceptableValueRange<float>(1f, 5f),
                new ConfigurationManagerAttributes { Order = 96 }));
        _playerSwimRunSpeedMultiplier = config(
            "2 - Player Diving",
            "Swim Run Speed Multiplier",
            1.5f,
            new ConfigDescription(
                "Final swim speed while swimming and holding the run key = base swim speed x [1 + (this value - 1) x (Swim skill level / 100)^1.5].",
                new AcceptableValueRange<float>(1f, 3f),
                new ConfigurationManagerAttributes { Order = 95 }));
        _playerDiveAscendShortcut = config(
            "2 - Player Diving",
            "Dive Ascend Key",
            new KeyboardShortcut(KeyCode.Space),
            new ConfigDescription(
                "Client-side key used to ascend while swimming underwater.",
                new AcceptableShortcuts(),
                new ConfigurationManagerAttributes { Order = 110 }),
            synchronizedSetting: false);
        _playerDiveDescendShortcut = config(
            "2 - Player Diving",
            "Dive Descend Key",
            new KeyboardShortcut(KeyCode.LeftControl),
            new ConfigDescription(
                "Client-side key used to descend while swimming.",
                new AcceptableShortcuts(),
                new ConfigurationManagerAttributes { Order = 109 }),
            synchronizedSetting: false);
        _enableUnderwaterVisualStyling = config(
            "3 - Underwater Visuals",
            "Enable Underwater Visual Styling",
            Toggle.On,
            new ConfigDescription(
                "Whether underwater fog and reversed water surface styling are applied while submerged.",
                null,
                new ConfigurationManagerAttributes { Order = 94 }),
            synchronizedSetting: false);
        _underwaterDarknessFactor = config(
            "3 - Underwater Visuals",
            "Darkness Factor",
            DefaultUnderwaterDarknessFactor,
            new ConfigDescription(
                "How quickly underwater darkness increases as swim depth increases. Values are entered as percent-style per-meter amounts, so 3.3 means 0.033 internally.",
                new AcceptableValueRange<float>(0f, 10f),
                new ConfigurationManagerAttributes { Order = 93 }),
            synchronizedSetting: false);
        _minimumUnderwaterDarkness = config(
            "3 - Underwater Visuals",
            "Minimum Darkness",
            DefaultMinimumUnderwaterDarkness,
            new ConfigDescription(
                "Minimum underwater darkness regardless of depth. 0 keeps shallow water at full brightness, 1 makes all underwater visuals fully dark.",
                new AcceptableValueRange<float>(0f, 1f),
                new ConfigurationManagerAttributes { Order = 92 }),
            synchronizedSetting: false);
        _maximumUnderwaterDarkness = config(
            "3 - Underwater Visuals",
            "Maximum Darkness",
            DefaultMaximumUnderwaterDarkness,
            new ConfigDescription(
                "Maximum underwater darkness regardless of depth. 0 disables darkening, 1 allows full darkness.",
                new AcceptableValueRange<float>(0f, 1f),
                new ConfigurationManagerAttributes { Order = 91 }),
            synchronizedSetting: false);
        _underwaterVisibilityFalloff = config(
            "3 - Underwater Visuals",
            "Murkiness Factor",
            DefaultUnderwaterVisibilityFalloff,
            new ConfigDescription(
                "How quickly underwater murkiness increases as swim depth increases. Values are entered as percent-style per-meter amounts, so 3.3 means 0.033 internally.",
                new AcceptableValueRange<float>(0f, 10f),
                new ConfigurationManagerAttributes { Order = 90 }),
            synchronizedSetting: false);
        _minimumUnderwaterMurkiness = config(
            "3 - Underwater Visuals",
            "Minimum Murkiness",
            DefaultMinimumUnderwaterMurkiness,
            new ConfigDescription(
                "Minimum underwater murkiness regardless of depth.",
                new AcceptableValueRange<float>(0f, 1f),
                new ConfigurationManagerAttributes { Order = 89 }),
            synchronizedSetting: false);
        _maximumUnderwaterMurkiness = config(
            "3 - Underwater Visuals",
            "Maximum Murkiness",
            DefaultMaximumUnderwaterMurkiness,
            new ConfigDescription(
                "Maximum underwater murkiness regardless of depth.",
                new AcceptableValueRange<float>(0f, 1f),
                new ConfigurationManagerAttributes { Order = 88 }),
            synchronizedSetting: false);
    }

    internal static bool IsUnderwaterVisualStylingEnabled()
    {
        return _enableUnderwaterVisualStyling.Value == Toggle.On;
    }

    internal static float GetUnderwaterCameraMinWaterDistance()
    {
        return DefaultUnderwaterCameraMinWaterDistance;
    }

    internal static float GetUnderwaterDarknessFactor()
    {
        return Mathf.Max(0f, _underwaterDarknessFactor.Value) * 0.01f;
    }

    internal static float GetMinimumUnderwaterDarkness()
    {
        return Mathf.Clamp(_minimumUnderwaterDarkness.Value, 0f, 1f);
    }

    internal static float GetMaximumUnderwaterDarkness()
    {
        return Mathf.Max(GetMinimumUnderwaterDarkness(), Mathf.Clamp(_maximumUnderwaterDarkness.Value, 0f, 1f));
    }

    internal static float GetUnderwaterVisibilityFalloff()
    {
        return Mathf.Max(0f, _underwaterVisibilityFalloff.Value) * 0.01f;
    }

    internal static float GetMinimumUnderwaterMurkiness()
    {
        return Mathf.Clamp(_minimumUnderwaterMurkiness.Value, 0f, 5f);
    }

    internal static float GetMaximumUnderwaterMurkiness()
    {
        return Mathf.Max(GetMinimumUnderwaterMurkiness(), _maximumUnderwaterMurkiness.Value);
    }

    internal static bool IsDiveAscendInputHeld()
    {
        return (_playerDiveAscendShortcut?.Value.IsKeyHeld() ?? false) || ZInput.GetButton("JoyJump");
    }

    internal static bool IsDiveDescendInputHeld()
    {
        return (_playerDiveDescendShortcut?.Value.IsKeyHeld() ?? false) || ZInput.GetButton("JoyCrouch");
    }

    internal static string GetDiveAscendKeyHint()
    {
        return FormatShortcutForKeyHint(_playerDiveAscendShortcut?.Value ?? new KeyboardShortcut(KeyCode.Space));
    }

    internal static string GetDiveDescendKeyHint()
    {
        return FormatShortcutForKeyHint(_playerDiveDescendShortcut?.Value ?? new KeyboardShortcut(KeyCode.LeftControl));
    }

    private static string FormatShortcutForKeyHint(KeyboardShortcut shortcut)
    {
        if (shortcut.MainKey == KeyCode.None)
        {
            return "None";
        }

        List<string> keys = shortcut.Modifiers
            .Where(key => key != KeyCode.None)
            .Select(FormatKeyCodeForHint)
            .ToList();
        keys.Add(FormatKeyCodeForHint(shortcut.MainKey));
        return string.Join(" + ", keys);
    }

    private static string FormatKeyCodeForHint(KeyCode key)
    {
        return key switch
        {
            KeyCode.LeftControl => "Left Ctrl",
            KeyCode.RightControl => "Right Ctrl",
            KeyCode.LeftShift => "Left Shift",
            KeyCode.RightShift => "Right Shift",
            KeyCode.LeftAlt => "Left Alt",
            KeyCode.RightAlt => "Right Alt",
            KeyCode.Mouse0 => "Mouse-1",
            KeyCode.Mouse1 => "Mouse-2",
            KeyCode.Mouse2 => "Mouse-3",
            KeyCode.Mouse3 => "Mouse-4",
            KeyCode.Mouse4 => "Mouse-5",
            KeyCode.Mouse5 => "Mouse-6",
            KeyCode.Mouse6 => "Mouse-7",
            _ => key.ToString()
        };
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
