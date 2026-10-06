using UnityEngine;

namespace ServerSyncModTemplate;

internal static class PlayerDiveUtils
{
    internal static PlayerDiveController? EnsureLocalDiver()
    {
        Player player = Player.m_localPlayer;
        if (player == null)
        {
            return null;
        }

        PlayerDiveController? diver = PlayerDiveController.LocalInstance;
        if (diver != null && diver.Player == player)
        {
            return diver;
        }

        if (!player.TryGetComponent(out diver))
        {
            diver = player.gameObject.AddComponent<PlayerDiveController>();
        }

        return diver;
    }

    internal static bool TryGetLocalDiver(Player player, out PlayerDiveController diver)
    {
        if (!IsValidLocalPlayer(player))
        {
            diver = null!;
            return false;
        }

        PlayerDiveController? ensuredDiver = EnsureLocalDiver();
        if (ensuredDiver == null)
        {
            diver = null!;
            return false;
        }

        diver = ensuredDiver;
        return true;
    }

    internal static bool IsValidLocalPlayer(Player player)
    {
        return player != null && player == Player.m_localPlayer;
    }

    internal static bool TryGetUnderwaterLocalDiver(Player player, out PlayerDiveController diver)
    {
        diver = null!;
        return TryGetLocalDiver(player, out diver)
               && diver.ShouldTreatAsSwimming();
    }
}

internal sealed class PlayerDiveController : MonoBehaviour
{
    private const float HeadUnderwaterTolerance = 0.01f;
    private const float NoLiquidLevel = -10000f;
    private const float MinimumSurfaceSwimDepth = 0.1f;
    private const float SurfaceExitClearance = 0.1f;
    private const float DivingSwimDepthOffset = 1.1f;
    private const float BottomAscendDepthStep = 0.75f;
    private const float CombatMovementSuppressionDuration = 0.1f;
    private float _surfaceSwimDepth = 2f;
    private float? _surfaceExitSwimDepth;
    private bool _surfaceRotationLevelingActive;
    private bool _underwaterMovementActive;
    private bool _fastSwimEnabled;
    private bool _hasSwimSpeedOverride;
    private float _originalSwimSpeed;
    private float _activeSwimRunStaminaDrainMultiplier = 1f;
    private float _combatMovementSuppressedUntilTime;
    private bool _waterTeleportTransitionActive;
    private float _waterTeleportLevel = NoLiquidLevel;
    private int _swimmingUpdateContextDepth;
    private int _swimmingUpdateContextFrame = -1;

    internal static PlayerDiveController? LocalInstance { get; private set; }
    internal Player Player { get; private set; } = null!;

    private void Awake()
    {
        Player = GetComponent<Player>();
        if (Player == null)
        {
            Destroy(this);
            return;
        }

        if (Player != Player.m_localPlayer)
        {
            Destroy(this);
            return;
        }

        LocalInstance = this;
        _surfaceSwimDepth = Mathf.Max(MinimumSurfaceSwimDepth, Player.m_swimDepth);
    }

    private void OnDestroy()
    {
        if (LocalInstance == this)
        {
            LocalInstance = null;
        }
    }

    internal void DisableUnderwaterMovement()
    {
        ClearWaterTeleportTransition();
        _underwaterMovementActive = false;
        _fastSwimEnabled = false;
        _surfaceRotationLevelingActive = false;
        ResetSwimDepthToDefault();
        ResetSwimSpeedOverride();
    }

    internal void BeginWaterTeleportTransition()
    {
        _waterTeleportTransitionActive = true;
        RefreshWaterTeleportLevel();
        _ = TryRestoreSubmergedTeleportState();
    }

    internal void UpdateWaterTeleportTransition()
    {
        if (Player.IsTeleporting())
        {
            _waterTeleportTransitionActive = true;
            RefreshWaterTeleportLevel();
            _ = TryRestoreSubmergedTeleportState();
            return;
        }

        if (!_waterTeleportTransitionActive)
        {
            return;
        }

        RefreshWaterTeleportLevel();
        if (TryRestoreSubmergedTeleportState())
        {
            if (Player.InWater())
            {
                ClearWaterTeleportTransition();
            }

            return;
        }

        DisableUnderwaterMovement();
    }

    internal void ResetSwimDepthIfNotInWater()
    {
        if (_waterTeleportTransitionActive || Player.IsTeleporting())
        {
            return;
        }

        if (!Player.InWater())
        {
            DisableUnderwaterMovement();
        }
    }

    internal bool TryGetSubmergedTeleportWaterLevel(out float waterLevel)
    {
        waterLevel = NoLiquidLevel;
        if (!_waterTeleportTransitionActive && !Player.IsTeleporting())
        {
            return false;
        }

        if (_waterTeleportLevel <= NoLiquidLevel)
        {
            return false;
        }

        Vector3 playerPosition = Player.transform.position;
        float eyeY = Player.m_eye != null ? Player.m_eye.position.y : playerPosition.y;
        if (_waterTeleportLevel - eyeY <= HeadUnderwaterTolerance)
        {
            return false;
        }

        float physicalDepth = _waterTeleportLevel - playerPosition.y;
        if (!IsUnderSurface()
            && physicalDepth <= _surfaceSwimDepth + DivingSwimDepthOffset)
        {
            return false;
        }

        waterLevel = _waterTeleportLevel;
        return true;
    }

    private void RefreshWaterTeleportLevel()
    {
        // InWater is forced false while teleporting, so query the live WaterVolume instead of Character caches.
        _waterTeleportLevel = Floating.GetLiquidLevel(
            Player.transform.position,
            1f,
            LiquidType.Water);
    }

    private void ClearWaterTeleportTransition()
    {
        _waterTeleportTransitionActive = false;
        _waterTeleportLevel = NoLiquidLevel;
    }

    private bool TryRestoreSubmergedTeleportState()
    {
        if (!TryGetSubmergedTeleportWaterLevel(out float waterLevel))
        {
            return false;
        }

        float physicalDepth = waterLevel - Player.transform.position.y;
        Player.m_swimDepth = UnderwaterDepthUtils.ClampDepthAboveBottom(
            Player,
            physicalDepth,
            _surfaceSwimDepth);
        _surfaceExitSwimDepth = null;
        _surfaceRotationLevelingActive = false;

        _underwaterMovementActive = true;
        return true;
    }

    internal void ResetSwimDepthToDefault()
    {
        _surfaceExitSwimDepth = null;
        Player.m_swimDepth = _surfaceSwimDepth;
    }

    internal bool CanDive()
    {
        if (ShouldForceDive())
        {
            return true;
        }

        if (!Player.InWater() || Player.IsOnGround() || !Player.IsSwimming())
        {
            return false;
        }

        if (GameAccess.GroundHeight(Player, Player.transform.position, out float height, out Vector3 _)
            && Player.transform.position.y - height < 1f)
        {
            return false;
        }

        return true;
    }

    internal bool IsHeadUnderwater()
    {
        float eyeY = Player.m_eye != null ? Player.m_eye.position.y : Player.transform.position.y;
        return Player.GetLiquidLevel() - eyeY > HeadUnderwaterTolerance;
    }

    internal void RefreshUnderwaterMovementState()
    {
        if (_waterTeleportTransitionActive || Player.IsTeleporting())
        {
            return;
        }

        if (!Player.InWater() || !IsHeadUnderwater())
        {
            _underwaterMovementActive = false;
            return;
        }

        if (IsUnderSurface())
        {
            _underwaterMovementActive = true;
        }
    }

    internal bool ShouldForceSwimming()
    {
        return _underwaterMovementActive && Player.InWater() && IsHeadUnderwater();
    }

    internal bool ShouldShowDiveKeyHints()
    {
        return ShouldTreatAsSwimming();
    }

    internal bool ShouldTreatAsSwimming()
    {
        return Player.InWater() && (Player.IsSwimming() || ShouldForceSwimming());
    }

    internal bool IsFastSwimEnabled()
    {
        return CanUseFastSwim() && _fastSwimEnabled;
    }

    internal bool CanUseFastSwim()
    {
        return ServerSyncModTemplatePlugin.IsSwimRunEnabled() && !Player.IsEncumbered();
    }

    internal void UpdateFastSwimInput()
    {
        if (!ShouldShowDiveKeyHints() || !CanUseFastSwim())
        {
            _fastSwimEnabled = false;
            return;
        }

        if (!ServerSyncModTemplatePlugin.UseFastSwimToggleInput())
        {
            _fastSwimEnabled = ZInput.GetButton("Run") || ZInput.GetButton("JoyRun");
            return;
        }

        if (ZInput.GetButtonDown("Run") || ZInput.GetButtonDown("JoyRun"))
        {
            _fastSwimEnabled = !_fastSwimEnabled;
        }
    }

    internal void SuppressMovementForCombat()
    {
        _combatMovementSuppressedUntilTime = Mathf.Max(
            _combatMovementSuppressedUntilTime,
            Time.time + CombatMovementSuppressionDuration);
    }

    internal bool IsMovementSuppressedForCombat()
    {
        return Time.time <= _combatMovementSuppressedUntilTime;
    }

    internal bool ShouldForceDive()
    {
        return ShouldForceSwimming() && !Player.IsOnGround();
    }

    internal void PrepareForcedSwimming()
    {
        Player.m_swimDepth = UnderwaterDepthUtils.ClampDepthAboveBottom(
            Player,
            Player.m_swimDepth,
            _surfaceSwimDepth);
        GameAccess.Body(Player).WakeUp();
        GameAccess.LastGroundTouch(Player) = 0.3f;
        GameAccess.SwimTimer(Player) = 0f;
    }

    internal bool IsUnderSurface()
    {
        return Player.m_swimDepth > _surfaceSwimDepth + HeadUnderwaterTolerance;
    }

    internal bool CanContinueAscending()
    {
        return IsUnderSurface() || ShouldForceSwimming();
    }

    internal bool IsDiving()
    {
        return Player.m_swimDepth > _surfaceSwimDepth + DivingSwimDepthOffset;
    }

    internal bool IsSurfacing()
    {
        return !IsDiving() && IsUnderSurface();
    }

    internal bool IsIdleInWater()
    {
        return ShouldTreatAsSwimming()
               && Player.GetVelocity().magnitude < 1f;
    }

    internal void BeginSwimmingUpdateContext()
    {
        _swimmingUpdateContextDepth++;
        _swimmingUpdateContextFrame = Time.frameCount;
    }

    internal void EndSwimmingUpdateContext()
    {
        if (_swimmingUpdateContextDepth > 0)
        {
            _swimmingUpdateContextDepth--;
        }

        if (_swimmingUpdateContextDepth == 0)
        {
            _swimmingUpdateContextFrame = -1;
        }
    }

    internal bool IsInSwimmingUpdateContext()
    {
        return _swimmingUpdateContextDepth > 0 && _swimmingUpdateContextFrame == Time.frameCount;
    }

    internal void RegenWaterStamina(float dt)
    {
        float waterRegenRate = IsHeadUnderwater()
            ? ServerSyncModTemplatePlugin._midwaterStaminaRegenRateMultiplier.Value
            : ServerSyncModTemplatePlugin._surfaceStaminaRegenRateMultiplier.Value;
        if (waterRegenRate <= 0f)
        {
            return;
        }

        float maxStamina = Player.GetMaxStamina();
        float regenFactor = 1f;
        if (Player.IsBlocking())
        {
            regenFactor *= 0.8f;
        }

        if (Player.InAttack() || Player.InDodge() || GameAccess.WallRunning(Player) || Player.IsEncumbered())
        {
            regenFactor = 0f;
        }

        float regenSpeed = (Player.m_staminaRegen
                            + (1f - Player.GetStamina() / maxStamina) * Player.m_staminaRegen * Player.m_staminaRegenTimeMultiplier)
                           * regenFactor;
        float staminaMultiplier = 1f;
        Player.GetSEMan().ModifyStaminaRegen(ref staminaMultiplier);
        regenSpeed *= staminaMultiplier;
        regenSpeed *= waterRegenRate;
        if (Player.GetStamina() < maxStamina && GameAccess.StaminaRegenTimer(Player) <= 0f)
        {
            GameAccess.Stamina(Player) = Mathf.Min(maxStamina, Player.GetStamina() + regenSpeed * dt * Game.m_staminaRegenRate);
        }
    }

    internal void ApplyIdleMidwaterStaminaDrain(float dt)
    {
        float drainPerMeter = Mathf.Max(0f, ServerSyncModTemplatePlugin._midwaterIdleStaminaDrainPerDepth.Value);
        if (drainPerMeter <= 0f || !IsHeadUnderwater() || !IsIdleInWater())
        {
            return;
        }

        float liquidDepth = Mathf.Max(0f, GameAccess.LiquidDepth(Player));
        float drainPerSecond = liquidDepth * drainPerMeter;
        if (drainPerSecond <= 0f)
        {
            return;
        }

        Player.UseStamina(drainPerSecond * dt);
    }

    internal void AdjustMovingSwimStaminaDrain(float staminaBeforeVanillaSwim)
    {
        float vanillaDrain = Mathf.Max(0f, staminaBeforeVanillaSwim - Player.GetStamina());
        if (vanillaDrain <= 0f)
        {
            return;
        }

        float drainMultiplier = GetMovingSwimStaminaDrainMultiplier();
        if (Mathf.Approximately(drainMultiplier, 1f))
        {
            return;
        }

        float scaledDrain = vanillaDrain * Mathf.Max(0f, drainMultiplier);
        float targetStamina = Mathf.Clamp(
            staminaBeforeVanillaSwim - scaledDrain,
            0f,
            Player.GetMaxStamina());
        if (targetStamina < Player.GetStamina())
        {
            float extraDrain = Player.GetStamina() - targetStamina;
            // The observed drain already includes this rate; UseStamina applies it again.
            // Keep UseStamina's hooks, ownership handling and regen delay for the extra cost.
            float staminaRate = Game.m_staminaRate;
            Player.UseStamina(staminaRate > 0f ? extraDrain / staminaRate : extraDrain);
            return;
        }

        GameAccess.Stamina(Player) = targetStamina;
    }

    internal void UpdateSwimSpeed()
    {
        ResetSwimSpeedOverride();
        float skillSpeedMultiplier = GetSwimSkillSpeedMultiplier();
        float encumberedSpeedMultiplier = GetEncumberedSwimSpeedMultiplier();
        bool fastSwimActive = IsFastSwimEnabled() && Player.HaveStamina();
        float runSpeedMultiplier = fastSwimActive
            ? Mathf.Max(1f, ServerSyncModTemplatePlugin._fastSwimSpeedMultiplier.Value)
            : 1f;
        _activeSwimRunStaminaDrainMultiplier = fastSwimActive
            ? Mathf.Max(1f, ServerSyncModTemplatePlugin._fastSwimStaminaDrainMultiplier.Value)
            : 1f;

        float speedMultiplier = skillSpeedMultiplier * encumberedSpeedMultiplier * runSpeedMultiplier;
        if (Mathf.Approximately(speedMultiplier, 1f))
        {
            return;
        }

        _originalSwimSpeed = Player.m_swimSpeed;
        _hasSwimSpeedOverride = true;
        Player.m_swimSpeed *= speedMultiplier;
    }

    internal void ResetSwimSpeedOverride()
    {
        if (!_hasSwimSpeedOverride)
        {
            _activeSwimRunStaminaDrainMultiplier = 1f;
            return;
        }

        Player.m_swimSpeed = _originalSwimSpeed;
        _hasSwimSpeedOverride = false;
        _activeSwimRunStaminaDrainMultiplier = 1f;
    }

    private float GetSwimSkillSpeedMultiplier()
    {
        float swimSkillFactor = Player.GetSkills().GetSkillFactor(Skills.SkillType.Swim);
        float maxSkillMultiplier = Mathf.Max(1f, ServerSyncModTemplatePlugin._playerSwimSkillSpeedMultiplier.Value);
        return Mathf.Lerp(1f, maxSkillMultiplier, swimSkillFactor);
    }

    private float GetEncumberedSwimSpeedMultiplier()
    {
        if (!Player.IsEncumbered())
        {
            return 1f;
        }

        return Mathf.Clamp(ServerSyncModTemplatePlugin._encumberedSwimSpeedMultiplier.Value, 0.1f, 1f);
    }

    internal void Dive(float dt, bool ascend)
    {
        Player.SetMoveDir(GetDiveDirection(ascend));
        if (ascend)
        {
            EnsureAscendTargetFromBottom();
        }
        else
        {
            _surfaceExitSwimDepth = null;
            _surfaceRotationLevelingActive = false;
        }

        Vector3 diveVelocity = CalculateSwimVelocity();
        float newDepth = Player.m_swimDepth - (diveVelocity.y * dt);
        float minimumDepth = ascend ? GetAscendMinimumDepth() : _surfaceSwimDepth;
        Player.m_swimDepth = Mathf.Max(newDepth, minimumDepth);
    }

    private float GetAscendMinimumDepth()
    {
        if (IsUnderSurface() || !IsHeadUnderwater())
        {
            _surfaceExitSwimDepth = null;
            return _surfaceSwimDepth;
        }

        if (!_surfaceExitSwimDepth.HasValue)
        {
            float eyeY = Player.m_eye != null ? Player.m_eye.position.y : Player.transform.position.y;
            float headDepth = Mathf.Max(0f, Player.GetLiquidLevel() - eyeY);
            _surfaceExitSwimDepth = Mathf.Max(
                MinimumSurfaceSwimDepth,
                _surfaceSwimDepth - headDepth - SurfaceExitClearance);
            _surfaceRotationLevelingActive = true;
        }

        return _surfaceExitSwimDepth.Value;
    }

    internal void UpdateSurfaceRotationLeveling(float dt)
    {
        if (!_surfaceRotationLevelingActive)
        {
            return;
        }

        if (!ShouldTreatAsSwimming() || Player.IsOnGround())
        {
            _surfaceRotationLevelingActive = false;
            return;
        }

        Vector3 horizontalForward = Player.transform.forward;
        horizontalForward.y = 0f;
        if (horizontalForward.sqrMagnitude < 0.0001f)
        {
            horizontalForward = GetHorizontalLookDirection(1f);
        }

        if (horizontalForward.sqrMagnitude < 0.0001f)
        {
            _surfaceRotationLevelingActive = false;
            return;
        }

        Quaternion targetRotation = Quaternion.LookRotation(horizontalForward.normalized, Vector3.up);
        float effectiveSpeed = Player.m_swimTurnSpeed * GameAccess.AttackSpeedRotation(Player);
        Player.transform.rotation = Quaternion.RotateTowards(
            Player.transform.rotation,
            targetRotation,
            effectiveSpeed * dt);

        if (Quaternion.Angle(Player.transform.rotation, targetRotation) <= 0.1f)
        {
            Player.transform.rotation = targetRotation;
            _surfaceRotationLevelingActive = false;
        }
    }

    private void EnsureAscendTargetFromBottom()
    {
        float currentLiquidDepth = GameAccess.LiquidDepth(Player);
        if (currentLiquidDepth <= _surfaceSwimDepth || !UnderwaterDepthUtils.IsAtUnderwaterBottom(Player))
        {
            return;
        }

        float ascendTargetDepth = Mathf.Max(_surfaceSwimDepth, currentLiquidDepth - BottomAscendDepthStep);
        if (Player.m_swimDepth > ascendTargetDepth)
        {
            Player.m_swimDepth = ascendTargetDepth;
        }

        GameAccess.Body(Player).WakeUp();
    }

    private Vector3 GetDiveDirection(bool ascend)
    {
        Vector3 verticalDirection = ascend ? Vector3.up : Vector3.down;
        Vector3 horizontalDirection = Player.GetMoveDir();
        if (horizontalDirection.magnitude < 0.1f)
        {
            float scale = ascend && IsSurfacing() ? 0.6f : 0.05f;
            horizontalDirection = GetHorizontalLookDirection(scale);
        }

        Vector3 diveDirection = horizontalDirection + verticalDirection;
        return diveDirection.normalized;
    }

    private Vector3 GetHorizontalLookDirection(float scale)
    {
        Vector3 horizontalDirection = Player.GetLookDir();
        horizontalDirection.y = 0f;
        horizontalDirection.Normalize();
        return horizontalDirection * scale;
    }

    private Vector3 CalculateSwimVelocity()
    {
        float speed = Player.m_swimSpeed * GameAccess.AttackSpeedMovement(Player);
        if (Player.InMinorActionSlowdown())
        {
            speed = 0f;
        }

        Player.GetSEMan().ApplyStatusEffectSpeedMods(ref speed, Player.GetMoveDir());
        Vector3 velocity = Player.GetMoveDir() * speed;
        velocity = Vector3.Lerp(GameAccess.CurrentVelocity(Player), velocity, Player.m_swimAcceleration);
        GameAccess.AddPushbackForce(Player, ref velocity);
        return velocity;
    }

    private float GetMovingSwimStaminaDrainMultiplier()
    {
        float baseMultiplier = Mathf.Clamp(ServerSyncModTemplatePlugin._swimStaminaDrainBaseMultiplier.Value, 0.1f, 2f);
        return baseMultiplier * GetDepthSwimStaminaDrainMultiplier() * Mathf.Max(1f, _activeSwimRunStaminaDrainMultiplier);
    }

    private float GetDepthSwimStaminaDrainMultiplier()
    {
        float percentPerMeter = Mathf.Max(0f, ServerSyncModTemplatePlugin._swimStaminaDrainMultiplierPerDepth.Value);
        float swimDepth = Mathf.Max(0f, GameAccess.LiquidDepth(Player));
        return 1f + swimDepth * percentPerMeter / 100f;
    }
}
