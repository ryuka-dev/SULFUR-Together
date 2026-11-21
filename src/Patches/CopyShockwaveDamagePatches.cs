using System;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;
using UnityEngine;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// ED-1: on a client, a shockwave spawned by the local copy of a host enemy does not damage the local player.
    ///
    /// <para>A client's copy of a host enemy plays the host's attack animations, and their animation events spawn the
    /// attack's effect on the copy through <c>UnitSpawnEffect.SpawnEffectPrefab</c> — the Goblin Barrel Boy's landing
    /// is the reported one. The parts of that shockwave belong to the unit that spawned it, so on the client they
    /// belonged to the copy and hit the client's own player. The host's enemy spawns the same shockwave next to the
    /// host's stand-in for that client, its parts hit the stand-in, and <c>Unit_ReceiveDamage_Pre</c> forwards that to
    /// the client as a <c>HostDamageRequest</c>. One landing therefore charged a client twice whenever the two arrived
    /// further apart than the game's 0.2 s player hit window: the copy lands when the host's attack reaches the client,
    /// the forwarded hit arrives a network delay after the host's landing.</para>
    ///
    /// <para>The host's forward is the authority (the enemy is the host's), so the copy's parts do not damage the local
    /// player. <c>UnitSpawnEffect</c> spawns one of two prefabs, so both of the game's shockwave kinds are covered, each
    /// at its own hit: <c>ShockwaveEffect</c> (the low-quality prefab, chosen at the lowest effect quality and in
    /// Endless) damages from <c>ShockwaveEffectPart.OnTriggerEnter</c>; <c>ShockwaveSpawnedEffect</c> (the normal one)
    /// through its <c>DamageTrigger</c>s, <c>DamageTrigger.ApplyDamage(Unit)</c>. For a player either method does
    /// nothing but the damage (neither pushes a player), so skipping it for the local player drops exactly that. The
    /// effect still plays on the copy, and its push on props and other units is untouched. A local unit that no host
    /// enemy drives runs its own AI and keeps its damage.</para>
    ///
    /// <para>Not covered: the copy's own explosion (<c>ShockwaveSpawnedEffect.shockwaveExplosion</c>) still damages the
    /// local player. The host's explosion does not reach the stand-in, so the copy's is the client's only source of
    /// it.</para>
    /// </summary>
    // Executes on: Client
    internal static class CopyShockwaveDamagePatches
    {
        // Diagnostics only (read with an inspector); never consulted by the guard.
        public static int DroppedHits;

        public static void Apply(Harmony harmony)
        {
            bool trigger = Patch(harmony, AccessTools.DeclaredMethod(typeof(DamageTrigger), nameof(DamageTrigger.ApplyDamage), new[] { typeof(Unit) }), nameof(DamageTrigger_ApplyDamage_Pre));
            bool part = Patch(harmony, AccessTools.DeclaredMethod(typeof(ShockwaveEffectPart), "OnTriggerEnter", new[] { typeof(Collider) }), nameof(ShockwaveEffectPart_OnTriggerEnter_Pre));
            if (trigger && part)
                Plugin.Log.Info("[CopyShockwave] Patched DamageTrigger.ApplyDamage / ShockwaveEffectPart.OnTriggerEnter (a copy's shockwave does not damage the local player).");
            else
                Plugin.Log.Warn($"[CopyShockwave] a shockwave hit method is missing (DamageTrigger={trigger}, ShockwaveEffectPart={part}) — a client may take an enemy's shockwave from its own copy as well as from the host");
        }

        private static bool Patch(Harmony harmony, System.Reflection.MethodInfo? method, string prefix)
        {
            if (method == null) return false;
            try
            {
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(CopyShockwaveDamagePatches), prefix));
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[CopyShockwave] patching {method.DeclaringType?.Name}.{method.Name} failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>CLIENT: is this a part of a copy's shockwave about to damage the local player?</summary>
        private static bool IsCopyHitOnLocalPlayer(Unit? owner, Unit? victim)
        {
            if (owner == null || victim == null) return false;
            if (NetConfig.GetMode() != NetMode.Client || !NetGameplaySyncBridge.IsSessionActive) return false;
            if (!NetPlayerLifeManager.IsLocalPlayerUnit(victim)) return false;
            return NetGameplayProbeManager.IsHostBoundEntity(owner) || NetGameplayProbeManager.IsRuntimeMirroredEntity(owner);
        }

        private static bool Drop(Component part, float damage, Unit owner)
        {
            DroppedHits++;
            if (Plugin.Cfg.LogEnemyHostDamageAuthority.Value)
                Plugin.Log.Info($"[EnemyDamageAuthority] dropped a copy's shockwave hit on the local player part={part.name} dmg={damage} owner={owner.name}");
            return false;
        }

        private static bool DamageTrigger_ApplyDamage_Pre(DamageTrigger __instance, Unit unit)
        {
            try
            {
                if (__instance == null || !IsCopyHitOnLocalPlayer(__instance.owner, unit)) return true;
                return Drop(__instance, __instance.damage, __instance.owner);
            }
            catch { return true; }
        }

        private static bool ShockwaveEffectPart_OnTriggerEnter_Pre(ShockwaveEffectPart __instance, Collider other)
        {
            try
            {
                if (__instance == null || other == null) return true;
                if (!other.TryGetComponent<Hitmesh>(out var hitmesh) || hitmesh == null) return true;
                if (!IsCopyHitOnLocalPlayer(__instance.owner, hitmesh.owner)) return true;
                return Drop(__instance, __instance.damage, __instance.owner);
            }
            catch { return true; }
        }
    }
}
