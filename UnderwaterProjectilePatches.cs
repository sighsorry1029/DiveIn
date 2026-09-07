using System;
using HarmonyLib;
using UnityEngine;

namespace ServerSyncModTemplate;

[HarmonyPatch]
internal static class UnderwaterProjectilePatches
{
    [ThreadStatic]
    private static int _playerProjectileSpawnOnHitDepth;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.SpawnOnHit), new[] { typeof(GameObject), typeof(Collider), typeof(Vector3) })]
    private static void ProjectileSpawnOnHitPrefix(Projectile __instance, out bool __state)
    {
        __state = __instance != null && __instance.m_owner is Player;
        if (__state)
        {
            _playerProjectileSpawnOnHitDepth++;
        }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.SpawnOnHit), new[] { typeof(GameObject), typeof(Collider), typeof(Vector3) })]
    private static void ProjectileSpawnOnHitFinalizer(ref bool __state)
    {
        if (!__state)
        {
            return;
        }

        __state = false;
        if (_playerProjectileSpawnOnHitDepth > 0)
        {
            _playerProjectileSpawnOnHitDepth--;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
    private static void ProjectileSetupPostfix(Projectile __instance, Character owner)
    {
        if (!ShouldApplyUnderwaterPenalty(__instance, owner))
        {
            return;
        }

        ApplyUnderwaterPenalty(__instance);
    }

    private static bool ShouldApplyUnderwaterPenalty(Projectile projectile, Character? owner)
    {
        // SpawnOnHit passes launch data to descendants; only the original launch receives the penalty.
        return projectile != null
               && _playerProjectileSpawnOnHitDepth == 0
               && ServerSyncModTemplatePlugin.HasPlayerProjectileUnderwaterPenalty()
               && IsLocallyOwnedProjectile(projectile)
               && IsPlayerOwnedProjectile(projectile, owner)
               && IsUnderwater(projectile.transform.position);
    }

    private static bool IsLocallyOwnedProjectile(Projectile projectile)
    {
        return projectile.m_nview == null
               || !projectile.m_nview.IsValid()
               || projectile.m_nview.IsOwner();
    }

    private static bool IsPlayerOwnedProjectile(Projectile projectile, Character? owner)
    {
        return owner is Player || projectile.m_owner is Player;
    }

    private static bool IsUnderwater(Vector3 position)
    {
        float waterLevel = Floating.GetLiquidLevel(position, 1f, LiquidType.Water);
        return waterLevel > -10000f && position.y < waterLevel;
    }

    private static void ApplyUnderwaterPenalty(Projectile projectile)
    {
        float ttlMultiplier = ServerSyncModTemplatePlugin.GetPlayerProjectileUnderwaterTtlMultiplier();
        if (!Mathf.Approximately(ttlMultiplier, 1f) && projectile.m_ttl > 0f)
        {
            projectile.m_ttl *= ttlMultiplier;
        }

        float speedMultiplier = ServerSyncModTemplatePlugin.GetPlayerProjectileUnderwaterSpeedMultiplier();
        if (!Mathf.Approximately(speedMultiplier, 1f))
        {
            projectile.m_vel *= speedMultiplier;
        }

        float damageMultiplier = ServerSyncModTemplatePlugin.GetPlayerProjectileUnderwaterDamageMultiplier();
        if (Mathf.Approximately(damageMultiplier, 1f))
        {
            return;
        }

        projectile.m_damage.Modify(damageMultiplier);
        if (projectile.m_originalHitData != null)
        {
            projectile.m_originalHitData = projectile.m_originalHitData.Clone();
            projectile.m_originalHitData.m_damage.Modify(damageMultiplier);
        }
    }
}
