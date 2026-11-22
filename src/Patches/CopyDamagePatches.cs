using System;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// ED-2: on a client, an enemy's hit on the local player counts once — the host's.
    ///
    /// <para>A client's copy of a host enemy plays the host's attacks (melee swings, shots, the Barrel Boy's landing),
    /// and they run the game's own hit code on the client: the copy's arrow, swing, shockwave or explosion hits the
    /// client's own player. The host's enemy makes the same attack beside the host's stand-in for that client, the
    /// stand-in is hit, and <c>Unit_ReceiveDamage_Pre</c> forwards that hit to the client as a <c>HostDamageRequest</c>.
    /// Both used to land. The game's player hit window (0.2 s, the largest hit counts) hid the second when the two
    /// arrived close together and charged it whenever they did not (ED-1 found the Barrel Boy's landing; an archer's
    /// arrow and the landing's explosion do the same).</para>
    ///
    /// <para>The host's enemy is the authority (invariant 1), so a copy's damage on the local player is dropped. Only
    /// where both conditions hold:
    /// <list type="bullet">
    /// <item>the damage comes from one of the hit mechanisms below, each of which the host's enemy also runs against the
    /// stand-in, so the forwarded hit exists; and</item>
    /// <item>its <c>DamageSourceData.sourceUnit</c> is a copy the host drives (<c>IsClientEnemyPuppetNpc</c>,
    /// <c>IsHostBoundEntity</c> or <c>IsRuntimeMirroredEntity</c>).</item>
    /// </list>
    /// The mechanisms: <c>Npc.HandleMeleeHit</c> (a swing), <c>ProjectileUtilities.ProcessUnitHit</c> (a projectile),
    /// <c>ExplosionManager.Update</c> (every queued explosion), <c>ShockwaveEffectPart.OnTriggerEnter</c> and
    /// <c>DamageTrigger.ApplyDamage(Unit)</c> (the two shockwave prefabs). Only the hit's own <c>ReceiveDamage</c> is
    /// dropped: the status it applies (<c>ApplyHitModifiers</c>, an explosion's status) still lands, and so does every
    /// damage-over-time tick of that status (<c>states.isPeriodicModifier</c>), including the one the game fires on the
    /// spot inside the hit (an <c>applyOnce</c> modifier, a player's Electrocuted rising). Those ticks are the client's
    /// own: the host absorbs them on the stand-in instead of forwarding them (Issue #2).</para>
    ///
    /// <para>Deliberately not covered: damage whose host side does not reach the stand-in, so the client's copy is the
    /// only source — a boss's <c>DonutCollision</c> ring (GU102-8 makes the host's ring skip the stand-in), and every
    /// other mechanism not listed above. A unit no host enemy drives runs its own AI and keeps its damage. Damage the
    /// host forwards is applied under <c>HostDamageApplyDepth</c> and never dropped. A hit whose owner is already gone
    /// (an explosion queued by a copy destroyed before <c>ExplosionManager.Update</c> runs) has no source unit and is
    /// not dropped.</para>
    /// </summary>
    // Executes on: Client (the scopes are counted on every end; only a client drops anything)
    internal static class CopyDamagePatches
    {
        // Diagnostics only (read with an inspector); never consulted by the gate.
        public static int DroppedMelee, DroppedProjectile, DroppedExplosion, DroppedShockwave;

        // The hit mechanism running right now, or null. Main thread only; a nested scope restores the outer one.
        private static string? _scope;

        private const string Melee = "melee", Projectile = "projectile", Explosion = "explosion", Shockwave = "shockwave";

        public static void Apply(Harmony harmony)
        {
            int patched = 0, wanted = 0;
            void Scope(MethodInfo? method, string prefix, string label)
            {
                wanted++;
                if (method == null) { Plugin.Log.Warn($"[CopyDamage] {label} not found — a copy's {label} hit on the client is not dropped"); return; }
                try
                {
                    harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(CopyDamagePatches), prefix),
                        finalizer: new HarmonyMethod(typeof(CopyDamagePatches), nameof(ExitScope)));
                    patched++;
                }
                catch (Exception ex) { Plugin.Log.Warn($"[CopyDamage] patching {label} failed: {ex.GetType().Name}: {ex.Message}"); }
            }

            Scope(AccessTools.DeclaredMethod(typeof(Npc), "HandleMeleeHit", Type.EmptyTypes), nameof(EnterMelee), "Npc.HandleMeleeHit");
            Scope(AccessTools.DeclaredMethod(typeof(ProjectileUtilities), nameof(ProjectileUtilities.ProcessUnitHit)), nameof(EnterProjectile), "ProjectileUtilities.ProcessUnitHit");
            Scope(AccessTools.DeclaredMethod(typeof(ExplosionManager), "Update", Type.EmptyTypes), nameof(EnterExplosion), "ExplosionManager.Update");
            Scope(AccessTools.DeclaredMethod(typeof(ShockwaveEffectPart), "OnTriggerEnter", new[] { typeof(UnityEngine.Collider) }), nameof(EnterShockwave), "ShockwaveEffectPart.OnTriggerEnter");
            Scope(AccessTools.DeclaredMethod(typeof(DamageTrigger), nameof(DamageTrigger.ApplyDamage), new[] { typeof(Unit) }), nameof(EnterShockwave), "DamageTrigger.ApplyDamage");

            if (patched == wanted)
                Plugin.Log.Info($"[CopyDamage] Patched {patched} enemy hit mechanisms (a copy's hit on the local player defers to the host's).");
        }

        private static void EnterMelee(out string? __state) { __state = _scope; _scope = Melee; }
        private static void EnterProjectile(out string? __state) { __state = _scope; _scope = Projectile; }
        private static void EnterExplosion(out string? __state) { __state = _scope; _scope = Explosion; }
        private static void EnterShockwave(out string? __state) { __state = _scope; _scope = Shockwave; }

        private static Exception? ExitScope(Exception? __exception, string? __state)
        {
            _scope = __state;
            return __exception;
        }

        /// <summary>
        /// CLIENT, from <c>Unit_ReceiveDamage_Pre</c>: true when this hit is a copy's, inside one of the mechanisms the
        /// host also runs against the stand-in, landing on the local player — the caller then skips it.
        /// </summary>
        public static bool ShouldDropCopyHit(object victim, float damage, object? rawSource)
        {
            string? scope = _scope;
            if (scope == null) return false;
            try
            {
                if (NetPlayerLifeManager.HostDamageApplyDepth > 0) return false;
                if (NetConfig.GetMode() != NetMode.Client || !NetGameplaySyncBridge.IsSessionActive) return false;
                if (!(rawSource is DamageSourceData source)) return false;
                if (source.states.isPeriodicModifier) return false; // a status tick: client-local, never forwarded
                Unit owner = source.sourceUnit;
                if (owner == null) return false;
                if (!NetPlayerLifeManager.IsLocalPlayerUnit(victim)) return false;
                if (!NetGameplayProbeManager.IsClientEnemyPuppetNpc(owner)
                    && !NetGameplayProbeManager.IsHostBoundEntity(owner)
                    && !NetGameplayProbeManager.IsRuntimeMirroredEntity(owner)) return false;

                switch (scope)
                {
                    case Melee: DroppedMelee++; break;
                    case Projectile: DroppedProjectile++; break;
                    case Explosion: DroppedExplosion++; break;
                    default: DroppedShockwave++; break;
                }
                if (Plugin.Cfg.LogEnemyHostDamageAuthority.Value)
                    Plugin.Log.Info($"[EnemyDamageAuthority] dropped a copy's {scope} hit on the local player dmg={damage:0.##} type={source.damageType} owner={owner.name} source={source.name}");
                return true;
            }
            catch { return false; }
        }
    }
}
