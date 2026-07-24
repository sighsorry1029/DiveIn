// Contains player diving code derived from UnderTheSea (GPL-3.0) and modified for DiveIn on 2026-04-04.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ServerSyncModTemplate;

[HarmonyPatch]
internal static class WaterEquipmentPatches
{
    private static readonly object BlacklistLock = new();
    private static string _lastBlacklistRaw = string.Empty;
    private static HashSet<string> _blacklist = new(StringComparer.OrdinalIgnoreCase);

    private static readonly CodeMatch[] SwimmingRestrictionPattern =
    {
        new(OpCodes.Ldarg_0),
        new(OpCodes.Call, AccessTools.Method(typeof(Character), nameof(Character.IsSwimming))),
        new(instruction => instruction.opcode == OpCodes.Brfalse || instruction.opcode == OpCodes.Brfalse_S),
        new(OpCodes.Ldarg_0),
        new(OpCodes.Call, AccessTools.Method(typeof(Character), nameof(Character.IsOnGround))),
        new(instruction => instruction.opcode == OpCodes.Brtrue || instruction.opcode == OpCodes.Brtrue_S)
    };

    private static bool IsWaterEquipmentBypassTarget(Humanoid? humanoid)
    {
        return humanoid is Player player && PlayerDiveUtils.IsValidLocalPlayer(player);
    }

    private static bool ShouldKeepWaterRestrictionForHumanoid(Humanoid humanoid)
    {
        return !IsWaterEquipmentBypassTarget(humanoid)
               || HasWaterRestrictedHandItem(humanoid);
    }

    private static bool ShouldKeepWaterRestrictionForEquipItem(Humanoid humanoid, ItemDrop.ItemData item)
    {
        return !IsWaterEquipmentBypassTarget(humanoid)
               || IsWaterRestrictedItem(item);
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UpdateEquipment))]
    private static IEnumerable<CodeInstruction> HumanoidUpdateEquipmentTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        return InsertWaterEquipmentBypass(
            instructions,
            "Humanoid.UpdateEquipment",
            new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                Transpilers.EmitDelegate((Func<Humanoid, bool>)ShouldKeepWaterRestrictionForHumanoid)
            });
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
    private static IEnumerable<CodeInstruction> HumanoidEquipItemTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        return InsertWaterEquipmentBypass(
            instructions,
            "Humanoid.EquipItem",
            new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_1),
                Transpilers.EmitDelegate((Func<Humanoid, ItemDrop.ItemData, bool>)ShouldKeepWaterRestrictionForEquipItem)
            });
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

    private static bool ShouldForceShowHiddenHandItems(Player player)
    {
        return WasHideInputPressed(player) && CanForceShowHiddenHandItems(player);
    }

    private static bool CanForceShowHiddenHandItems(Player player)
    {
        return PlayerDiveUtils.TryGetUnderwaterLocalDiver(player, out _)
               && !player.IsOnGround()
               && !player.InDodge()
               && player.GetRightItem() == null
               && player.GetLeftItem() == null
               && (player.m_hiddenRightItem != null || player.m_hiddenLeftItem != null)
               && !IsWaterRestrictedItem(player.m_hiddenRightItem)
               && !IsWaterRestrictedItem(player.m_hiddenLeftItem);
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

    internal static bool IsWaterRestrictedItem(ItemDrop.ItemData? item)
    {
        if (item == null || item.m_dropPrefab == null)
        {
            return false;
        }

        RefreshBlacklistIfNeeded();
        string prefabName = Utils.GetPrefabName(item.m_dropPrefab);
        return !string.IsNullOrEmpty(prefabName) && _blacklist.Contains(prefabName);
    }

    private static bool HasWaterRestrictedHandItem(Humanoid humanoid)
    {
        return IsWaterRestrictedItem(humanoid.m_rightItem)
               || IsWaterRestrictedItem(humanoid.m_hiddenRightItem)
               || IsWaterRestrictedItem(humanoid.m_leftItem)
               || IsWaterRestrictedItem(humanoid.m_hiddenLeftItem);
    }

    private static void RefreshBlacklistIfNeeded()
    {
        string raw = ServerSyncModTemplatePlugin._waterEquipmentBlacklist?.Value ?? string.Empty;
        if (string.Equals(raw, _lastBlacklistRaw, StringComparison.Ordinal))
        {
            return;
        }

        lock (BlacklistLock)
        {
            if (string.Equals(raw, _lastBlacklistRaw, StringComparison.Ordinal))
            {
                return;
            }

            _blacklist = raw
                .Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _lastBlacklistRaw = raw;
        }
    }

    private static IEnumerable<CodeInstruction> InsertWaterEquipmentBypass(
        IEnumerable<CodeInstruction> instructions,
        string methodName,
        IReadOnlyList<CodeInstruction> guardInstructions)
    {
        List<CodeInstruction> code = new(instructions);
        if (!TryFindSwimmingRestrictionInsertionPoint(code, methodName, out CodeMatcher codeMatcher, out Label branchTarget))
        {
            return code;
        }

        List<CodeInstruction> insertedInstructions = new(guardInstructions)
        {
            new(OpCodes.Brfalse, branchTarget)
        };

        ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogDebug($"Applied water equipment bypass transpiler to {methodName}.");
        return codeMatcher
            .InsertAndAdvance(insertedInstructions)
            .InstructionEnumeration();
    }

    private static bool TryFindSwimmingRestrictionInsertionPoint(
        List<CodeInstruction> code,
        string methodName,
        out CodeMatcher codeMatcher,
        out Label branchTarget)
    {
        branchTarget = default;
        codeMatcher = new CodeMatcher(code);
        codeMatcher.MatchStartForward(SwimmingRestrictionPattern);
        if (!codeMatcher.IsValid)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning(
                $"Failed to locate swimming item restriction in {methodName}. Water equipment bypass for this method is disabled; vanilla swimming restrictions remain.");
            return false;
        }

        object swimmingBranchTarget = codeMatcher.InstructionAt(2).operand;
        object groundBranchTarget = codeMatcher.InstructionAt(5).operand;
        if (swimmingBranchTarget is not Label target
            || groundBranchTarget is not Label
            || !swimmingBranchTarget.Equals(groundBranchTarget))
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning(
                $"Swimming item restriction in {methodName} does not use the expected common branch target. Water equipment bypass for this method is disabled; vanilla swimming restrictions remain.");
            return false;
        }

        bool targetExists = false;
        foreach (CodeInstruction instruction in code)
        {
            if (instruction.labels.Contains(target))
            {
                targetExists = true;
                break;
            }
        }

        if (!targetExists)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning(
                $"Swimming item restriction branch target in {methodName} could not be resolved. Water equipment bypass for this method is disabled; vanilla swimming restrictions remain.");
            return false;
        }

        branchTarget = target;
        codeMatcher.Advance(SwimmingRestrictionPattern.Length);
        if (!codeMatcher.IsValid)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning(
                $"Failed to advance water equipment transpiler cursor in {methodName}. Water equipment bypass for this method is disabled; vanilla swimming restrictions remain.");
            return false;
        }

        return true;
    }
}
