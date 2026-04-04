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
    internal const float DefaultSwimDepth = 1.4f;
    private const float DivingSwimDepth = 2.5f;

    private bool _underwaterMovementActive;
    private float _baseSwimSpeed;
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
        Player.m_swimDepth = DefaultSwimDepth;
        _baseSwimSpeed = Player.m_swimSpeed;
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
        ResetSwimDepthToDefault();
        Player.m_swimSpeed = _baseSwimSpeed;
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
        Player.m_swimDepth = DefaultSwimDepth;
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

    internal bool ShouldForceDive()
    {
        return ShouldForceSwimming() && !Player.IsOnGround();
    }

    internal bool IsUnderSurface()
    {
        return Player.m_swimDepth > DefaultSwimDepth;
    }

    internal bool IsDiving()
    {
        return Player.m_swimDepth > DivingSwimDepth;
    }

    internal bool IsSurfacing()
    {
        return !IsDiving() && IsUnderSurface();
    }

    internal bool IsIdleInWater()
    {
        return Player.InWater()
               && (Player.IsSwimming() || ShouldForceSwimming())
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
        float waterRegenRate = ServerSyncModTemplatePlugin._waterStaminaRegenRateMultiplier.Value;
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

    internal void ApplyDepthScaledSwimDrain(float dt)
    {
        float drainMultiplier = GetDepthScaledSwimDrainMultiplier();
        if (drainMultiplier <= 1f)
        {
            return;
        }

        float skillFactor = Player.m_skills.GetSkillFactor(Skills.SkillType.Swim);
        float staminaDrain = Mathf.Lerp(Player.m_swimStaminaDrainMinSkill, Player.m_swimStaminaDrainMaxSkill, skillFactor);
        staminaDrain += staminaDrain * Player.GetEquipmentSwimStaminaModifier();
        Player.m_seman.ModifySwimStaminaUsage(staminaDrain, ref staminaDrain);

        float extraDrainMultiplier = drainMultiplier - 1f;
        if (extraDrainMultiplier <= 0f)
        {
            return;
        }

        Player.UseStamina(dt * staminaDrain * Game.m_moveStaminaRate * extraDrainMultiplier);
    }

    internal void UpdateSwimSpeed()
    {
        float speedMultiplier = 1f;
        if (ZInput.GetButton("Run") || ZInput.GetButton("JoyRun"))
        {
            speedMultiplier = Mathf.Max(1f, ServerSyncModTemplatePlugin._playerSwimRunSpeedMultiplier.Value);
        }

        Player.m_swimSpeed = _baseSwimSpeed * speedMultiplier;
    }

    internal void Dive(float dt, bool ascend, out Vector3? defaultMoveDir)
    {
        defaultMoveDir = Player.m_moveDir;
        Player.m_moveDir = GetDiveDirection(ascend);
        Vector3 diveVelocity = CalculateSwimVelocity();
        float newDepth = Player.m_swimDepth - (diveVelocity.y * dt);
        Player.m_swimDepth = Mathf.Max(newDepth, DefaultSwimDepth);
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

    private float GetDepthScaledSwimDrainMultiplier()
    {
        float maxMultiplier = Mathf.Max(1f, ServerSyncModTemplatePlugin._waterDepthStaminaDrainMaxMultiplier.Value);
        if (maxMultiplier <= 1f)
        {
            return 1f;
        }

        float fullDepth = Mathf.Max(0.25f, ServerSyncModTemplatePlugin._waterDepthStaminaDrainFull.Value);
        float startDepth = Mathf.Clamp(ServerSyncModTemplatePlugin._waterDepthStaminaDrainStart.Value, 0f, fullDepth);
        if (Player.m_swimDepth <= startDepth)
        {
            return 1f;
        }

        float t = Mathf.InverseLerp(startDepth, fullDepth, Player.m_swimDepth);
        return Mathf.SmoothStep(1f, maxMultiplier, t);
    }
}
