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
        if (ServerSyncModTemplatePlugin.IsDiveAscendInputHeld() && diver.IsUnderSurface())
        {
            diver.Dive(dt, ascend: true, out Vector3? originalMoveDir);
            __state = new SwimmingUpdateState(diver, originalMoveDir);
        }
        else if (ServerSyncModTemplatePlugin.IsDiveDescendInputHeld() && diver.CanDive())
        {
            diver.Dive(dt, ascend: false, out Vector3? originalMoveDir);
            __state = new SwimmingUpdateState(diver, originalMoveDir);
        }
        else if ((__instance.IsOnGround() || !diver.IsDiving()) && !diver.IsIdleInWater())
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

        diver.ApplyDepthScaledSwimDrain(dt);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
    private static void PlayerSetControlsPrefix(Player __instance, ref bool crouch)
    {
        if (__instance != Player.m_localPlayer)
        {
            return;
        }

        if (__instance.IsSwimming())
        {
            crouch = false;
        }
    }
}
