// Contains player diving code derived from UnderTheSea (GPL-3.0) and modified for DiveIn on 2026-04-04.
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ServerSyncModTemplate;

[HarmonyPatch]
internal static class WaterEquipmentPatches
{
    private static readonly CodeMatch[] SwimmingRestrictionPattern =
    {
        new(OpCodes.Ldarg_0),
        new(OpCodes.Call, AccessTools.Method(typeof(Character), nameof(Character.IsSwimming))),
        new(OpCodes.Brfalse),
        new(OpCodes.Ldarg_0),
        new(OpCodes.Call, AccessTools.Method(typeof(Character), nameof(Character.IsOnGround)))
    };

    private static readonly int SwimmingRestrictionInstructionCount = SwimmingRestrictionPattern.Length + 1;

    private static bool IsWaterEquipmentBypassTarget(Humanoid? humanoid)
    {
        return humanoid is Player player && PlayerDiveUtils.IsValidLocalPlayer(player);
    }

    private static bool ShouldKeepWaterRestrictionForHumanoid(Humanoid humanoid)
    {
        return !IsWaterEquipmentBypassTarget(humanoid)
               || ServerSyncModTemplatePlugin.HumanoidHasWaterRestrictedEquipment(humanoid);
    }

    private static bool ShouldKeepWaterRestrictionForEquipItem(Humanoid humanoid, ItemDrop.ItemData item)
    {
        return !IsWaterEquipmentBypassTarget(humanoid)
               || ServerSyncModTemplatePlugin.IsWaterRestrictedItem(item);
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UpdateEquipment))]
    private static IEnumerable<CodeInstruction> HumanoidUpdateEquipmentTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        return InsertWaterEquipmentBypass<Humanoid>(instructions, "Humanoid.UpdateEquipment", OpCodes.Ldarg_0, ShouldKeepWaterRestrictionForHumanoid);
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
    private static IEnumerable<CodeInstruction> HumanoidEquipItemTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        return InsertWaterEquipmentBypass<Humanoid, ItemDrop.ItemData>(
            instructions,
            "Humanoid.EquipItem",
            OpCodes.Ldarg_0,
            OpCodes.Ldarg_1,
            ShouldKeepWaterRestrictionForEquipItem);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.Update))]
    private static void PlayerUpdatePrefix(Player __instance, out bool __state)
    {
        __state = ShouldForceShowHiddenHandItems(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.Update))]
    private static void PlayerUpdatePostfix(Player __instance, bool __state)
    {
        if (__state && CanForceShowHiddenHandItems(__instance))
        {
            __instance.ShowHandItems();
        }
    }

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
        if (!TryGetUnderwaterLocalDiver(__instance, out PlayerDiveController diver))
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

        bool forceShowHiddenBlocker = (block || blockHold) && CanForceShowHiddenBlocker(__instance);
        if (forceShowHiddenBlocker)
        {
            __instance.ShowHandItems();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Character), nameof(Character.UpdateSwimming))]
    private static void CharacterUpdateSwimmingPostfix(Character __instance)
    {
        ForceUnderwaterGuardAnimation(__instance);
    }

    private static bool ShouldForceShowHiddenHandItems(Player player)
    {
        return WasHideInputPressed(player) && CanForceShowHiddenHandItems(player);
    }

    private static bool CanForceShowHiddenBlocker(Player player)
    {
        return TryGetUnderwaterLocalDiver(player, out _)
               && !player.IsOnGround()
               && !player.InDodge()
               && HasHiddenBlocker(player)
               && !HasWaterRestrictedHiddenBlocker(player);
    }

    private static void ForceUnderwaterGuardAnimation(Character character)
    {
        if (character is not Player player || !ShouldShowUnderwaterGuardAnimation(player))
        {
            return;
        }

        player.m_zanim.SetBool(Humanoid.s_blocking, true);
        player.m_zanim.SetBool(Character.s_inWater, false);
    }

    private static bool ShouldShowUnderwaterGuardAnimation(Player player)
    {
        return TryGetUnderwaterLocalDiver(player, out _)
               && player.IsBlocking();
    }

    private static bool TryGetUnderwaterLocalDiver(Player player, out PlayerDiveController diver)
    {
        diver = null!;
        return PlayerDiveUtils.IsValidLocalPlayer(player)
               && PlayerDiveUtils.TryGetLocalDiver(player, out diver)
               && diver.ShouldTreatAsSwimming();
    }

    private static bool CanForceShowHiddenHandItems(Player player)
    {
        return TryGetUnderwaterLocalDiver(player, out _)
               && !player.IsOnGround()
               && !player.InDodge()
               && player.GetRightItem() == null
               && player.GetLeftItem() == null
               && HasHiddenHandItems(player)
               && !HasWaterRestrictedHiddenHandItems(player);
    }

    private static bool WasHideInputPressed(Player player)
    {
        bool joyHide = !Hud.InRadial() &&
                       ZInput.GetButtonUp("JoyHide") &&
                       ZInput.GetButtonLastPressedTimer("JoyHide") < 0.33f;

        if ((int)ZInput.InputLayout == 0 || !ZInput.IsGamepadActive())
        {
            return ZInput.GetButtonDown("Hide") ||
                   joyHide && !ZInput.GetButton("JoyAltKeys") && !player.InPlaceMode();
        }

        return joyHide && !ZInput.GetButton("JoyAltKeys") && !player.InPlaceMode();
    }

    private static bool HasHiddenHandItems(Player player)
    {
        return player.m_hiddenRightItem != null || player.m_hiddenLeftItem != null;
    }

    private static bool HasWaterRestrictedHiddenHandItems(Player player)
    {
        return IsWaterRestrictedHiddenItem(player.m_hiddenRightItem) ||
               IsWaterRestrictedHiddenItem(player.m_hiddenLeftItem);
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

    private static bool IsWaterRestrictedHiddenItem(ItemDrop.ItemData? item)
    {
        return item != null && ServerSyncModTemplatePlugin.IsWaterRestrictedItem(item);
    }

    private static bool IsWaterRestrictedHiddenBlocker(ItemDrop.ItemData? item)
    {
        return IsBlockableHiddenItem(item) && ServerSyncModTemplatePlugin.IsWaterRestrictedItem(item);
    }

    private static bool IsBlockableHiddenItem(ItemDrop.ItemData? item)
    {
        return item?.m_shared != null && item.m_shared.m_blockable;
    }

    private static IEnumerable<CodeInstruction> InsertWaterEquipmentBypass<T>(
        IEnumerable<CodeInstruction> instructions,
        string methodName,
        OpCode argumentLoadOpCode,
        Func<T, bool> shouldKeepWaterRestriction)
    {
        return InsertWaterEquipmentBypass(
            instructions,
            methodName,
            new[]
            {
                new CodeInstruction(argumentLoadOpCode),
                Transpilers.EmitDelegate(shouldKeepWaterRestriction)
            });
    }

    private static IEnumerable<CodeInstruction> InsertWaterEquipmentBypass<T1, T2>(
        IEnumerable<CodeInstruction> instructions,
        string methodName,
        OpCode firstArgumentLoadOpCode,
        OpCode secondArgumentLoadOpCode,
        Func<T1, T2, bool> shouldKeepWaterRestriction)
    {
        return InsertWaterEquipmentBypass(
            instructions,
            methodName,
            new[]
            {
                new CodeInstruction(firstArgumentLoadOpCode),
                new CodeInstruction(secondArgumentLoadOpCode),
                Transpilers.EmitDelegate(shouldKeepWaterRestriction)
            });
    }

    private static IEnumerable<CodeInstruction> InsertWaterEquipmentBypass(
        IEnumerable<CodeInstruction> instructions,
        string methodName,
        IReadOnlyList<CodeInstruction> guardInstructions)
    {
        List<CodeInstruction> code = new(instructions);
        if (!TryFindSwimmingRestrictionInsertionPoint(code, methodName, out CodeMatcher codeMatcher))
        {
            return code;
        }

        object branchTarget = codeMatcher.InstructionAt(-1).operand;
        List<CodeInstruction> insertedInstructions = new(guardInstructions)
        {
            new(OpCodes.Brfalse, branchTarget)
        };

        ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogDebug($"Applied water equipment bypass transpiler to {methodName}.");
        return codeMatcher
            .InsertAndAdvance(insertedInstructions)
            .InstructionEnumeration();
    }

    private static bool TryFindSwimmingRestrictionInsertionPoint(List<CodeInstruction> code, string methodName, out CodeMatcher codeMatcher)
    {
        codeMatcher = new CodeMatcher(code);
        codeMatcher.MatchStartForward(SwimmingRestrictionPattern);
        if (!codeMatcher.IsValid)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning(
                $"Failed to locate swimming item restriction in {methodName}. Water equipment bypass for this method is disabled; vanilla swimming restrictions remain.");
            return false;
        }

        codeMatcher.Advance(SwimmingRestrictionInstructionCount);
        if (!codeMatcher.IsValid)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning(
                $"Failed to advance water equipment transpiler cursor in {methodName}. Water equipment bypass for this method is disabled; vanilla swimming restrictions remain.");
            return false;
        }

        return true;
    }
}
