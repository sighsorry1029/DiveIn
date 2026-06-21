using HarmonyLib;
using UnityEngine;

namespace ServerSyncModTemplate;

[HarmonyPatch]
internal static class UnderwaterProjectilePatches
{
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
        return projectile != null
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
