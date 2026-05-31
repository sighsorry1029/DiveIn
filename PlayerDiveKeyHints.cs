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
    private const string DescendHintName = "DiveIn_DescendHint";
    private const string AscendHintName = "DiveIn_AscendHint";

    private static KeyHints? _owner;
    private static DiveHintSet? _swimmingHints;
    private static DiveHintSet? _combatHints;

    private sealed class DiveHintSet
    {
        public DiveHintSet(GameObject? root, GameObject descendHint, GameObject ascendHint)
        {
            Root = root;
            DescendHint = descendHint;
            AscendHint = ascendHint;
        }

        public GameObject? Root { get; }
        public GameObject DescendHint { get; }
        public GameObject AscendHint { get; }

        public bool IsValid => DescendHint && AscendHint && (Root == null || Root);

        public void Configure(string descendKey, string ascendKey)
        {
            ConfigureHint(DescendHint, "Descend", descendKey);
            ConfigureHint(AscendHint, "Ascend", ascendKey);
        }

        public void SetActive(bool active)
        {
            DescendHint.SetActive(active);
            AscendHint.SetActive(active);
            if (Root)
            {
                Root!.SetActive(active);
            }
        }

        public void RebuildLayout()
        {
            PlayerDiveKeyHints.RebuildLayout(Root != null ? Root : DescendHint);
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
            SetAllHintsActive(false);
            return;
        }

        if (!EnsureHints(keyHints))
        {
            SetAllHintsActive(false);
            return;
        }

        string descendKey = ServerSyncModTemplatePlugin.GetDiveDescendKeyHint();
        string ascendKey = ServerSyncModTemplatePlugin.GetDiveAscendKeyHint();
        _swimmingHints?.Configure(descendKey, ascendKey);
        _combatHints?.Configure(descendKey, ascendKey);

        bool showCombatHints = keyHints.m_combatHints != null && keyHints.m_combatHints.activeSelf;
        bool showSwimmingHints = !showCombatHints && HasNoVisibleHandItems(player);
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

        if (_owner == keyHints &&
            _swimmingHints?.IsValid == true &&
            _combatHints?.IsValid == true)
        {
            return true;
        }

        _owner = keyHints;
        _swimmingHints = CreateSwimmingHints(keyHints);
        _combatHints = CreateCombatHints(keyHints);
        return _swimmingHints?.IsValid == true && _combatHints?.IsValid == true;
    }

    private static DiveHintSet? CreateSwimmingHints(KeyHints keyHints)
    {
        Transform parent = keyHints.m_combatHints.transform.parent;
        GameObject root = Object.Instantiate(keyHints.m_combatHints, parent, false);
        root.name = SwimmingHintsRootName;
        root.transform.SetSiblingIndex(keyHints.m_combatHints.transform.GetSiblingIndex());

        Transform hintParent = GetKeyboardHintParent(root);
        for (int i = 0; i < root.transform.childCount; ++i)
        {
            Transform child = root.transform.GetChild(i);
            child.gameObject.SetActive(child == hintParent);
        }

        GameObject[] hintCells = GetTemplateHintCells(hintParent);
        if (hintCells.Length == 0)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning("Failed to create DiveIn swimming key hints: no combat hint template cells found.");
            root.SetActive(false);
            return null;
        }

        GameObject descendHint = hintCells[0];
        GameObject ascendHint = hintCells.Length > 1
            ? hintCells[1]
            : Object.Instantiate(descendHint, descendHint.transform.parent, false);
        descendHint.name = DescendHintName;
        ascendHint.name = AscendHintName;

        foreach (GameObject hintCell in hintCells)
        {
            hintCell.SetActive(false);
        }

        descendHint.transform.SetSiblingIndex(0);
        ascendHint.transform.SetSiblingIndex(1);

        DiveHintSet hintSet = new(root, descendHint, ascendHint);
        hintSet.SetActive(false);
        return hintSet;
    }

    private static DiveHintSet? CreateCombatHints(KeyHints keyHints)
    {
        Transform hintParent = GetKeyboardHintParent(keyHints.m_combatHints);
        GameObject[] templates = GetTemplateHintCells(hintParent);
        if (templates.Length == 0)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning("Failed to create DiveIn combat key hints: no combat hint template cells found.");
            return null;
        }

        GameObject descendHint = Object.Instantiate(templates[0], hintParent, false);
        GameObject ascendHint = Object.Instantiate(templates.Length > 1 ? templates[1] : templates[0], hintParent, false);
        descendHint.name = DescendHintName;
        ascendHint.name = AscendHintName;
        descendHint.transform.SetSiblingIndex(0);
        ascendHint.transform.SetSiblingIndex(1);

        DiveHintSet hintSet = new(null, descendHint, ascendHint);
        hintSet.SetActive(false);
        return hintSet;
    }

    private static Transform GetKeyboardHintParent(GameObject root)
    {
        return root.transform.Find("Keyboard") ?? root.transform;
    }

    private static GameObject[] GetTemplateHintCells(Transform hintParent)
    {
        return hintParent
            .Cast<Transform>()
            .Where(static child => !child.name.StartsWith("DiveIn_") && child.GetComponentsInChildren<TMP_Text>(true).Length > 0)
            .Select(static child => child.gameObject)
            .ToArray();
    }

    private static void ConfigureHint(GameObject? hint, string label, string keyText)
    {
        if (!hint)
        {
            return;
        }

        TMP_Text[] texts = hint!
            .GetComponentsInChildren<TMP_Text>(true)
            .OrderBy(static text => text.transform.position.x)
            .ToArray();
        if (texts.Length == 0)
        {
            return;
        }

        texts[0].gameObject.SetActive(true);
        texts[0].text = label;

        if (texts.Length < 2)
        {
            return;
        }

        for (int i = 1; i < texts.Length - 1; ++i)
        {
            texts[i].gameObject.SetActive(false);
        }

        TMP_Text key = texts[texts.Length - 1];
        key.gameObject.SetActive(true);
        key.text = keyText;
    }

    private static void SetAllHintsActive(bool active)
    {
        _swimmingHints?.SetActive(active);
        _combatHints?.SetActive(active);
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
