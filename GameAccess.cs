using System;
using HarmonyLib;
using UnityEngine;

namespace ServerSyncModTemplate;

// Valheim 1.0.7 non-public access only. Public APIs stay at call sites.
// Cache typed delegates once: no reflection lookup, boxing or object[] allocation per frame.
internal static class GameAccess
{
    internal delegate bool GroundHeightDelegate(Character character, Vector3 position, out float height, out Vector3 normal);
    internal delegate void PushbackDelegate(Character character, ref Vector3 velocity);

    internal static readonly AccessTools.FieldRef<Character, Rigidbody> Body =
        AccessTools.FieldRefAccess<Character, Rigidbody>("m_body");
    internal static readonly AccessTools.FieldRef<Character, ZNetView> CharacterView =
        AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");
    internal static readonly AccessTools.FieldRef<Character, float> LastGroundTouch =
        AccessTools.FieldRefAccess<Character, float>("m_lastGroundTouch");
    internal static readonly AccessTools.FieldRef<Character, float> SwimTimer =
        AccessTools.FieldRefAccess<Character, float>("m_swimTimer");
    internal static readonly AccessTools.FieldRef<Character, bool> WallRunning =
        AccessTools.FieldRefAccess<Character, bool>("m_wallRunning");
    internal static readonly AccessTools.FieldRef<Character, Vector3> CurrentVelocity =
        AccessTools.FieldRefAccess<Character, Vector3>("m_currentVel");
    internal static readonly AccessTools.FieldRef<Player, float> Stamina =
        AccessTools.FieldRefAccess<Player, float>("m_stamina");
    internal static readonly AccessTools.FieldRef<Player, float> StaminaRegenTimer =
        AccessTools.FieldRefAccess<Player, float>("m_staminaRegenTimer");
    internal static readonly AccessTools.FieldRef<Player, float> Eitr =
        AccessTools.FieldRefAccess<Player, float>("m_eitr");
    internal static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> HiddenRightItem =
        AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_hiddenRightItem");
    internal static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> HiddenLeftItem =
        AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_hiddenLeftItem");
    internal static readonly AccessTools.FieldRef<BaseAI, Character> AICharacter =
        AccessTools.FieldRefAccess<BaseAI, Character>("m_character");
    internal static readonly AccessTools.FieldRef<BaseAI, ZNetView> AIView =
        AccessTools.FieldRefAccess<BaseAI, ZNetView>("m_nview");
    internal static readonly AccessTools.FieldRef<BaseAI, Tameable> AITameable =
        AccessTools.FieldRefAccess<BaseAI, Tameable>("m_tamable");
    internal static readonly AccessTools.FieldRef<MonsterAI, Character?> TargetCreature =
        AccessTools.FieldRefAccess<MonsterAI, Character?>("m_targetCreature");
    internal static readonly AccessTools.FieldRef<MonsterAI, StaticTarget?> TargetStatic =
        AccessTools.FieldRefAccess<MonsterAI, StaticTarget?>("m_targetStatic");
    internal static readonly AccessTools.FieldRef<MonsterAI, Vector3> LastTargetPosition =
        AccessTools.FieldRefAccess<MonsterAI, Vector3>("m_lastKnownTargetPos");
    internal static readonly AccessTools.FieldRef<MonsterAI, float> TargetTimer =
        AccessTools.FieldRefAccess<MonsterAI, float>("m_updateTargetTimer");
    internal static readonly AccessTools.FieldRef<Projectile, Character> ProjectileOwner =
        AccessTools.FieldRefAccess<Projectile, Character>("m_owner");
    internal static readonly AccessTools.FieldRef<Projectile, ZNetView> ProjectileView =
        AccessTools.FieldRefAccess<Projectile, ZNetView>("m_nview");
    internal static readonly AccessTools.FieldRef<Projectile, Vector3> ProjectileVelocity =
        AccessTools.FieldRefAccess<Projectile, Vector3>("m_vel");
    internal static readonly AccessTools.FieldRef<Projectile, HitData> OriginalHitData =
        AccessTools.FieldRefAccess<Projectile, HitData>("m_originalHitData");
    internal static readonly AccessTools.FieldRef<GameCamera, Camera> Camera =
        AccessTools.FieldRefAccess<GameCamera, Camera>("m_camera");
    internal static readonly AccessTools.FieldRef<GameCamera, bool> WaterClipping =
        AccessTools.FieldRefAccess<GameCamera, bool>("m_waterClipping");
    internal static readonly AccessTools.FieldRef<KeyHints, bool> KeyHintsEnabled =
        AccessTools.FieldRefAccess<KeyHints, bool>("m_keyHintsEnabled");
    internal static readonly AccessTools.FieldRef<WaterVolume, float[]> WaterDepth =
        AccessTools.FieldRefAccess<WaterVolume, float[]>("m_normalizedDepth");

    internal static readonly Func<Character, float> LiquidDepth =
        AccessTools.MethodDelegate<Func<Character, float>>(AccessTools.DeclaredMethod(typeof(Character), "InLiquidDepth", Type.EmptyTypes));
    internal static readonly GroundHeightDelegate GroundHeight =
        AccessTools.MethodDelegate<GroundHeightDelegate>(AccessTools.DeclaredMethod(typeof(Character), "GetGroundHeight", new[] { typeof(Vector3), typeof(float).MakeByRefType(), typeof(Vector3).MakeByRefType() }));
    internal static readonly Action<Humanoid, bool, bool> ShowHandItems =
        AccessTools.MethodDelegate<Action<Humanoid, bool, bool>>(AccessTools.DeclaredMethod(typeof(Humanoid), "ShowHandItems", new[] { typeof(bool), typeof(bool) }));
    internal static readonly Func<Player, bool> TakeInput =
        AccessTools.MethodDelegate<Func<Player, bool>>(AccessTools.DeclaredMethod(typeof(Player), "TakeInput", Type.EmptyTypes));
    internal static readonly Func<Player, bool> AlwaysRotateCamera =
        AccessTools.MethodDelegate<Func<Player, bool>>(AccessTools.DeclaredMethod(typeof(Player), "AlwaysRotateCamera", Type.EmptyTypes));
    internal static readonly Func<Humanoid, float> AttackSpeedMovement =
        AccessTools.MethodDelegate<Func<Humanoid, float>>(AccessTools.DeclaredMethod(typeof(Humanoid), "GetAttackSpeedFactorMovement", Type.EmptyTypes));
    internal static readonly Func<Humanoid, float> AttackSpeedRotation =
        AccessTools.MethodDelegate<Func<Humanoid, float>>(AccessTools.DeclaredMethod(typeof(Humanoid), "GetAttackSpeedFactorRotation", Type.EmptyTypes));
    internal static readonly PushbackDelegate AddPushbackForce =
        AccessTools.MethodDelegate<PushbackDelegate>(AccessTools.DeclaredMethod(typeof(Character), "AddPushbackForce", new[] { typeof(Vector3).MakeByRefType() }));
    internal static readonly Func<BaseAI, Vector3, float, float, bool> CanMove =
        AccessTools.MethodDelegate<Func<BaseAI, Vector3, float, float, bool>>(AccessTools.DeclaredMethod(typeof(BaseAI), "CanMove", new[] { typeof(Vector3), typeof(float), typeof(float) }));
    internal static readonly Action<BaseAI, Vector3, bool, float> MoveTowardsSwoop =
        AccessTools.MethodDelegate<Action<BaseAI, Vector3, bool, float>>(AccessTools.DeclaredMethod(typeof(BaseAI), "MoveTowardsSwoop", new[] { typeof(Vector3), typeof(bool), typeof(float) }));
    internal static readonly Func<BaseAI, float, Vector3, bool> Flee =
        AccessTools.MethodDelegate<Func<BaseAI, float, Vector3, bool>>(AccessTools.DeclaredMethod(typeof(BaseAI), "Flee", new[] { typeof(float), typeof(Vector3) }));
    internal static readonly Action<Localization, string, string> AddWord =
        AccessTools.MethodDelegate<Action<Localization, string, string>>(AccessTools.DeclaredMethod(typeof(Localization), "AddWord", new[] { typeof(string), typeof(string) }));
}
