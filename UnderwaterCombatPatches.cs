// Contains player diving code derived from UnderTheSea (GPL-3.0) and modified for DiveIn on 2026-04-04.
using HarmonyLib;
using UnityEngine;

namespace ServerSyncModTemplate;

[HarmonyPatch]
internal static class UnderwaterCombatPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
    private static void PlayerSetControlsPrefix(
        Player __instance,
        ref Vector3 movedir,
        ref bool attack,
        ref bool attackHold,
        ref bool secondaryAttack,
        ref bool secondaryAttackHold,
        ref bool crouch,
        ref bool block,
        ref bool blockHold)
    {
        if (!PlayerDiveUtils.TryGetUnderwaterLocalDiver(__instance, out PlayerDiveController diver))
        {
            return;
        }

        crouch = false;

        bool hadCombatInput = attack || attackHold || secondaryAttack || secondaryAttackHold || block || blockHold;
        if (hadCombatInput)
        {
            movedir = Vector3.zero;
            diver.SuppressMovementForCombat();
        }

        if ((block || blockHold) && CanForceShowHiddenBlocker(__instance))
        {
            __instance.ShowHandItems();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Character), nameof(Character.UpdateSwimming))]
    private static void CharacterUpdateSwimmingPostfix(Character __instance)
    {
        if (__instance is not Player player || !ShouldShowUnderwaterGuardAnimation(player))
        {
            return;
        }

        player.m_zanim.SetBool(Humanoid.s_blocking, true);
        player.m_zanim.SetBool(Character.s_inWater, false);
    }

    private static bool CanForceShowHiddenBlocker(Player player)
    {
        return PlayerDiveUtils.TryGetUnderwaterLocalDiver(player, out _)
               && !player.IsOnGround()
               && !player.InDodge()
               && HasHiddenBlocker(player)
               && !HasWaterRestrictedHiddenBlocker(player);
    }

    private static bool ShouldShowUnderwaterGuardAnimation(Player player)
    {
        return PlayerDiveUtils.TryGetUnderwaterLocalDiver(player, out _)
               && player.IsBlocking();
    }

    private static bool HasWaterRestrictedHiddenBlocker(Player player)
    {
        return IsWaterRestrictedHiddenBlocker(player.m_hiddenRightItem) ||
               IsWaterRestrictedHiddenBlocker(player.m_hiddenLeftItem);
    }

    private static bool HasHiddenBlocker(Player player)
    {
        return IsBlockableHiddenItem(player.m_hiddenLeftItem) ||
               IsBlockableHiddenItem(player.m_hiddenRightItem);
    }

    private static bool IsWaterRestrictedHiddenBlocker(ItemDrop.ItemData? item)
    {
        return IsBlockableHiddenItem(item) && ServerSyncModTemplatePlugin.IsWaterRestrictedItem(item);
    }

    private static bool IsBlockableHiddenItem(ItemDrop.ItemData? item)
    {
        return item?.m_shared != null && item.m_shared.m_blockable;
    }
}
