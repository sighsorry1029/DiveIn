using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace ServerSyncModTemplate;

public partial class ServerSyncModTemplatePlugin
{
    internal const float DefaultUnderwaterDarknessFactor = 0.5f;
    internal const float DefaultUnderwaterVisibilityFalloff = 0.25f;
    internal const float DefaultUnderwaterCameraMinWaterDistance = -5000f;

    internal static ConfigEntry<string> _waterEquipmentBlacklist = null!;
    internal static ConfigEntry<float> _surfaceStaminaRegenRateMultiplier = null!;
    internal static ConfigEntry<float> _midwaterStaminaRegenRateMultiplier = null!;
    internal static ConfigEntry<float> _surfaceEitrRegenRateMultiplier = null!;
    internal static ConfigEntry<float> _midwaterEitrRegenRateMultiplier = null!;
    internal static ConfigEntry<float> _midwaterIdleStaminaDrainPerDepth = null!;
    internal static ConfigEntry<float> _swimStaminaDrainMultiplierPerDepth = null!;
    internal static ConfigEntry<Toggle> _multiplicativeSwimStaminaModifiers = null!;
    internal static ConfigEntry<float> _swimStaminaDrainBaseMultiplier = null!;
    internal static ConfigEntry<float> _playerSwimSkillSpeedMultiplier = null!;
    internal static ConfigEntry<float> _fastSwimSpeedMultiplier = null!;
    internal static ConfigEntry<float> _fastSwimStaminaDrainMultiplier = null!;
    internal static ConfigEntry<KeyboardShortcut> _playerDiveAscendShortcut = null!;
    internal static ConfigEntry<KeyboardShortcut> _playerDiveDescendShortcut = null!;
    internal static ConfigEntry<float> _underwaterDarknessFactor = null!;
    internal static ConfigEntry<float> _underwaterVisibilityFalloff = null!;

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
        _surfaceStaminaRegenRateMultiplier = config(
            "3 - Regen Rate",
            "Surface Stamina Regen Rate",
            0.5f,
            new ConfigDescription(
                "Multiplier applied to vanilla stamina regeneration while swimming on the surface with your head above water. 0 matches vanilla swimming behavior, 1 matches normal non-swimming stamina regeneration timing and rate.",
                new AcceptableValueRange<float>(0f, 1f),
                new ConfigurationManagerAttributes { Order = 110 }));
        _midwaterStaminaRegenRateMultiplier = config(
            "3 - Regen Rate",
            "Midwater Stamina Regen Rate",
            0f,
            new ConfigDescription(
                "Multiplier applied to vanilla stamina regeneration while your head is underwater. 0 makes stamina recover only after surfacing.",
                new AcceptableValueRange<float>(0f, 1f),
                new ConfigurationManagerAttributes { Order = 109 }));
        _surfaceEitrRegenRateMultiplier = config(
            "3 - Regen Rate",
            "Surface Eitr Regen Rate",
            0.7f,
            new ConfigDescription(
                "Multiplier applied to vanilla eitr regeneration while swimming on the surface with your head above water. 0 disables eitr regeneration while surface swimming, 1 keeps vanilla eitr regeneration.",
                new AcceptableValueRange<float>(0f, 1f),
                new ConfigurationManagerAttributes { Order = 108 }));
        _midwaterEitrRegenRateMultiplier = config(
            "3 - Regen Rate",
            "Midwater Eitr Regen Rate",
            0.3f,
            new ConfigDescription(
                "Multiplier applied to vanilla eitr regeneration while your head is underwater. 0 makes eitr recover only after surfacing.",
                new AcceptableValueRange<float>(0f, 1f),
                new ConfigurationManagerAttributes { Order = 107 }));
        _midwaterIdleStaminaDrainPerDepth = config(
            "4 - Stamina Drain",
            "Midwater Idle Stamina Drain Per Depth",
            0.02f,
            new ConfigDescription(
                "Idle stamina drained per second per 1m of current liquid depth while your head is underwater. 0 disables idle underwater stamina drain. Example: 0.1 drains 3 stamina per second at 30m depth.",
                new AcceptableValueRange<float>(0f, 1f),
                new ConfigurationManagerAttributes { Order = 109 }));
        _swimStaminaDrainMultiplierPerDepth = config(
            "4 - Stamina Drain",
            "Swim Stamina Drain Multiplier Per Depth",
            2.5f,
            new ConfigDescription(
                "Additional moving swim stamina drain percent per 1m of current liquid depth. 1 means 30% extra at 30m; 2.5 means 75% extra at 30m. Applied multiplicatively with base and Fast Swim stamina drain.",
                new AcceptableValueRange<float>(0f, 5f),
                new ConfigurationManagerAttributes { Order = 108 }));
        _multiplicativeSwimStaminaModifiers = config(
            "4 - Stamina Drain",
            "Multiplicative Swim Stamina Modifiers",
            Toggle.On,
            new ConfigDescription(
                "If on, status-effect swim stamina use modifiers stack multiplicatively during actual swim stamina consumption. Off is vanilla. Example: -50% and -60% leaves 20% cost instead of 0%. Tooltips keep vanilla display behavior.",
                null,
                new ConfigurationManagerAttributes { Order = 110 }));
        _swimStaminaDrainBaseMultiplier = config(
            "4 - Stamina Drain",
            "Swim Stamina Drain Base Multiplier",
            1f,
            new ConfigDescription(
                "Multiplier applied to vanilla moving swim stamina drain before depth and Fast Swim multipliers. 1 keeps vanilla cost, 0.5 halves it, 2 doubles it.",
                new AcceptableValueRange<float>(0.1f, 2f),
                new ConfigurationManagerAttributes { Order = 107 }));
        _fastSwimSpeedMultiplier = config(
            "5 - Swim Speed",
            "Fast Swim Speed Multiplier",
            2f,
            new ConfigDescription(
                "Swim speed multiplier while Fast Swim is toggled on with the vanilla run key. 1 disables Fast Swim and hides its key hint. Swim skill separately increases base swim speed.",
                new AcceptableValueRange<float>(1f, 3f),
                new ConfigurationManagerAttributes { Order = 109 }));
        _fastSwimStaminaDrainMultiplier = config(
            "5 - Swim Speed",
            "Fast Swim Stamina Drain Multiplier",
            2f,
            new ConfigDescription(
                "Moving swim stamina drain multiplier while Fast Swim is toggled on. Applied multiplicatively with base and depth stamina drain.",
                new AcceptableValueRange<float>(1f, 5f),
                new ConfigurationManagerAttributes { Order = 108 }));
        _playerSwimSkillSpeedMultiplier = config(
            "5 - Swim Speed",
            "Swim Skill Speed Multiplier",
            1.5f,
            new ConfigDescription(
                "Base swim speed multiplier at Swim skill 100. 1.5 means +50%.",
                new AcceptableValueRange<float>(1f, 3f),
                new ConfigurationManagerAttributes { Order = 110 }));
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
        _underwaterDarknessFactor = config(
            "2 - Player Diving",
            "Darkness Factor",
            DefaultUnderwaterDarknessFactor,
            new ConfigDescription(
                "Underwater darkness added per meter of swim depth. 1 means 1% per meter, so 30m gives 30%.",
                new AcceptableValueRange<float>(0f, 3f),
                new ConfigurationManagerAttributes { Order = 92 }),
            synchronizedSetting: true);
        _underwaterVisibilityFalloff = config(
            "2 - Player Diving",
            "Murkiness Factor",
            DefaultUnderwaterVisibilityFalloff,
            new ConfigDescription(
                "Underwater fog density added per meter of swim depth. 1 means 1% per meter, so 30m adds 30%.",
                new AcceptableValueRange<float>(0f, 3f),
                new ConfigurationManagerAttributes { Order = 91 }),
            synchronizedSetting: true);
    }

    internal static bool IsUnderwaterVisualStylingEnabled()
    {
        return true;
    }

    internal static float GetUnderwaterCameraMinWaterDistance()
    {
        return DefaultUnderwaterCameraMinWaterDistance;
    }

    internal static float GetUnderwaterDarknessFactor()
    {
        return Mathf.Max(0f, _underwaterDarknessFactor.Value) * 0.01f;
    }

    internal static float GetUnderwaterVisibilityFalloff()
    {
        return Mathf.Max(0f, _underwaterVisibilityFalloff.Value) * 0.01f;
    }

    internal static bool IsSwimRunEnabled()
    {
        return _fastSwimSpeedMultiplier != null && _fastSwimSpeedMultiplier.Value > 1.001f;
    }

    internal static bool UseMultiplicativeSwimStaminaModifiers()
    {
        return _multiplicativeSwimStaminaModifiers?.Value == Toggle.On;
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
        if (ShouldShowGamepadKeyHints())
        {
            return GetBoundKeyHint("JoyJump", "A");
        }

        return FormatShortcutForKeyHint(_playerDiveAscendShortcut?.Value ?? new KeyboardShortcut(KeyCode.Space));
    }

    internal static string GetDiveDescendKeyHint()
    {
        if (ShouldShowGamepadKeyHints())
        {
            return GetBoundKeyHint("JoyCrouch", "B");
        }

        return FormatShortcutForKeyHint(_playerDiveDescendShortcut?.Value ?? new KeyboardShortcut(KeyCode.LeftControl));
    }

    internal static string GetDiveRunKeyHint()
    {
        string keyHint = ShouldShowGamepadKeyHints()
            ? GetBoundKeyHint("JoyRun", "LT")
            : GetBoundKeyHint("Run", "Left Shift");
        return keyHint;
    }

    private static bool ShouldShowGamepadKeyHints()
    {
        return ZInput.IsGamepadActive();
    }

    private static string GetBoundKeyHint(string bindingName, string fallback)
    {
        string keyHint = ZInput.instance?.GetBoundKeyString(bindingName, true) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(keyHint))
        {
            return fallback;
        }

        return Localization.instance != null ? Localization.instance.Localize(keyHint) : keyHint;
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
