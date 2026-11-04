using System;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core.Weapons;
using Sonity;
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
    /// <para>WS-SND-1: <c>Shoot</c> also plays the shot sound (<c>SoundShot</c> → <c>PlayShootSoundEvent</c> /
    /// <c>PlayShootSoundEventIntensity</c>). A prefix on <c>Shoot</c> opens a recording window for the local player's
    /// gun, prefixes on the two play methods record what vanilla chose, and the postfix sends it with the barrage.</para>
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

                var pre = new HarmonyMethod(typeof(WeaponFirePatches)
                    .GetMethod(nameof(Weapon_Shoot_Pre), BindingFlags.Static | BindingFlags.NonPublic));
                var post = new HarmonyMethod(typeof(WeaponFirePatches)
                    .GetMethod(nameof(Weapon_Shoot_Post), BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(shoot, prefix: pre, postfix: post);
                Plugin.Log.Info("[WeaponFire] Patched Weapon.Shoot (player weapon bullet sync).");

                ApplyShotSound(harmony, weaponType);
                ApplyBeamLoop(harmony, weaponType);
                ApplyAnimationSounds(harmony, weaponType);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[WeaponFire] Apply failed: {ex.Message}");
            }
        }

        private static void ApplyShotSound(Harmony harmony, Type weaponType)
        {
            try
            {
                if (!RemoteShotSound.Resolve()) return;

                var play = AccessTools.Method(weaponType, "PlayShootSoundEvent",
                    new[] { typeof(SoundEvent), typeof(bool), typeof(SoundParameterVolumeDecibel) });
                var playIntensity = AccessTools.Method(weaponType, "PlayShootSoundEventIntensity",
                    new[] { typeof(SoundEvent), typeof(bool), typeof(float) });
                if (play == null || playIntensity == null)
                {
                    Plugin.Log.Warn("[WeaponFire] Weapon.PlayShootSoundEvent/PlayShootSoundEventIntensity not found — "
                                    + "remote shot sound not supported on this game build; teammates' gunfire stays silent");
                    return;
                }

                harmony.Patch(play, prefix: new HarmonyMethod(typeof(WeaponFirePatches)
                    .GetMethod(nameof(Weapon_PlayShootSoundEvent_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                harmony.Patch(playIntensity, prefix: new HarmonyMethod(typeof(WeaponFirePatches)
                    .GetMethod(nameof(Weapon_PlayShootSoundEventIntensity_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info("[WeaponFire] Patched Weapon.PlayShootSoundEvent/PlayShootSoundEventIntensity (remote shot sound).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[WeaponFire] shot sound patch failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>WS-SND-2: the beam loop has no per-shot report, only these two transitions (both guard on
        /// beamLoopPlaying, so the prefix keeps the state before the call and the postfix reports only a change).</summary>
        private static void ApplyBeamLoop(Harmony harmony, Type weaponType)
        {
            try
            {
                if (!RemoteShotSound.ResolveBeamLoop()) return;
                var start = AccessTools.Method(weaponType, "StartBeamLoopSound", Type.EmptyTypes);
                var stop = AccessTools.Method(weaponType, "StopBeamLoopSound", Type.EmptyTypes);
                if (start == null || stop == null)
                {
                    Plugin.Log.Warn("[WeaponFire] Weapon.StartBeamLoopSound/StopBeamLoopSound not found — remote beam loop sound not supported on this game build");
                    return;
                }
                var pre = new HarmonyMethod(typeof(WeaponFirePatches).GetMethod(nameof(Weapon_BeamLoop_Pre), BindingFlags.Static | BindingFlags.NonPublic));
                var post = new HarmonyMethod(typeof(WeaponFirePatches).GetMethod(nameof(Weapon_BeamLoop_Post), BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(start, prefix: pre, postfix: post);
                harmony.Patch(stop, prefix: pre, postfix: post);
                Plugin.Log.Info("[WeaponFire] Patched Weapon.StartBeamLoopSound/StopBeamLoopSound (remote beam loop sound).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[WeaponFire] beam loop patch failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>WS-SND-3: a melee swing and a throw make their sound from the weapon's animation, not from Shoot —
        /// the swing clip's event calls <c>Weapon.ShootSound(int)</c> (→ SoundShot → PlayShootSoundEvent), a throwable's
        /// and a reload's clip events call <c>Weapon.PlayMiscClip(int)</c> (→ PlaySoundEvent2DIfPlayer). Neither has a
        /// caller in code. Both become recording windows like Shoot, and PlaySoundEvent2DIfPlayer becomes an observation
        /// point; what a window recorded is sent on its own, with no bullets.</summary>
        private static void ApplyAnimationSounds(Harmony harmony, Type weaponType)
        {
            try
            {
                var shootSound = AccessTools.Method(weaponType, "ShootSound", new[] { typeof(int) });
                var miscClip = AccessTools.Method(weaponType, "PlayMiscClip", new[] { typeof(int) });
                var play2D = AccessTools.Method(weaponType, "PlaySoundEvent2DIfPlayer", new[] { typeof(SoundEvent) });
                var pre = new HarmonyMethod(typeof(WeaponFirePatches).GetMethod(nameof(Weapon_SoundWindow_Pre), BindingFlags.Static | BindingFlags.NonPublic));
                var post = new HarmonyMethod(typeof(WeaponFirePatches).GetMethod(nameof(Weapon_SoundWindow_Post), BindingFlags.Static | BindingFlags.NonPublic));
                if (shootSound != null) harmony.Patch(shootSound, prefix: pre, postfix: post);
                if (miscClip != null) harmony.Patch(miscClip, prefix: pre, postfix: post);
                if (play2D != null) harmony.Patch(play2D, prefix: new HarmonyMethod(typeof(WeaponFirePatches)
                    .GetMethod(nameof(Weapon_PlaySoundEvent2DIfPlayer_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                if (shootSound == null || miscClip == null || play2D == null)
                    Plugin.Log.Warn($"[WeaponFire] remote swing/throw sound partly unsupported on this game build — ShootSound(int)={shootSound != null} "
                                    + $"PlayMiscClip(int)={miscClip != null} PlaySoundEvent2DIfPlayer={play2D != null}");
                else
                    Plugin.Log.Info("[WeaponFire] Patched Weapon.ShootSound/PlayMiscClip/PlaySoundEvent2DIfPlayer (remote swing/throw sound).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[WeaponFire] swing/throw sound patch failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Weapon_SoundWindow_Pre(Weapon __instance)
        {
            try { RemoteShotSound.BeginWindow(__instance); } catch { }
        }

        private static void Weapon_SoundWindow_Post(Weapon __instance)
        {
            NetShotSound[] sounds;
            try { sounds = RemoteShotSound.EndWindow(__instance); }
            catch { return; }
            PlayerWeaponFireManager.CaptureLocalSounds(__instance, sounds);
        }

        private static void Weapon_PlaySoundEvent2DIfPlayer_Pre(Weapon __instance, SoundEvent soundEvent)
        {
            try { RemoteShotSound.OnPlay2D(__instance, soundEvent); } catch { }
        }

        private static void Weapon_BeamLoop_Pre(Weapon __instance, out bool __state)
        {
            __state = false;
            try { __state = RemoteShotSound.ReadBeamLoopPlaying(__instance); } catch { }
        }

        private static void Weapon_BeamLoop_Post(Weapon __instance, bool __state)
        {
            PlayerWeaponFireManager.CaptureLocalBeamLoop(__instance, __state);
        }

        private static void Weapon_Shoot_Pre(Weapon __instance)
        {
            try { RemoteShotSound.BeginWindow(__instance); } catch { }
        }

        private static void Weapon_Shoot_Post(Weapon __instance)
        {
            NetShotSound[] sounds;
            try { sounds = RemoteShotSound.EndWindow(__instance); }
            catch { sounds = Array.Empty<NetShotSound>(); }
            PlayerWeaponFireManager.CaptureLocalFire(__instance, sounds);
        }

        private static void Weapon_PlayShootSoundEvent_Pre(Weapon __instance, SoundEvent soundEvent, bool isEffectShootSound,
            SoundParameterVolumeDecibel parameterVolumeDb)
        {
            try { RemoteShotSound.OnPlay(__instance, soundEvent, isEffectShootSound, parameterVolumeDb); } catch { }
        }

        private static void Weapon_PlayShootSoundEventIntensity_Pre(Weapon __instance, SoundEvent soundEvent, bool isEffectShootSound,
            float intensityValue)
        {
            try { RemoteShotSound.OnPlayIntensity(__instance, soundEvent, isEffectShootSound, intensityValue); } catch { }
        }
    }
}
