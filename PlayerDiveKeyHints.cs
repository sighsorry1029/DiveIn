using System;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ServerSyncModTemplate;

[HarmonyPatch]
internal static class PlayerDiveKeyHints
{
    private const string SwimmingHintsRootName = "DiveIn_SwimmingHints";
    private const string RunHintName = "DiveIn_RunHint";
    private const string DescendHintName = "DiveIn_DescendHint";
    private const string AscendHintName = "DiveIn_AscendHint";

    private static KeyHints? _owner;
    private static DiveHintSet? _swimmingHints;
    private static DiveHintSet? _combatHints;
    private static InputHintMode _hintMode;
    private static DiveHintSnapshot _lastHintSnapshot;
    private static bool _hasLastHintSnapshot;
    private static readonly string[] KeyTextNameTokens = { "key", "bind", "binding", "shortcut", "input", "button" };
    private static readonly string[] LabelTextNameTokens = { "label", "action", "name", "title" };

    private enum InputHintMode
    {
        Keyboard,
        Gamepad
    }

    private readonly struct DiveHintSnapshot
    {
        public DiveHintSnapshot(
            bool showFastSwimHint,
            string fastSwimLabel,
            string runKey,
            string descendKey,
            string ascendKey,
            bool showCombatHints,
            bool showSwimmingHints)
        {
            ShowFastSwimHint = showFastSwimHint;
            FastSwimLabel = fastSwimLabel;
            RunKey = runKey;
            DescendKey = descendKey;
            AscendKey = ascendKey;
            ShowCombatHints = showCombatHints;
            ShowSwimmingHints = showSwimmingHints;
        }

        public bool ShowFastSwimHint { get; }
        public string FastSwimLabel { get; }
        public string RunKey { get; }
        public string DescendKey { get; }
        public string AscendKey { get; }
        public bool ShowCombatHints { get; }
        public bool ShowSwimmingHints { get; }

        public bool Matches(DiveHintSnapshot other)
        {
            return ShowFastSwimHint == other.ShowFastSwimHint
                   && FastSwimLabel == other.FastSwimLabel
                   && RunKey == other.RunKey
                   && DescendKey == other.DescendKey
                   && AscendKey == other.AscendKey
                   && ShowCombatHints == other.ShowCombatHints
                   && ShowSwimmingHints == other.ShowSwimmingHints;
        }
    }

    private sealed class DiveHintCell
    {
        public DiveHintCell(GameObject root)
        {
            Root = root;
            TMP_Text[] texts = root
                .GetComponentsInChildren<TMP_Text>(true)
                .ToArray();

            Key = FindKeyText(texts);
            Label = FindLabelText(texts, Key);
            ExtraTexts = texts
                .Where(text => text != Label && text != Key)
                .ToArray();
        }

        public GameObject Root { get; }
        private TMP_Text? Label { get; }
        private TMP_Text? Key { get; }
        private TMP_Text[] ExtraTexts { get; }

        public bool IsValid => Root && Label != null && Key != null;

        public void Configure(string label, string keyText)
        {
            if (!IsValid)
            {
                return;
            }

            Label!.gameObject.SetActive(true);
            Label.text = label;
            foreach (TMP_Text extraText in ExtraTexts)
            {
                extraText.gameObject.SetActive(false);
            }

            Key!.gameObject.SetActive(true);
            Key.text = keyText;
        }

        public void SetActive(bool active)
        {
            if (Root)
            {
                Root.SetActive(active);
            }
        }

        public void Destroy()
        {
            if (Root)
            {
                UnityEngine.Object.Destroy(Root);
            }
        }
    }

    private sealed class DiveHintSet
    {
        public DiveHintSet(GameObject? root, DiveHintCell runHint, DiveHintCell descendHint, DiveHintCell ascendHint)
        {
            Root = root;
            RunHint = runHint;
            DescendHint = descendHint;
            AscendHint = ascendHint;
        }

        public GameObject? Root { get; }
        public DiveHintCell RunHint { get; }
        public DiveHintCell DescendHint { get; }
        public DiveHintCell AscendHint { get; }
        private bool ShowRunHint { get; set; } = true;

        public bool IsValid => RunHint.IsValid && DescendHint.IsValid && AscendHint.IsValid && (Root == null || Root);

        public void Configure(bool showRunHint, string fastSwimLabel, string runKey, string descendKey, string ascendKey)
        {
            ShowRunHint = showRunHint;
            if (showRunHint)
            {
                RunHint.Configure(fastSwimLabel, runKey);
            }
            else
            {
                RunHint.SetActive(false);
            }

            DescendHint.Configure(DiveLocalization.Localize(DiveLocalization.DescendKey), descendKey);
            AscendHint.Configure(DiveLocalization.Localize(DiveLocalization.AscendKey), ascendKey);
        }

        public void SetActive(bool active)
        {
            RunHint.SetActive(active && ShowRunHint);
            DescendHint.SetActive(active);
            AscendHint.SetActive(active);
            if (Root)
            {
                Root!.SetActive(active);
            }
        }

        public void RebuildLayout()
        {
            PlayerDiveKeyHints.RebuildLayout(Root != null ? Root : DescendHint.Root);
        }

        public void Destroy()
        {
            if (Root)
            {
                UnityEngine.Object.Destroy(Root);
                return;
            }

            RunHint.Destroy();
            DescendHint.Destroy();
            AscendHint.Destroy();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(KeyHints), nameof(KeyHints.Awake))]
    private static void KeyHintsAwakePostfix(KeyHints __instance)
    {
        EnsureHints(__instance);
        UpdateDiveHints(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(KeyHints), nameof(KeyHints.UpdateHints))]
    private static void KeyHintsUpdateHintsPostfix(KeyHints __instance)
    {
        UpdateDiveHints(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(KeyHints), nameof(KeyHints.ApplySettings))]
    private static void KeyHintsApplySettingsPostfix(KeyHints __instance)
    {
        UpdateDiveHints(__instance);
    }

    private static void UpdateDiveHints(KeyHints keyHints)
    {
        Player player = Player.m_localPlayer;
        if (!CanShowKeyHints(keyHints, player) ||
            !PlayerDiveUtils.TryGetLocalDiver(player, out PlayerDiveController diver) ||
            !diver.ShouldShowDiveKeyHints())
        {
            HideDiveHints();
            return;
        }

        if (!EnsureHints(keyHints))
        {
            HideDiveHints();
            return;
        }

        bool showFastSwimHint = ServerSyncModTemplatePlugin.IsSwimRunEnabled();
        string runKey = showFastSwimHint ? ServerSyncModTemplatePlugin.GetDiveRunKeyHint() : string.Empty;
        string fastSwimLabel = DiveLocalization.Localize(diver.IsFastSwimEnabled()
            ? DiveLocalization.FastSwimOnKey
            : DiveLocalization.FastSwimOffKey);
        string descendKey = ServerSyncModTemplatePlugin.GetDiveDescendKeyHint();
        string ascendKey = ServerSyncModTemplatePlugin.GetDiveAscendKeyHint();
        bool showCombatHints = keyHints.m_combatHints != null && keyHints.m_combatHints.activeSelf;
        bool showSwimmingHints = !showCombatHints && HasNoVisibleHandItems(player);

        DiveHintSnapshot snapshot = new(
            showFastSwimHint,
            fastSwimLabel,
            runKey,
            descendKey,
            ascendKey,
            showCombatHints,
            showSwimmingHints);
        if (_hasLastHintSnapshot && _lastHintSnapshot.Matches(snapshot))
        {
            return;
        }

        _lastHintSnapshot = snapshot;
        _hasLastHintSnapshot = true;

        _swimmingHints?.Configure(showFastSwimHint, fastSwimLabel, runKey, descendKey, ascendKey);
        _combatHints?.Configure(showFastSwimHint, fastSwimLabel, runKey, descendKey, ascendKey);

        _combatHints?.SetActive(showCombatHints);
        _swimmingHints?.SetActive(showSwimmingHints);
        _combatHints?.RebuildLayout();
        _swimmingHints?.RebuildLayout();
    }

    private static bool CanShowKeyHints(KeyHints keyHints, Player player)
    {
        if (keyHints == null || !keyHints.m_keyHintsEnabled || player == null || player.IsDead())
        {
            return false;
        }

        if (Chat.instance != null && Chat.instance.IsChatDialogWindowVisible())
        {
            return false;
        }

        if (Game.IsPaused() || InventoryGui.IsVisible())
        {
            return false;
        }

        if (InventoryGui.instance != null &&
            (InventoryGui.instance.IsSkillsPanelOpen || InventoryGui.instance.IsTrophisPanelOpen || InventoryGui.instance.IsTextPanelOpen))
        {
            return false;
        }

        if (Hud.instance != null && Hud.instance.m_radialMenu.Active)
        {
            return false;
        }

        return !player.InPlaceMode()
               && !PlayerCustomizaton.IsBarberGuiVisible()
               && player.GetDoodadController() == null;
    }

    private static bool HasNoVisibleHandItems(Player player)
    {
        return player.m_rightItem == null
               && player.m_leftItem == null;
    }

    private static bool EnsureHints(KeyHints keyHints)
    {
        if (keyHints == null || keyHints.m_combatHints == null)
        {
            return false;
        }

        InputHintMode currentMode = GetInputHintMode();
        if (_owner == keyHints &&
            _hintMode == currentMode &&
            _swimmingHints?.IsValid == true &&
            _combatHints?.IsValid == true)
        {
            return true;
        }

        DestroyHints();
        _owner = keyHints;
        _hintMode = currentMode;
        _swimmingHints = CreateSwimmingHints(keyHints, currentMode);
        _combatHints = CreateCombatHints(keyHints, currentMode);
        return _swimmingHints?.IsValid == true && _combatHints?.IsValid == true;
    }

    private static DiveHintSet? CreateSwimmingHints(KeyHints keyHints, InputHintMode mode)
    {
        Transform parent = keyHints.m_combatHints.transform.parent;
        GameObject root = UnityEngine.Object.Instantiate(keyHints.m_combatHints, parent, false);
        root.name = SwimmingHintsRootName;
        root.transform.SetSiblingIndex(keyHints.m_combatHints.transform.GetSiblingIndex());

        Transform hintParent = GetHintParent(root, mode);
        for (int i = 0; i < root.transform.childCount; ++i)
        {
            Transform child = root.transform.GetChild(i);
            child.gameObject.SetActive(child == hintParent);
        }

        GameObject[] hintCells = GetTemplateHintCells(hintParent);
        DiveHintSet? hintSet = CreateHintSet(hintParent, root, hintCells, true, "swimming");
        if (hintSet == null)
        {
            root.SetActive(false);
            return null;
        }

        hintSet.SetActive(false);
        return hintSet;
    }

    private static DiveHintSet? CreateCombatHints(KeyHints keyHints, InputHintMode mode)
    {
        Transform hintParent = GetHintParent(keyHints.m_combatHints, mode);
        GameObject[] templates = GetTemplateHintCells(hintParent);
        DiveHintSet? hintSet = CreateHintSet(hintParent, null, templates, false, "combat");
        if (hintSet == null)
        {
            return null;
        }

        hintSet.SetActive(false);
        return hintSet;
    }

    private static DiveHintSet? CreateHintSet(
        Transform hintParent,
        GameObject? root,
        GameObject[] templateCells,
        bool hideTemplateCells,
        string context)
    {
        if (templateCells.Length == 0)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning(
                $"Failed to create DiveIn {context} key hints: no combat hint template cells found.");
            return null;
        }

        if (hideTemplateCells)
        {
            foreach (GameObject hintCell in templateCells)
            {
                hintCell.SetActive(false);
            }
        }

        DiveHintCell runHint = CreateHintCell(templateCells[0], RunHintName, hintParent, 0);
        DiveHintCell descendHint = CreateHintCell(templateCells[0], DescendHintName, hintParent, 1);
        DiveHintCell ascendHint = CreateHintCell(templateCells[0], AscendHintName, hintParent, 2);
        return new DiveHintSet(root, runHint, descendHint, ascendHint);
    }

    private static Transform GetHintParent(GameObject root, InputHintMode mode)
    {
        string preferredParentName = mode == InputHintMode.Gamepad ? "Gamepad" : "Keyboard";
        return FindHintParentWithTemplates(root, preferredParentName)
               ?? FindHintParentWithTemplates(root, "Keyboard")
               ?? FindHintParentWithTemplates(root, "Gamepad")
               ?? root.transform;
    }

    private static InputHintMode GetInputHintMode()
    {
        return ZInput.IsGamepadActive() ? InputHintMode.Gamepad : InputHintMode.Keyboard;
    }

    private static Transform? FindHintParentWithTemplates(GameObject root, string name)
    {
        Transform candidate = root.transform.Find(name);
        return candidate != null && GetTemplateHintCells(candidate).Length > 0 ? candidate : null;
    }

    private static GameObject[] GetTemplateHintCells(Transform hintParent)
    {
        return hintParent
            .Cast<Transform>()
            .Where(static child => !child.name.StartsWith("DiveIn_") && child.GetComponentsInChildren<TMP_Text>(true).Length >= 2)
            .Select(static child => child.gameObject)
            .ToArray();
    }

    private static TMP_Text? FindKeyText(TMP_Text[] texts)
    {
        if (texts.Length == 0)
        {
            return null;
        }

        return texts.FirstOrDefault(static text => HierarchyNameContainsToken(text, KeyTextNameTokens))
               ?? texts.FirstOrDefault(static text => LooksLikeKeyBindingText(text.text))
               ?? texts.OrderBy(static text => text.transform.position.x).LastOrDefault();
    }

    private static TMP_Text? FindLabelText(TMP_Text[] texts, TMP_Text? keyText)
    {
        TMP_Text[] candidates = texts
            .Where(text => text != keyText)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        return candidates.FirstOrDefault(static text => HierarchyNameContainsToken(text, LabelTextNameTokens))
               ?? candidates.FirstOrDefault(static text => !LooksLikeKeyBindingText(text.text))
               ?? candidates.OrderBy(static text => text.transform.position.x).FirstOrDefault();
    }

    private static bool HierarchyNameContainsToken(TMP_Text text, string[] tokens)
    {
        Transform? current = text.transform;
        for (int depth = 0; current != null && depth < 4; ++depth)
        {
            string normalizedName = NormalizeIdentifier(current.name);
            if (!IsIgnoredHintContainerName(normalizedName) &&
                tokens.Any(token => normalizedName.Contains(token)))
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private static bool IsIgnoredHintContainerName(string normalizedName)
    {
        return normalizedName == "keyboard"
               || normalizedName == "gamepad"
               || normalizedName == "joystick";
    }

    private static bool LooksLikeKeyBindingText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string normalizedText = NormalizeIdentifier(text);
        return normalizedText.Contains("mouse")
               || normalizedText.Contains("ctrl")
               || normalizedText.Contains("shift")
               || normalizedText.Contains("alt")
               || normalizedText.Contains("space")
               || normalizedText.Contains("button")
               || normalizedText.Contains("sprite")
               || normalizedText.Length <= 2;
    }

    private static string NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static DiveHintCell CreateHintCell(GameObject template, string name, Transform parent, int siblingIndex)
    {
        GameObject hint = UnityEngine.Object.Instantiate(template, parent, false);
        hint.name = name;
        hint.transform.SetSiblingIndex(siblingIndex);
        return new DiveHintCell(hint);
    }

    private static void SetAllHintsActive(bool active)
    {
        _swimmingHints?.SetActive(active);
        _combatHints?.SetActive(active);
    }

    private static void HideDiveHints()
    {
        if (!_hasLastHintSnapshot)
        {
            return;
        }

        SetAllHintsActive(false);
        _hasLastHintSnapshot = false;
    }

    private static void DestroyHints()
    {
        _swimmingHints?.Destroy();
        _combatHints?.Destroy();
        _swimmingHints = null;
        _combatHints = null;
        _hasLastHintSnapshot = false;
    }

    private static void RebuildLayout(GameObject? hint)
    {
        if (hint == null || hint.transform.parent is not RectTransform parent)
        {
            return;
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(parent);
    }
}
