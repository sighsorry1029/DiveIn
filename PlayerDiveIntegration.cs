using HarmonyLib;
using UnityEngine;

namespace ServerSyncModTemplate;

[HarmonyPatch]
internal static class PlayerDivePatches
{
    private readonly struct SwimmingUpdateState
    {
        internal SwimmingUpdateState(PlayerDiveController? diver, Vector3? originalMoveDir)
        {
            Diver = diver;
            OriginalMoveDir = originalMoveDir;
        }

        internal PlayerDiveController? Diver { get; }
        internal Vector3? OriginalMoveDir { get; }
    }

    private readonly struct ResourceUpdateState
    {
        internal ResourceUpdateState(PlayerDiveController? diver, float eitr)
        {
            Diver = diver;
            Eitr = eitr;
        }

        internal PlayerDiveController? Diver { get; }
        internal float Eitr { get; }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.Awake))]
    private static void PlayerAwakePostfix(Player __instance)
    {
        if (__instance == Player.m_localPlayer)
        {
            _ = PlayerDiveUtils.EnsureLocalDiver();
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Character), nameof(Character.UpdateMotion))]
    private static void CharacterUpdateMotionPrefix(Character __instance)
    {
        if (__instance != Player.m_localPlayer)
        {
            return;
        }

        PlayerDiveController? diver = PlayerDiveUtils.EnsureLocalDiver();
        if (diver == null)
        {
            return;
        }

        diver.ResetSwimDepthIfNotInWater();
        diver.RefreshUnderwaterMovementState();
        diver.UpdateFastSwimToggle();
        if (diver.ShouldForceSwimming())
        {
            diver.PrepareForcedSwimming();
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Character), nameof(Character.UpdateSwimming))]
    private static void CharacterUpdateSwimmingPrefix(Character __instance, float dt, out SwimmingUpdateState __state)
    {
        if (__instance != Player.m_localPlayer)
        {
            __state = new SwimmingUpdateState(null, null);
            return;
        }

        PlayerDiveController? diver = PlayerDiveUtils.EnsureLocalDiver();
        if (diver == null)
        {
            __state = new SwimmingUpdateState(null, null);
            return;
        }

        diver.BeginSwimmingUpdateContext();
        __state = new SwimmingUpdateState(diver, null);

        diver.UpdateSwimSpeed();
        bool movementSuppressedForCombat = diver.IsMovementSuppressedForCombat();
        if (!movementSuppressedForCombat && ServerSyncModTemplatePlugin.IsDiveAscendInputHeld() && diver.IsUnderSurface())
        {
            diver.Dive(dt, ascend: true, out Vector3? originalMoveDir);
            __state = new SwimmingUpdateState(diver, originalMoveDir);
        }
        else if (!movementSuppressedForCombat && ServerSyncModTemplatePlugin.IsDiveDescendInputHeld() && diver.CanDive())
        {
            diver.Dive(dt, ascend: false, out Vector3? originalMoveDir);
            __state = new SwimmingUpdateState(diver, originalMoveDir);
        }
        else if (__instance.IsOnGround() || !diver.IsDiving())
        {
            diver.ResetSwimDepthToDefault();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Character), nameof(Character.UpdateSwimming))]
    private static void CharacterUpdateSwimmingPostfix(Character __instance, ref SwimmingUpdateState __state)
    {
        __state.Diver?.ResetSwimSpeedOverride();
        __state.Diver?.EndSwimmingUpdateContext();
        if (__state.OriginalMoveDir.HasValue)
        {
            __instance.m_moveDir = __state.OriginalMoveDir.Value;
            __state = default;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Character), nameof(Character.UpdateRotation))]
    private static void CharacterUpdateRotationPrefix(Character __instance, out Quaternion? __state)
    {
        if (__instance != Player.m_localPlayer)
        {
            __state = null;
            return;
        }

        PlayerDiveController? diver = PlayerDiveUtils.EnsureLocalDiver();
        if (diver != null && diver.IsInSwimmingUpdateContext())
        {
            __state = __instance.transform.rotation;
            return;
        }

        __state = null;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Character), nameof(Character.UpdateRotation))]
    private static void CharacterUpdateRotationPostfix(Character __instance, float turnSpeed, float dt, ref Quaternion? __state)
    {
        if (!__state.HasValue || __instance == null || __instance != Player.m_localPlayer)
        {
            return;
        }

        if (__instance.transform.rotation != __state.Value)
        {
            return;
        }

        PlayerDiveController? diver = PlayerDiveUtils.EnsureLocalDiver();
        if (diver == null
            || !diver.IsInSwimmingUpdateContext()
            || !diver.IsUnderSurface())
        {
            return;
        }

        Player player = diver.Player;
        Quaternion targetRotation = player.AlwaysRotateCamera() || player.m_moveDir == Vector3.zero
            ? player.m_lookYaw
            : Quaternion.LookRotation(player.m_moveDir);
        float effectiveSpeed = turnSpeed * player.GetAttackSpeedFactorRotation();
        player.transform.rotation = Quaternion.RotateTowards(player.transform.rotation, targetRotation, effectiveSpeed * dt);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.OnSwimming))]
    private static void PlayerOnSwimmingPrefix(Player __instance, Vector3 targetVel, float dt)
    {
        if (__instance != Player.m_localPlayer)
        {
            return;
        }

        PlayerDiveController? diver = PlayerDiveUtils.EnsureLocalDiver();
        if (diver == null)
        {
            return;
        }

        diver.RegenWaterStamina(dt);
        diver.ApplyIdleMidwaterStaminaDrain(dt);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.OnSwimming))]
    private static void PlayerOnSwimmingPostfix(Player __instance, Vector3 targetVel, float dt)
    {
        if (__instance != Player.m_localPlayer
            || targetVel.magnitude < 0.1f
            )
        {
            return;
        }

        PlayerDiveController? diver = PlayerDiveUtils.EnsureLocalDiver();
        if (diver == null)
        {
            return;
        }

        diver.ApplyExtraSwimStaminaDrain(dt);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.UpdateStats), new[] { typeof(float) })]
    private static void PlayerUpdateStatsPrefix(Player __instance, out ResourceUpdateState __state)
    {
        __state = default;
        if (__instance != Player.m_localPlayer ||
            !PlayerDiveUtils.TryGetLocalDiver(__instance, out PlayerDiveController diver) ||
            !diver.ShouldTreatAsSwimming())
        {
            return;
        }

        __state = new ResourceUpdateState(diver, __instance.m_eitr);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.UpdateStats), new[] { typeof(float) })]
    private static void PlayerUpdateStatsPostfix(Player __instance, ref ResourceUpdateState __state)
    {
        if (__state.Diver == null)
        {
            return;
        }

        float gainedEitr = __instance.m_eitr - __state.Eitr;
        if (gainedEitr <= 0f)
        {
            return;
        }

        float regenRate = __state.Diver.IsHeadUnderwater()
            ? ServerSyncModTemplatePlugin._midwaterEitrRegenRateMultiplier.Value
            : ServerSyncModTemplatePlugin._surfaceEitrRegenRateMultiplier.Value;
        if (regenRate >= 1f)
        {
            return;
        }

        __instance.m_eitr = Mathf.Min(__instance.GetMaxEitr(), __state.Eitr + gainedEitr * Mathf.Clamp01(regenRate));
        if (__instance.m_nview != null && __instance.m_nview.IsValid())
        {
            __instance.m_nview.GetZDO().Set(ZDOVars.s_eitr, __instance.m_eitr);
        }
    }

}
