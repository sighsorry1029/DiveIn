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
        Func<Character, bool> hide = BindPredicate<Func<Character, bool>>(waterCombat, "ShouldHideForSwimming", typeof(Character));
        Func<Character, ItemDrop.ItemData, bool> equip = BindPredicate<Func<Character, ItemDrop.ItemData, bool>>(waterCombat, "ShouldBlockWaterEquip", typeof(Character), typeof(ItemDrop.ItemData));
        _isMeleeWeapon = melee;
        _allowsAttack = attack;
        _shouldHide = hide;
        _shouldBlockEquip = equip;
        _enabled = enabled;
    }

    private static T BindPredicate<T>(Type type, string name, params Type[] parameters) where T : Delegate
    {
        MethodInfo method = AccessTools.DeclaredMethod(type, name, parameters);
        if (method == null || !method.IsStatic || method.ReturnType != typeof(bool) || method.ContainsGenericParameters)
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
        _failed = false;
    }

    private static void Fail(string reason)
    {
        if (!_failed)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning(
                $"Path of Valheiman water combat integration unavailable: {reason}. DiveIn water-combat assists are disabled; already integrated gates fall back to swimming restrictions. Other mod patches are left untouched.");
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

        MethodInfo original = equip ? _shouldBlockEquip!.Method : _shouldHide!.Method;
        List<CodeInstruction> calls = code.Where(i => i.Calls(original)).ToList();
        if (calls.Count != 1)
        {
            Fail($"expected one PoV swimming gate in Humanoid.{(equip ? "EquipItem" : "UpdateEquipment")}, found {calls.Count}");
            return code;
        }

        // Same stack signature, no inserted branches or removed instructions. Keep labels and exception blocks.
        CodeInstruction replacement = new(calls[0]);
        replacement.operand = AccessTools.DeclaredMethod(typeof(PathOfValheimanCompat),
            equip ? nameof(ShouldBlockWaterEquip) : nameof(ShouldHideForSwimming));
        code[code.IndexOf(calls[0])] = replacement;
        return code;
    }

    private static bool ShouldBlockWaterEquip(Character actor, ItemDrop.ItemData item)
    {
        if (_failed || _shouldBlockEquip == null) return actor.IsSwimming();
        bool blocked;
        try { blocked = _shouldBlockEquip(actor, item); }
        catch (Exception ex) { Fail(ex.Message); return actor.IsSwimming(); }
        return blocked || actor is Player player && PlayerDiveUtils.IsValidLocalPlayer(player)
            && actor.IsSwimming() && WaterEquipmentPatches.IsWaterRestrictedItem(item);
    }

    private static bool ShouldHideForSwimming(Character actor)
    {
        if (_failed || _shouldHide == null) return actor.IsSwimming();
        bool hidden;
        try { hidden = _shouldHide(actor); }
        catch (Exception ex) { Fail(ex.Message); return actor.IsSwimming(); }
        return hidden || actor is Player player && PlayerDiveUtils.IsValidLocalPlayer(player)
            && actor.IsSwimming() && WaterEquipmentPatches.HasWaterRestrictedHandItem(player);
    }

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
