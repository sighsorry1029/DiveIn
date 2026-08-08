using System;
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

    private readonly struct SwimmingStaminaState
    {
        internal SwimmingStaminaState(
            PlayerDiveController? diver,
            float staminaBeforeVanillaSwim,
            bool isMoving)
        {
            Diver = diver;
            StaminaBeforeVanillaSwim = staminaBeforeVanillaSwim;
            IsMoving = isMoving;
        }

        internal PlayerDiveController? Diver { get; }
        internal float StaminaBeforeVanillaSwim { get; }
        internal bool IsMoving { get; }
    }

    [ThreadStatic]
    private static int _swimStaminaModifierContextDepth;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.Awake))]
    private static void PlayerAwakePostfix(Player __instance)
    {
        _ = PlayerDiveUtils.TryGetLocalDiver(__instance, out _);
    }

    [HarmonyPostfix]
    [HarmonyPatch(
        typeof(Player),
        nameof(Player.TeleportTo),
        new[] { typeof(Vector3), typeof(Quaternion), typeof(bool) })]
    private static void PlayerTeleportToPostfix(Player __instance, bool __result)
    {
        if (__result
            && PlayerDiveUtils.TryGetLocalDiver(__instance, out PlayerDiveController diver))
        {
            diver.BeginWaterTeleportTransition();
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Character), nameof(Character.UpdateMotion))]
    private static void CharacterUpdateMotionPrefix(Character __instance)
    {
        if (__instance is not Player player ||
            !PlayerDiveUtils.TryGetLocalDiver(player, out PlayerDiveController diver))
        {
            return;
        }

        diver.UpdateWaterTeleportTransition();
        diver.ResetSwimDepthIfNotInWater();
        diver.RefreshUnderwaterMovementState();
        diver.UpdateFastSwimInput();
        if (diver.ShouldForceSwimming())
        {
            diver.PrepareForcedSwimming();
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Character), nameof(Character.UpdateSwimming))]
    private static void CharacterUpdateSwimmingPrefix(Character __instance, float dt, out SwimmingUpdateState __state)
    {
        if (__instance is not Player player ||
            !PlayerDiveUtils.TryGetLocalDiver(player, out PlayerDiveController diver))
        {
            __state = default;
            return;
        }

        __state = new SwimmingUpdateState(diver, null);
        diver.BeginSwimmingUpdateContext();

        diver.UpdateSwimSpeed();
        bool movementSuppressedForCombat = diver.IsMovementSuppressedForCombat();
        if (!movementSuppressedForCombat && ServerSyncModTemplatePlugin.IsDiveAscendInputHeld() && diver.CanContinueAscending())
        {
            __state = new SwimmingUpdateState(diver, __instance.m_moveDir);
            diver.Dive(dt, ascend: true);
        }
        else if (!movementSuppressedForCombat && ServerSyncModTemplatePlugin.IsDiveDescendInputHeld() && diver.CanDive())
        {
            __state = new SwimmingUpdateState(diver, __instance.m_moveDir);
            diver.Dive(dt, ascend: false);
        }
        else if (__instance.IsOnGround() || !diver.IsDiving())
        {
            diver.ResetSwimDepthToDefault();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Character), nameof(Character.UpdateSwimming))]
    private static void CharacterUpdateSwimmingPostfix(Character __instance, float dt, ref SwimmingUpdateState __state)
    {
        __state.Diver?.UpdateSurfaceRotationLeveling(dt);
        RestoreSwimmingUpdateState(__instance, ref __state);
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Character), nameof(Character.UpdateSwimming))]
    private static void CharacterUpdateSwimmingFinalizer(Character __instance, ref SwimmingUpdateState __state)
    {
        RestoreSwimmingUpdateState(__instance, ref __state);
    }

    private static void RestoreSwimmingUpdateState(Character instance, ref SwimmingUpdateState state)
    {
        PlayerDiveController? diver = state.Diver;
        Vector3? originalMoveDir = state.OriginalMoveDir;
        state = default;
        if (diver == null)
        {
            return;
        }

        try
        {
            diver.ResetSwimSpeedOverride();
        }
        finally
        {
            try
            {
                diver.EndSwimmingUpdateContext();
            }
            finally
            {
                if (originalMoveDir.HasValue && instance != null)
                {
                    instance.m_moveDir = originalMoveDir.Value;
                }
            }
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Character), nameof(Character.UpdateRotation))]
    private static void CharacterUpdateRotationPrefix(Character __instance, out Quaternion? __state)
    {
        if (__instance is Player player &&
            PlayerDiveUtils.TryGetLocalDiver(player, out PlayerDiveController diver) &&
            diver.IsInSwimmingUpdateContext())
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
        if (!__state.HasValue ||
            __instance == null ||
            __instance is not Player player ||
            player.transform.rotation != __state.Value ||
            !PlayerDiveUtils.TryGetLocalDiver(player, out PlayerDiveController diver))
        {
            return;
        }

        if (!diver.IsInSwimmingUpdateContext()
            || !diver.IsUnderSurface())
        {
            return;
        }

        Player localPlayer = diver.Player;
        Quaternion targetRotation = localPlayer.AlwaysRotateCamera() || localPlayer.m_moveDir == Vector3.zero
            ? localPlayer.m_lookYaw
            : Quaternion.LookRotation(localPlayer.m_moveDir);
        float effectiveSpeed = turnSpeed * localPlayer.GetAttackSpeedFactorRotation();
        localPlayer.transform.rotation = Quaternion.RotateTowards(localPlayer.transform.rotation, targetRotation, effectiveSpeed * dt);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.OnSwimming))]
    private static void PlayerOnSwimmingPrefix(Player __instance, Vector3 targetVel, float dt, out SwimmingStaminaState __state)
    {
        __state = default;
        if (!PlayerDiveUtils.TryGetLocalDiver(__instance, out PlayerDiveController diver))
        {
            return;
        }

        diver.RegenWaterStamina(dt);
        diver.ApplyIdleMidwaterStaminaDrain(dt);

        bool isMoving = targetVel.magnitude >= 0.1f;
        __state = new SwimmingStaminaState(diver, __instance.m_stamina, isMoving);
        if (isMoving)
        {
            BeginSwimStaminaModifierContext();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.OnSwimming))]
    private static void PlayerOnSwimmingPostfix(Player __instance, ref SwimmingStaminaState __state)
    {
        if (__state.Diver == null
            || !__state.IsMoving)
        {
            return;
        }

        __state.Diver.AdjustMovingSwimStaminaDrain(__state.StaminaBeforeVanillaSwim);
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Player), nameof(Player.OnSwimming))]
    private static void PlayerOnSwimmingFinalizer(ref SwimmingStaminaState __state)
    {
        if (__state.IsMoving)
        {
            EndSwimStaminaModifierContext();
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifySwimStaminaUsage))]
    private static bool SEManModifySwimStaminaUsagePrefix(SEMan __instance, float baseStaminaUse, ref float staminaUse, bool minZero)
    {
        if (!ShouldUseMultiplicativeSwimStaminaModifiers(baseStaminaUse, staminaUse, minZero))
        {
            return true;
        }

        float modifier = 1f;
        foreach (StatusEffect statusEffect in __instance.m_statusEffects)
        {
            if (statusEffect == null)
            {
                continue;
            }

            float modifiedUse = baseStaminaUse;
            statusEffect.ModifySwimStaminaUsage(baseStaminaUse, ref modifiedUse);
            if (float.IsNaN(modifiedUse) || float.IsInfinity(modifiedUse))
            {
                continue;
            }

            modifier *= Mathf.Max(0f, modifiedUse / baseStaminaUse);
        }

        staminaUse = Mathf.Max(0f, baseStaminaUse * modifier);
        return false;
    }

    private static bool ShouldUseMultiplicativeSwimStaminaModifiers(float baseStaminaUse, float staminaUse, bool minZero)
    {
        return ServerSyncModTemplatePlugin.UseMultiplicativeSwimStaminaModifiers()
               && IsInSwimStaminaModifierContext()
               && minZero
               && baseStaminaUse > 0f
               && Mathf.Approximately(staminaUse, baseStaminaUse);
    }

    private static void BeginSwimStaminaModifierContext()
    {
        _swimStaminaModifierContextDepth++;
    }

    private static void EndSwimStaminaModifierContext()
    {
        if (_swimStaminaModifierContextDepth > 0)
        {
            _swimStaminaModifierContextDepth--;
        }
    }

    private static bool IsInSwimStaminaModifierContext()
    {
        return _swimStaminaModifierContextDepth > 0;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.UpdateStats), new[] { typeof(float) })]
    private static void PlayerUpdateStatsPrefix(Player __instance, out ResourceUpdateState __state)
    {
        __state = default;
        if (!PlayerDiveUtils.TryGetLocalDiver(__instance, out PlayerDiveController diver) ||
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

        float gainedEitr = Mathf.Max(0f, __instance.m_eitr - __state.Eitr);
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

        float scaledGain = gainedEitr * Mathf.Clamp01(regenRate);
        __instance.m_eitr = Mathf.Clamp(
            __state.Eitr + scaledGain,
            0f,
            __instance.GetMaxEitr());
        if (__instance.m_nview != null && __instance.m_nview.IsValid())
        {
            __instance.m_nview.GetZDO().Set(ZDOVars.s_eitr, __instance.m_eitr);
        }
    }

}
