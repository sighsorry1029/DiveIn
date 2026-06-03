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
}

internal sealed class PlayerDiveController : MonoBehaviour
{
    private const float HeadUnderwaterTolerance = 0.01f;
    private const float MinimumSurfaceSwimDepth = 0.1f;
    private const float DivingSwimDepthOffset = 1.1f;
    private const float BottomAscendDepthStep = 0.75f;
    private const float CombatMovementSuppressionDuration = 0.1f;
    private float _surfaceSwimDepth = 2f;
    private bool _underwaterMovementActive;
    private bool _fastSwimEnabled;
    private bool _hasSwimSpeedOverride;
    private float _originalSwimSpeed;
    private float _activeSwimRunStaminaDrainMultiplier = 1f;
    private float _combatMovementSuppressedUntilTime;
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
        _underwaterMovementActive = false;
        _fastSwimEnabled = false;
        ResetSwimDepthToDefault();
        ResetSwimSpeedOverride();
    }

    internal void ResetSwimDepthIfNotInWater()
    {
        if (!Player.InWater())
        {
            DisableUnderwaterMovement();
        }
    }

    internal void ResetSwimDepthToDefault()
    {
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

        if (Player.GetGroundHeight(Player.transform.position, out float height, out Vector3 _)
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
        return ServerSyncModTemplatePlugin.IsSwimRunEnabled() && _fastSwimEnabled;
    }

    internal void UpdateFastSwimToggle()
    {
        if (!ShouldShowDiveKeyHints() || !ServerSyncModTemplatePlugin.IsSwimRunEnabled())
        {
            _fastSwimEnabled = false;
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
        ClampSwimDepthForBottomContact();
        Player.m_body.WakeUp();
        Player.m_lastGroundTouch = 0.3f;
        Player.m_swimTimer = 0f;
    }

    internal bool IsUnderSurface()
    {
        return Player.m_swimDepth > _surfaceSwimDepth + HeadUnderwaterTolerance;
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

        if (Player.InAttack() || Player.InDodge() || Player.m_wallRunning || Player.IsEncumbered())
        {
            regenFactor = 0f;
        }

        float regenSpeed = (Player.m_staminaRegen
                            + (1f - Player.m_stamina / maxStamina) * Player.m_staminaRegen * Player.m_staminaRegenTimeMultiplier)
                           * regenFactor;
        float staminaMultiplier = 1f;
        Player.m_seman.ModifyStaminaRegen(ref staminaMultiplier);
        regenSpeed *= staminaMultiplier;
        regenSpeed *= waterRegenRate;
        if (Player.m_stamina < maxStamina && Player.m_staminaRegenTimer <= 0f)
        {
            Player.m_stamina = Mathf.Min(maxStamina, Player.m_stamina + regenSpeed * dt * Game.m_staminaRegenRate);
        }
    }

    internal void ApplyIdleMidwaterStaminaDrain(float dt)
    {
        float drainPerMeter = Mathf.Max(0f, ServerSyncModTemplatePlugin._midwaterIdleStaminaDrainPerDepth.Value);
        if (drainPerMeter <= 0f || !IsHeadUnderwater() || !IsIdleInWater())
        {
            return;
        }

        float liquidDepth = Mathf.Max(0f, Player.InLiquidDepth());
        float drainPerSecond = liquidDepth * drainPerMeter;
        if (drainPerSecond <= 0f)
        {
            return;
        }

        Player.UseStamina(drainPerSecond * dt);
    }

    internal void ApplyExtraSwimStaminaDrain(float dt)
    {
        float drainMultiplier = GetExtraSwimStaminaDrainMultiplier();
        if (drainMultiplier <= 1f)
        {
            return;
        }

        float staminaDrain = GetModifiedSwimStaminaDrain();
        float extraDrainMultiplier = drainMultiplier - 1f;
        if (extraDrainMultiplier <= 0f)
        {
            return;
        }

        Player.UseStamina(dt * staminaDrain * Game.m_moveStaminaRate * extraDrainMultiplier);
    }

    internal void UpdateSwimSpeed()
    {
        ResetSwimSpeedOverride();
        float skillSpeedMultiplier = GetSwimSkillSpeedMultiplier();
        float runSpeedMultiplier = GetSwimRunSpeedMultiplier();
        _activeSwimRunStaminaDrainMultiplier = GetSwimRunStaminaDrainMultiplier();

        float speedMultiplier = skillSpeedMultiplier * runSpeedMultiplier;
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
        float swimSkillFactor = Player.m_skills.GetSkillFactor(Skills.SkillType.Swim);
        float maxSkillMultiplier = Mathf.Max(1f, ServerSyncModTemplatePlugin._playerSwimSkillSpeedMultiplier.Value);
        return Mathf.Lerp(1f, maxSkillMultiplier, swimSkillFactor);
    }

    private float GetSwimRunSpeedMultiplier()
    {
        if (!IsFastSwimEnabled() || !Player.HaveStamina())
        {
            return 1f;
        }

        return Mathf.Max(1f, ServerSyncModTemplatePlugin._fastSwimSpeedMultiplier.Value);
    }

    private float GetSwimRunStaminaDrainMultiplier()
    {
        if (!IsFastSwimEnabled() || !Player.HaveStamina())
        {
            return 1f;
        }

        return Mathf.Max(1f, ServerSyncModTemplatePlugin._fastSwimStaminaDrainMultiplier.Value);
    }

    private float GetModifiedSwimStaminaDrain()
    {
        float skillFactor = Player.m_skills.GetSkillFactor(Skills.SkillType.Swim);
        float staminaDrain = Mathf.Lerp(Player.m_swimStaminaDrainMinSkill, Player.m_swimStaminaDrainMaxSkill, skillFactor);
        staminaDrain += staminaDrain * Player.GetEquipmentSwimStaminaModifier();
        Player.m_seman.ModifySwimStaminaUsage(staminaDrain, ref staminaDrain);
        return staminaDrain;
    }

    internal void Dive(float dt, bool ascend, out Vector3? defaultMoveDir)
    {
        defaultMoveDir = Player.m_moveDir;
        Player.m_moveDir = GetDiveDirection(ascend);
        if (ascend)
        {
            EnsureAscendTargetFromBottom();
        }

        Vector3 diveVelocity = CalculateSwimVelocity();
        float newDepth = Player.m_swimDepth - (diveVelocity.y * dt);
        Player.m_swimDepth = Mathf.Max(newDepth, _surfaceSwimDepth);
    }

    private void EnsureAscendTargetFromBottom()
    {
        float currentLiquidDepth = Player.InLiquidDepth();
        if (currentLiquidDepth <= _surfaceSwimDepth || !UnderwaterDepthUtils.IsAtUnderwaterBottom(Player))
        {
            return;
        }

        float ascendTargetDepth = Mathf.Max(_surfaceSwimDepth, currentLiquidDepth - BottomAscendDepthStep);
        if (Player.m_swimDepth > ascendTargetDepth)
        {
            Player.m_swimDepth = ascendTargetDepth;
        }

        Player.m_body.WakeUp();
    }

    private void ClampSwimDepthForBottomContact()
    {
        Player.m_swimDepth = UnderwaterDepthUtils.ClampDepthAboveBottom(Player, Player.m_swimDepth, _surfaceSwimDepth);
    }

    private Vector3 GetDiveDirection(bool ascend)
    {
        Vector3 verticalDirection = ascend ? Vector3.up : Vector3.down;
        Vector3 horizontalDirection = Player.m_moveDir;
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
        Vector3 horizontalDirection = Player.m_lookDir;
        horizontalDirection.y = 0f;
        horizontalDirection.Normalize();
        return horizontalDirection * scale;
    }

    private Vector3 CalculateSwimVelocity()
    {
        float speed = Player.m_swimSpeed * Player.GetAttackSpeedFactorMovement();
        if (Player.InMinorActionSlowdown())
        {
            speed = 0f;
        }

        Player.m_seman.ApplyStatusEffectSpeedMods(ref speed, Player.m_moveDir);
        Vector3 velocity = Player.m_moveDir * speed;
        velocity = Vector3.Lerp(Player.m_currentVel, velocity, Player.m_swimAcceleration);
        Player.AddPushbackForce(ref velocity);
        return velocity;
    }

    private float GetExtraSwimStaminaDrainMultiplier()
    {
        return GetDepthSwimStaminaDrainMultiplier() * Mathf.Max(1f, _activeSwimRunStaminaDrainMultiplier);
    }

    private float GetDepthSwimStaminaDrainMultiplier()
    {
        float percentPerMeter = Mathf.Max(0f, ServerSyncModTemplatePlugin._swimStaminaDrainMultiplierPerDepth.Value);
        float swimDepth = Mathf.Max(0f, Player.InLiquidDepth());
        return 1f + swimDepth * percentPerMeter / 100f;
    }
}
