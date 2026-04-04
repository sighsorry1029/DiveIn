// Contains player diving code derived from UnderTheSea (GPL-3.0) and modified for DiveIn on 2026-04-04.
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

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

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Player), nameof(Player.Update))]
    private static IEnumerable<CodeInstruction> PlayerUpdateTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        return InsertWaterEquipmentBypass<Humanoid>(instructions, "Player.Update", OpCodes.Ldarg_0, ShouldKeepWaterRestrictionForHumanoid);
    }

    private static IEnumerable<CodeInstruction> InsertWaterEquipmentBypass<T>(
        IEnumerable<CodeInstruction> instructions,
        string methodName,
        OpCode argumentLoadOpCode,
        Func<T, bool> shouldKeepWaterRestriction)
    {
        List<CodeInstruction> code = new(instructions);
        CodeMatcher codeMatcher = new(code);
        codeMatcher.MatchStartForward(SwimmingRestrictionPattern);
        if (!codeMatcher.IsValid)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning($"Failed to locate swimming item restriction in {methodName}. Leaving original instructions untouched.");
            return code;
        }

        codeMatcher.Advance(SwimmingRestrictionInstructionCount);
        if (!codeMatcher.IsValid)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning($"Failed to advance transpiler cursor in {methodName}. Leaving original instructions untouched.");
            return code;
        }

        object branchTarget = codeMatcher.InstructionAt(-1).operand;
        return codeMatcher
            .InsertAndAdvance(
                new[]
                {
                    new CodeInstruction(argumentLoadOpCode),
                    Transpilers.EmitDelegate(shouldKeepWaterRestriction),
                    new CodeInstruction(OpCodes.Brfalse, branchTarget)
                })
            .InstructionEnumeration();
    }

    private static IEnumerable<CodeInstruction> InsertWaterEquipmentBypass<T1, T2>(
        IEnumerable<CodeInstruction> instructions,
        string methodName,
        OpCode firstArgumentLoadOpCode,
        OpCode secondArgumentLoadOpCode,
        Func<T1, T2, bool> shouldKeepWaterRestriction)
    {
        List<CodeInstruction> code = new(instructions);
        CodeMatcher codeMatcher = new(code);
        codeMatcher.MatchStartForward(SwimmingRestrictionPattern);
        if (!codeMatcher.IsValid)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning($"Failed to locate swimming item restriction in {methodName}. Leaving original instructions untouched.");
            return code;
        }

        codeMatcher.Advance(SwimmingRestrictionInstructionCount);
        if (!codeMatcher.IsValid)
        {
            ServerSyncModTemplatePlugin.ServerSyncModTemplateLogger.LogWarning($"Failed to advance transpiler cursor in {methodName}. Leaving original instructions untouched.");
            return code;
        }

        object branchTarget = codeMatcher.InstructionAt(-1).operand;
        return codeMatcher
            .InsertAndAdvance(
                new[]
                {
                    new CodeInstruction(firstArgumentLoadOpCode),
                    new CodeInstruction(secondArgumentLoadOpCode),
                    Transpilers.EmitDelegate(shouldKeepWaterRestriction),
                    new CodeInstruction(OpCodes.Brfalse, branchTarget)
                })
            .InstructionEnumeration();
    }
}
