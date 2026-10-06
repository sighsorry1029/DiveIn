using HarmonyLib;
using TMPro;
using UnityEngine;

namespace ServerSyncModTemplate;

[HarmonyPatch]
internal static class FastSwimHud
{
    private static Hud? _owner;
    private static TMP_Text? _controls;
    private static TMP_Text? _status;
    private static string? _language;
    private static string? _ascendKey;
    private static string? _descendKey;
    private static float _nextPresentationRefresh;
    private static bool _lastEnabled;
    private static bool _lastLowStamina;

    internal static bool ControlsVisible => IsVisible(_controls);
    internal static bool StatusVisible => IsVisible(_status);

    private static bool IsVisible(TMP_Text? row) => _owner != null && _owner == Hud.instance
                                                 && row != null && row.gameObject.activeInHierarchy;

    internal static (bool Controls, bool Status) GetRows(ServerSyncModTemplatePlugin.SwimHudMode mode, bool canFastSwim)
    {
        return (mode == ServerSyncModTemplatePlugin.SwimHudMode.Full,
            canFastSwim && (mode == ServerSyncModTemplatePlugin.SwimHudMode.Full
                           || mode == ServerSyncModTemplatePlugin.SwimHudMode.FastSwimOnly));
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Hud), "LateUpdate")]
    private static void HudLateUpdatePostfix(Hud __instance)
    {
        if (!ReferenceEquals(_owner, __instance))
        {
            Clear();
            _owner = __instance;
        }

        Player player = Player.m_localPlayer;
        PlayerDiveController? diver = PlayerDiveController.LocalInstance;
        ServerSyncModTemplatePlugin.SwimHudMode mode = ServerSyncModTemplatePlugin._swimHudMode?.Value
                                                     ?? ServerSyncModTemplatePlugin.SwimHudMode.Full;
        if (mode == ServerSyncModTemplatePlugin.SwimHudMode.Off
            || player == null || diver == null || diver.Player != player
            || !diver.ShouldTreatAsSwimming() || !CanShowHud(__instance, player))
        {
            SetVisible(false, false);
            return;
        }

        (bool showControls, bool showStatus) = GetRows(mode, diver.CanUseFastSwim());
        if ((!showControls && !showStatus) || !EnsureWidgets(__instance))
        {
            SetVisible(false, false);
            return;
        }

        // Report the selected mode, not instantaneous movement: standing still does not turn Toggle off.
        bool enabled = diver.IsFastSwimEnabled();
        bool lowStamina = enabled && !player.HaveStamina();
        bool controlsWereHidden = !_controls!.gameObject.activeSelf;
        bool statusWasHidden = !_status!.gameObject.activeSelf;
        bool rowsChanged = showControls != !controlsWereHidden || showStatus != !statusWasHidden;
        bool bindingsChanged = false;
        if (rowsChanged || Time.unscaledTime >= _nextPresentationRefresh)
        {
            _nextPresentationRefresh = Time.unscaledTime + 0.25f;
            string language = Localization.instance?.GetSelectedLanguage() ?? string.Empty;
            string ascendKey = showControls ? ServerSyncModTemplatePlugin.GetDiveAscendKeyHint() : string.Empty;
            string descendKey = showControls ? ServerSyncModTemplatePlugin.GetDiveDescendKeyHint() : string.Empty;
            bindingsChanged = language != _language || ascendKey != _ascendKey || descendKey != _descendKey;
            _language = language;
            _ascendKey = ascendKey;
            _descendKey = descendKey;
            UpdateStyleAndLayout(__instance, showControls);
        }

        if (showControls && (controlsWereHidden || bindingsChanged))
        {
            Localization.instance?.RemoveTextFromCache(_controls);
            _controls.text = $"{_ascendKey}: {DiveLocalization.Localize(DiveLocalization.AscendKey)}"
                             + $" · {_descendKey}: {DiveLocalization.Localize(DiveLocalization.DescendKey)}";
        }

        if (showStatus && (statusWasHidden || bindingsChanged || enabled != _lastEnabled))
        {
            Localization.instance?.RemoveTextFromCache(_status);
            _status.text = DiveLocalization.Localize(enabled
                ? DiveLocalization.FastSwimOnKey
                : DiveLocalization.FastSwimOffKey);
        }

        if (showStatus && (statusWasHidden || enabled != _lastEnabled || lowStamina != _lastLowStamina))
        {
            _status.color = lowStamina ? new Color(1f, 0.6f, 0.4f)
                : enabled ? new Color(1f, 0.85f, 0.45f) : Color.white;
        }

        _lastEnabled = enabled;
        _lastLowStamina = lowStamina;
        SetVisible(showControls, showStatus);
    }

    private static bool CanShowHud(Hud hud, Player player)
    {
        return hud.m_rootObject != null && hud.m_rootObject.activeInHierarchy && hud.IsVisible()
               && !Hud.IsUserHidden() && !player.IsDead() && !player.IsTeleporting()
               && !player.InCutscene() && !player.IsSleeping()
               && !(hud.m_loadingScreen != null && hud.m_loadingScreen.gameObject.activeSelf)
               && !Game.IsPaused() && !Menu.IsVisible() && !Console.IsVisible() && !TextInput.IsVisible()
               && !(TextViewer.instance != null && TextViewer.instance.IsVisible())
               && !InventoryGui.IsVisible() && !Minimap.IsOpen() && !StoreGui.IsVisible()
               && !(InventoryGui.instance != null
                    && (InventoryGui.instance.IsSkillsPanelOpen || InventoryGui.instance.IsTrophisPanelOpen
                        || InventoryGui.instance.IsAchievementsPanelOpen || InventoryGui.instance.IsTextPanelOpen))
               && !Hud.InRadial() && !(Chat.instance != null && Chat.instance.HasFocus())
               && !player.InPlaceMode() && !PlayerCustomizaton.IsBarberGuiVisible()
               && player.GetDoodadController() == null;
    }

    private static bool EnsureWidgets(Hud hud)
    {
        if ((_controls != null && _controls.transform.parent != hud.m_rootObject.transform)
            || (_status != null && _status.transform.parent != hud.m_rootObject.transform))
        {
            Clear();
            _owner = hud;
        }

        // Do not suppress vanilla hints unless both rows can use the actual stamina number style.
        if (hud.m_staminaText == null || hud.m_staminaText.font == null)
        {
            return false;
        }

        if (_controls == null)
        {
            _controls = CreateRow(hud, "DiveIn_SwimControls");
        }

        if (_status == null)
        {
            _status = CreateRow(hud, "DiveIn_FastSwimStatus");
        }

        return true;
    }

    private static TMP_Text CreateRow(Hud hud, string name)
    {
        GameObject widget = new(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        widget.SetActive(false);
        widget.transform.SetParent(hud.m_rootObject.transform, false);
        TMP_Text row = widget.GetComponent<TextMeshProUGUI>();
        row.alignment = TextAlignmentOptions.Center;
        row.color = Color.white;
        row.richText = true;
        row.raycastTarget = false;
        row.enableAutoSizing = false;
        row.textWrappingMode = TextWrappingModes.NoWrap;

        RectTransform rect = row.rectTransform;
        Vector3 initialScale = hud.m_staminaText.rectTransform.localScale;
        if (initialScale.x > 0.001f && initialScale.y > 0.001f) rect.localScale = initialScale;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, 90f);
        rect.sizeDelta = new Vector2(700f, 36f);
        return row;
    }

    private static void UpdateStyleAndLayout(Hud hud, bool showControls)
    {
        TMP_Text template = hud.m_staminaText;
        Vector3 sourceScale = template.transform.lossyScale;
        Vector3 rootScale = hud.m_rootObject.transform.lossyScale;
        Vector3 scale = _status!.rectTransform.localScale;
        Animator animator = hud.m_staminaAnimator;
        bool stableStaminaBar = animator == null || (animator.GetBool("Visible")
            && !animator.IsInTransition(0) && animator.GetCurrentAnimatorStateInfo(0).normalizedTime >= 1f);
        if (stableStaminaBar && sourceScale.x > 0.001f && sourceScale.y > 0.001f
            && rootScale.x > 0.001f && rootScale.y > 0.001f)
        {
            // Match rendered number size even if an intermediate stamina parent has a different scale.
            // Keep the last sample during hiding/transitions instead of shrinking with the bar.
            scale = new Vector3(sourceScale.x / rootScale.x, sourceScale.y / rootScale.y, 1f);
        }

        TMP_SpriteAsset sprites = hud.m_hoverName != null ? hud.m_hoverName.spriteAsset : template.spriteAsset;
        ApplyStyle(_controls!, template, sprites, scale);
        ApplyStyle(_status, template, sprites, scale);
        // Root siblings stay visible when the stamina bar fades. The first row retains its old center.
        float rowSpacing = (template.fontSize + 6f) * scale.y;
        Vector2 statusPosition = new(0f, showControls ? 90f - rowSpacing : 90f);
        if (_status.rectTransform.anchoredPosition != statusPosition)
        {
            _status.rectTransform.anchoredPosition = statusPosition;
        }
    }

    private static void ApplyStyle(TMP_Text row, TMP_Text template, TMP_SpriteAsset sprites, Vector3 scale)
    {
        if (row.font != template.font) row.font = template.font;
        if (row.fontSharedMaterial != template.fontSharedMaterial) row.fontSharedMaterial = template.fontSharedMaterial;
        if (row.fontSize != template.fontSize) row.fontSize = template.fontSize;
        if (row.fontStyle != template.fontStyle) row.fontStyle = template.fontStyle;
        if (row.spriteAsset != sprites) row.spriteAsset = sprites;
        if (row.rectTransform.localScale != scale) row.rectTransform.localScale = scale;
    }

    private static bool SetRowVisible(TMP_Text? row, bool visible)
    {
        if (row == null || row.gameObject.activeSelf == visible) return false;
        row.gameObject.SetActive(visible);
        return true;
    }

    private static void SetVisible(bool showControls, bool showStatus)
    {
        bool changed = SetRowVisible(_controls, showControls);
        changed |= SetRowVisible(_status, showStatus);
        if (changed)
        {
            // Also refresh Full <-> FastSwimOnly: status can stay active while controls change hands.
            PlayerDiveKeyHints.RefreshForFastSwimHud();
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Hud), "OnDestroy")]
    private static void HudOnDestroyPrefix(Hud __instance)
    {
        if (ReferenceEquals(_owner, __instance))
        {
            Clear();
        }
    }

    internal static void Clear()
    {
        DestroyRow(_controls);
        DestroyRow(_status);
        _controls = null;
        _status = null;
        _owner = null;
        _language = null;
        _ascendKey = null;
        _descendKey = null;
        _nextPresentationRefresh = 0f;
        // No hint refresh during teardown: it can lazily create a player controller.
    }

    private static void DestroyRow(TMP_Text? row)
    {
        if (row == null) return;
        row.gameObject.SetActive(false);
        Localization.instance?.RemoveTextFromCache(row);
        Object.Destroy(row.gameObject);
    }
}
