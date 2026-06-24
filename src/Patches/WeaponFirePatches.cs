using System;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core.Weapons;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Phase 5.6-WS player weapon bullet sync — capture hook.
    /// One <c>Weapon.Shoot()</c> == one trigger pull == one barrage (the full chain Shoot → DispatchProjectile×N runs
    /// synchronously, leaving the computed projectile template in <c>equipmentManager.lastFiredProjectile</c>). The
    /// postfix snapshots that template + barrage geometry and broadcasts it so other peers replay the barrage visually.
    /// Almost all guns (machine gun / rifle / shotgun + DIY variants) inherit the base <c>Weapon.Shoot</c>; only special
    /// weapons (e.g. Termite) override it — handled in a later stage.
    /// </summary>
    internal static class WeaponFirePatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var weaponType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Weapons.Weapon");
                if (weaponType == null)
                {
                    Plugin.Log.Warn("[WeaponFire] Weapon type not found — player weapon sync disabled.");
                    return;
                }

                var shoot = AccessTools.Method(weaponType, "Shoot");
                if (shoot == null)
                {
                    Plugin.Log.Warn("[WeaponFire] Weapon.Shoot not found — player weapon sync disabled.");
                    return;
                }

                var post = new HarmonyMethod(typeof(WeaponFirePatches)
                    .GetMethod(nameof(Weapon_Shoot_Post), BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(shoot, postfix: post);
                Plugin.Log.Info("[WeaponFire] Patched Weapon.Shoot (player weapon bullet sync).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[WeaponFire] Apply failed: {ex.Message}");
            }
        }

        private static void Weapon_Shoot_Post(Weapon __instance)
        {
            PlayerWeaponFireManager.CaptureLocalFire(__instance);
        }
    }
}
