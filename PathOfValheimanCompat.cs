using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace ServerSyncModTemplate;

// Optional-mod boundary only: PoV owns progression and weapon classification. Never cache an unlock result.
internal static class PathOfValheimanCompat
{
    internal const string PluginGuid = "treextr.pathofvalheiman";
    internal static bool Installed { get; private set; }
    private static Func<Character, bool>? _enabled;
    private static Func<ItemDrop.ItemData, bool>? _isMeleeWeapon;
    private static Func<Attack, ItemDrop.ItemData, bool>? _allowsAttack;
    private static Func<Character, bool>? _shouldHide;
    private static Func<Character, ItemDrop.ItemData, bool>? _shouldBlockEquip;
    private static Func<bool, Character, bool>? _shouldHideWithSwimming;
    private static Func<bool, Character, ItemDrop.ItemData, bool>? _shouldBlockEquipWithSwimming;
    private static bool _failed;

    internal static void Initialize()
    {
        Reset();
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin))
        {
            return;
        }

        Installed = true;
        try
        {
            Bind(plugin.Instance.GetType().Assembly.GetType("PassiveTree.WaterCombat", throwOnError: true)!);
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogInfo(
                "Path of Valheiman water combat compatibility enabled: its live node and melee policy control water equipment; DiveIn blacklist remains active.");
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private static void Bind(Type waterCombat)
    {
        // Validate all signatures before publishing any delegates, including the original private node predicate.
        Func<Character, bool> enabled = BindPredicate<Func<Character, bool>>(waterCombat, "Enabled", typeof(Character));
        Func<ItemDrop.ItemData, bool> melee = BindPredicate<Func<ItemDrop.ItemData, bool>>(waterCombat, "IsMeleeWeapon", typeof(ItemDrop.ItemData));
        Func<Attack, ItemDrop.ItemData, bool> attack = BindPredicate<Func<Attack, ItemDrop.ItemData, bool>>(waterCombat, "AllowsAttack", typeof(Attack), typeof(ItemDrop.ItemData));
        // Accept the two known gate contracts, not a list of PoV release versions. Probe silently:
        // absence of one supported shape is normal, and both may coexist during a transition.
        Func<Character, bool>? hide = TryBindPredicate<Func<Character, bool>>(waterCombat, "ShouldHideForSwimming", typeof(Character));
        Func<Character, ItemDrop.ItemData, bool>? equip = TryBindPredicate<Func<Character, ItemDrop.ItemData, bool>>(waterCombat, "ShouldBlockWaterEquip", typeof(Character), typeof(ItemDrop.ItemData));
        Func<bool, Character, bool>? hideWithSwimming = TryBindPredicate<Func<bool, Character, bool>>(waterCombat, "ShouldHideForSwimming", typeof(bool), typeof(Character));
        Func<bool, Character, ItemDrop.ItemData, bool>? equipWithSwimming = TryBindPredicate<Func<bool, Character, ItemDrop.ItemData, bool>>(waterCombat, "ShouldBlockWaterEquip", typeof(bool), typeof(Character), typeof(ItemDrop.ItemData));
        if (hide == null && hideWithSwimming == null)
            throw new MissingMethodException(waterCombat.FullName, "ShouldHideForSwimming: expected (Character) or (bool, Character)");
        if (equip == null && equipWithSwimming == null)
            throw new MissingMethodException(waterCombat.FullName, "ShouldBlockWaterEquip: expected (Character, ItemData) or (bool, Character, ItemData)");
        _isMeleeWeapon = melee;
        _allowsAttack = attack;
        _shouldHide = hide;
        _shouldBlockEquip = equip;
        _shouldHideWithSwimming = hideWithSwimming;
        _shouldBlockEquipWithSwimming = equipWithSwimming;
        _enabled = enabled;
    }

    private static T BindPredicate<T>(Type type, string name, params Type[] parameters) where T : Delegate
    {
        return TryBindPredicate<T>(type, name, parameters) ?? throw new MissingMethodException(type.FullName, name);
    }

    private static T? TryBindPredicate<T>(Type type, string name, params Type[] parameters) where T : Delegate
    {
        MethodInfo? method = type.GetMethod(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            null, parameters, null);
        if (method == null) return null;
        if (!method.IsStatic || method.ReturnType != typeof(bool) || method.ContainsGenericParameters
            || !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters))
        {
            throw new MissingMethodException(type.FullName, name);
        }

        return AccessTools.MethodDelegate<T>(method);
    }

    internal static void Reset()
    {
        Installed = false;
        _enabled = null;
        _isMeleeWeapon = null;
        _allowsAttack = null;
        _shouldHide = null;
        _shouldBlockEquip = null;
        _shouldHideWithSwimming = null;
        _shouldBlockEquipWithSwimming = null;
        _failed = false;
    }

    private static void Fail(string reason)
    {
        if (!_failed)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning(
                $"Path of Valheiman water combat integration unavailable: {reason}. DiveIn water-combat assists are disabled; already integrated gates retain their incoming swimming restriction and the DiveIn blacklist. Other mod patches are left untouched.");
        }
        _failed = true;
    }

    internal static IEnumerable<CodeInstruction> RewriteEquipmentGate(IEnumerable<CodeInstruction> instructions, bool equip)
    {
        List<CodeInstruction> code = instructions.ToList();
        if (_failed || _enabled == null)
        {
            return code;
        }

        MethodInfo? legacy = equip ? _shouldBlockEquip?.Method : _shouldHide?.Method;
        MethodInfo? withSwimming = equip ? _shouldBlockEquipWithSwimming?.Method : _shouldHideWithSwimming?.Method;
        List<CodeInstruction> calls = code.Where(i => legacy != null && i.Calls(legacy)
            || withSwimming != null && i.Calls(withSwimming)).ToList();
        if (calls.Count != 1)
        {
            Fail($"expected one PoV swimming gate in Humanoid.{(equip ? "EquipItem" : "UpdateEquipment")}, found {calls.Count}");
            return code;
        }

        // Same stack signature, no inserted branches or removed instructions. Keep labels and exception blocks.
        CodeInstruction replacement = new(calls[0]);
        bool takesSwimming = withSwimming != null && calls[0].Calls(withSwimming);
        replacement.operand = AccessTools.DeclaredMethod(typeof(PathOfValheimanCompat),
            takesSwimming
                ? (equip ? nameof(ShouldBlockWaterEquipWithSwimming) : nameof(ShouldHideForSwimmingWithSwimming))
                : (equip ? nameof(ShouldBlockWaterEquip) : nameof(ShouldHideForSwimming)));
        code[code.IndexOf(calls[0])] = replacement;
        return code;
    }

    private static bool ShouldBlockWaterEquip(Character actor, ItemDrop.ItemData item)
    {
        // Direct gameplay queries have no upstream gate result; only these sample IsSwimming themselves.
        if (_shouldBlockEquip == null && _shouldBlockEquipWithSwimming != null)
            return ShouldBlockWaterEquipWithSwimming(actor.IsSwimming(), actor, item);
        if (_failed || _shouldBlockEquip == null) return actor.IsSwimming();
        bool blocked;
        try { blocked = _shouldBlockEquip(actor, item); }
        catch (Exception ex) { Fail(ex.Message); return actor.IsSwimming(); }
        return blocked || IsRestrictedEquip(actor, item);
    }

    private static bool ShouldHideForSwimming(Character actor)
    {
        if (_shouldHide == null && _shouldHideWithSwimming != null)
            return ShouldHideForSwimmingWithSwimming(actor.IsSwimming(), actor);
        if (_failed || _shouldHide == null) return actor.IsSwimming();
        bool hidden;
        try { hidden = _shouldHide(actor); }
        catch (Exception ex) { Fail(ex.Message); return actor.IsSwimming(); }
        return hidden || HasRestrictedHands(actor);
    }

    private static bool ShouldBlockWaterEquipWithSwimming(bool swimming, Character actor, ItemDrop.ItemData item)
    {
        // PoV now composes after an existing vanilla/other-mod gate. Never replace that supplied result
        // with a fresh IsSwimming call, including on failure. Our own blacklist remains independent.
        bool blocked = swimming;
        if (!_failed && _shouldBlockEquipWithSwimming != null)
        {
            try { blocked = _shouldBlockEquipWithSwimming(swimming, actor, item); }
            catch (Exception ex) { Fail(ex.Message); }
        }
        return blocked || IsRestrictedEquip(actor, item);
    }

    private static bool ShouldHideForSwimmingWithSwimming(bool swimming, Character actor)
    {
        bool hidden = swimming;
        if (!_failed && _shouldHideWithSwimming != null)
        {
            try { hidden = _shouldHideWithSwimming(swimming, actor); }
            catch (Exception ex) { Fail(ex.Message); }
        }
        return hidden || HasRestrictedHands(actor);
    }

    private static bool IsRestrictedEquip(Character actor, ItemDrop.ItemData item) =>
        actor is Player player && PlayerDiveUtils.IsValidLocalPlayer(player)
        && actor.IsSwimming() && WaterEquipmentPatches.IsWaterRestrictedItem(item);

    private static bool HasRestrictedHands(Character actor) =>
        actor is Player player && PlayerDiveUtils.IsValidLocalPlayer(player)
        && actor.IsSwimming() && WaterEquipmentPatches.HasWaterRestrictedHandItem(player);

    private static bool HasNode(Player player)
    {
        if (_failed || _enabled == null) return false;
        try { return _enabled(player); }
        catch (Exception ex) { Fail(ex.Message); return false; }
    }

    private static bool CanUseItem(Player player, ItemDrop.ItemData? item)
    {
        return item != null && HasNode(player) && player.IsSwimming()
            && !WaterEquipmentPatches.IsWaterRestrictedItem(item) && !ShouldBlockWaterEquip(player, item);
    }

    internal static bool CanRestoreHiddenItems(Player player)
    {
        if (!Installed) return true;
        if (!HasNode(player)) return false;
        if (WaterEquipmentPatches.HasWaterRestrictedHandItem(player)) return false;
        ItemDrop.ItemData? right = GameAccess.HiddenRightItem(player);
        ItemDrop.ItemData? left = GameAccess.HiddenLeftItem(player);
        // ShowHandItems clears hidden references even when EquipItem fails: validate the entire pair first.
        if ((right != null && !CanUseItem(player, right)) || (left != null && !CanUseItem(player, left))) return false;
        try
        {
            // PoV permits equipping a shield but only keeps hands visible alongside a melee weapon.
            // Do not repeatedly restore a shield-only loadout that PoV immediately hides again.
            return IsMelee(player.RightItem) || IsMelee(player.LeftItem) || IsMelee(right) || IsMelee(left);
        }
        catch (Exception ex) { Fail(ex.Message); return false; }
    }

    private static bool IsMelee(ItemDrop.ItemData? item) => item != null && _isMeleeWeapon!(item);

    internal static bool CanUseAttackInput(Player player, bool secondary)
    {
        if (!Installed || player.IsOnGround()) return true;
        if (!HasNode(player)) return false;
        ItemDrop.ItemData? weapon = player.GetCurrentWeapon();
        try
        {
            if (player.RightItem == null && player.LeftItem == null)
            {
                // Match PoV's attack-triggered restoration candidate, without equipping or clearing anything.
                ItemDrop.ItemData? right = GameAccess.HiddenRightItem(player);
                ItemDrop.ItemData? left = GameAccess.HiddenLeftItem(player);
                if (IsMelee(right)) weapon = right;
                else if (IsMelee(left)) weapon = left;
            }

            Attack? attack = secondary ? weapon?.m_shared?.m_secondaryAttack : weapon?.m_shared?.m_attack;
            return CanUseItem(player, weapon) && attack != null && _allowsAttack!(attack, weapon!);
        }
        catch (Exception ex) { Fail(ex.Message); return false; }
    }

    internal static bool CanUseBlockInput(Player player)
    {
        if (!Installed || player.IsOnGround()) return true;
        if (!HasNode(player)) return false;
        ItemDrop.ItemData? blocker = player.LeftItem ?? player.GetCurrentWeapon();
        if (player.RightItem == null && player.LeftItem == null)
        {
            if (!CanRestoreHiddenItems(player)) return false;
            blocker = GameAccess.HiddenLeftItem(player) ?? GameAccess.HiddenRightItem(player) ?? blocker;
        }
        else if (ShouldHideForSwimming(player)) return false;
        return blocker?.m_shared?.m_blockable == true && CanUseItem(player, blocker);
    }
}
