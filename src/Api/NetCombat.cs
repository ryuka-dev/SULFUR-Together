using System;
using System.Collections.Generic;
using UnityEngine;

namespace SULFURTogether.Api
{
    /// <summary>How the host settled one of the local client's forwarded hits.</summary>
    public enum HitOutcome : byte
    {
        /// <summary>The game's damage function took health off the target.</summary>
        Applied = 1,
        /// <summary>The host refused the request before it reached the game (scene, target, invalid or out-of-range input).</summary>
        Rejected = 2,
        /// <summary>The target was alive and the game declined the damage (invulnerable unit or hitbox, block, parry,
        /// petrified vs ranged, protected NPC, fully mitigated).</summary>
        Invulnerable = 3,
        /// <summary>The target was already dead on the host.</summary>
        TargetDead = 4,
        /// <summary>No result arrived within <see cref="NetCombat.ResultTimeoutSeconds"/>, the host answered a later
        /// hit first (this one was never settled), or the session ended. A result that arrives later is dropped.</summary>
        Lost = 5,
    }

    /// <summary>A hit the local client just forwarded to the host. Raised synchronously, on the main thread, inside the
    /// game's damage call the client suppressed.</summary>
    public sealed class LocalHitForwardedArgs
    {
        internal LocalHitForwardedArgs(int requestSeq, object? target, object? weapon, bool melee, int damageType,
            int hitPart, ushort hitShapeId, Vector3? hitPosition, float requestedDamage)
        {
            RequestSeq = requestSeq; Target = target; Weapon = weapon; Melee = melee; DamageType = damageType;
            HitPart = hitPart; HitShapeId = hitShapeId; HitPosition = hitPosition; RequestedDamage = requestedDamage;
        }

        /// <summary>Session-unique, increasing. The matching <see cref="NetCombat.LocalHitResolved"/> carries the same value.</summary>
        public int RequestSeq { get; }
        /// <summary>The local unit that was hit (a game <c>Unit</c>).</summary>
        public object? Target { get; }
        /// <summary>The local weapon that dealt the hit (a game <c>Weapon</c>), or null when the damage had none (e.g. an explosion).</summary>
        public object? Weapon { get; }
        public bool Melee { get; }
        /// <summary>The game's <c>DamageTypes</c> value.</summary>
        public int DamageType { get; }
        /// <summary>The game's <c>HitboxColliders.Parts</c> value; 0 (None) when the damage carried no hitbox.</summary>
        public int HitPart { get; }
        /// <summary>The full <c>HitboxColliders.ShapeId.partAndIndex</c> (part in the low byte).</summary>
        public ushort HitShapeId { get; }
        /// <summary>The local world position of the hit, when the game supplied one.</summary>
        public Vector3? HitPosition { get; }
        /// <summary>The damage argument of the suppressed call — before crit, hitbox multiplier and resistances.</summary>
        public float RequestedDamage { get; }
    }

    /// <summary>A further damage instance the host's game dealt to the same target while settling the hit — e.g. the
    /// Crusader status's Holy damage, which the game deals from inside the hit.</summary>
    public readonly struct SecondaryHit
    {
        internal SecondaryHit(float damage, float healthRemoved, int damageType, bool isCritical, bool killed)
        {
            Damage = damage; HealthRemoved = healthRemoved; DamageType = damageType; IsCritical = isCritical; Killed = killed;
        }
        public float Damage { get; }
        public float HealthRemoved { get; }
        public int DamageType { get; }
        public bool IsCritical { get; }
        /// <summary>The target's death happened during this instance. On a killing hit with Crusader the hit's own
        /// damage can take the last health and the game's death check runs inside the Holy instance, so this can be
        /// true with <see cref="HealthRemoved"/> 0 — <see cref="LocalHitResolvedArgs.Killed"/> is the per-hit answer.</summary>
        public bool Killed { get; }
    }

    /// <summary>The host's settlement of one forwarded hit. Exactly one per <see cref="LocalHitForwardedArgs.RequestSeq"/>.</summary>
    public sealed class LocalHitResolvedArgs
    {
        internal LocalHitResolvedArgs(int requestSeq, object? target, HitOutcome outcome, float damage, float healthRemoved,
            float incomingDamage, int damageType, bool isCritical, int hitPart, float hitMultiplier, object? weapon, bool melee,
            Vector3? hitPosition, bool wasAlive, bool killed, IReadOnlyList<SecondaryHit> secondary)
        {
            RequestSeq = requestSeq; Target = target; Outcome = outcome; Damage = damage; HealthRemoved = healthRemoved;
            IncomingDamage = incomingDamage; DamageType = damageType; IsCritical = isCritical; HitPart = hitPart;
            HitMultiplier = hitMultiplier; Weapon = weapon; Melee = melee; HitPosition = hitPosition; WasAlive = wasAlive;
            Killed = killed; Secondary = secondary;
        }

        public int RequestSeq { get; }
        /// <summary>The local unit that was hit, or null when it no longer exists locally.</summary>
        public object? Target { get; }
        public HitOutcome Outcome { get; }
        /// <summary>The final damage the host's game logged for this hit (<c>Unit.CreateDamageLog</c>): after crit, hitbox
        /// multiplier and mitigation. 0 unless <see cref="Outcome"/> is Applied or the game logged a fully mitigated hit.</summary>
        public float Damage { get; }
        /// <summary>The health this hit itself took off the host's unit (excludes <see cref="Secondary"/>).</summary>
        public float HealthRemoved { get; }
        /// <summary>The pre-mitigation figure of <see cref="Damage"/> (<c>CreateDamageLog</c>'s incomingDamage).</summary>
        public float IncomingDamage { get; }
        public int DamageType { get; }
        public bool IsCritical { get; }
        /// <summary>From the local request, not the network.</summary>
        public int HitPart { get; }
        /// <summary><c>Hitmesh.Data.GetShapeMultiplier()</c> of the part hit; 0 when the hit had no part.</summary>
        public float HitMultiplier { get; }
        /// <summary>From the local request, not the network.</summary>
        public object? Weapon { get; }
        public bool Melee { get; }
        /// <summary>From the local request, not the network.</summary>
        public Vector3? HitPosition { get; }
        public bool WasAlive { get; }
        /// <summary>Settling this hit (its own damage or a secondary instance) killed the target.</summary>
        public bool Killed { get; }
        /// <summary>Further damage the game dealt to the same target inside this hit (e.g. Crusader's Holy damage), in
        /// the order dealt. Read-only and empty when there was none.</summary>
        public IReadOnlyList<SecondaryHit> Secondary { get; }
    }

    /// <summary>A kill the local player is credited with — the killing blow or an assist.</summary>
    public sealed class LocalKillCreditedArgs
    {
        internal LocalKillCreditedArgs(object? target, bool killingBlow, int killingRequestSeq, object? weapon, bool isHeadshot, int experience, float damageDealt)
        {
            Target = target; KillingBlow = killingBlow; KillingRequestSeq = killingRequestSeq; Weapon = weapon;
            IsHeadshot = isHeadshot; Experience = experience; DamageDealt = damageDealt;
        }

        /// <summary>The local unit that died (a game <c>Unit</c>), or null when it no longer exists locally.</summary>
        public object? Target { get; }
        /// <summary>True when one of the local client's hits killed it; false for an assist.</summary>
        public bool KillingBlow { get; }
        /// <summary>The RequestSeq of the killing hit, or -1 for an assist.</summary>
        public int KillingRequestSeq { get; }
        /// <summary>The weapon of the killing hit, or for an assist of the local client's last hit on the target.</summary>
        public object? Weapon { get; }
        /// <summary>The killing hit landed on the head or an eye.</summary>
        public bool IsHeadshot { get; }
        /// <summary>The base experience SULFUR Together passed to the game's <c>DistributeExperience</c> for this kill
        /// (before the local player's bonus-XP multiplier); 0 when none was distributed (e.g. a boss).</summary>
        public int Experience { get; }
        /// <summary>The health the local player's resolved hits took off this target (their own damage and secondary
        /// damage). The credit follows the XP rule, which counts any forwarded hit — an assist whose every hit came back
        /// TargetDead or Invulnerable reports 0 here; a feedback mod can require a minimum.</summary>
        public float DamageDealt { get; }
    }

    /// <summary>
    /// Combat events for a co-op <b>client</b>: its hits on host-owned enemies are not settled locally — the host runs the
    /// game's damage function on its own unit and sends the result back. These events give a companion mod (damage
    /// numbers, hit sounds, kill feedback) what the host decided, per hit.
    ///
    /// <para><b>Guarantees.</b> Raised on the main thread. Per hit: <see cref="LocalHitForwarded"/> once, then
    /// <see cref="LocalHitResolved"/> exactly once, in RequestSeq order. A hit with no result within
    /// <see cref="ResultTimeoutSeconds"/> (measured past the network poll, so a client stall does not lose results that
    /// already arrived) resolves as <see cref="HitOutcome.Lost"/>; a later result is dropped. A hit the client swallows
    /// without sending (an already dead target, a not-yet-bound unit) raises nothing.
    /// <see cref="LocalKillCredited"/> fires at most once per enemy, after the local death and after the resolution of
    /// every hit on it — so after the killing hit's; never from inside a session teardown. For bosses it fires from the
    /// killing hit, for the boss unit itself only (not a phase illusion or a worm section).</para>
    ///
    /// <para><b>Corpse hits.</b> A hit on a host-owned body (shattering a frozen corpse, blowing one up) is forwarded
    /// too and resolves <see cref="HitOutcome.TargetDead"/> with Damage 0; whether the body burst arrives through the
    /// game's own gib effect. Such hits never delay a kill credit.</para>
    ///
    /// <para>Nothing is raised offline or on the host — there the game's own damage call runs and is the event.</para>
    ///
    /// <para>A subscriber that throws is logged and does not stop the others or SULFUR Together.</para>
    /// </summary>
    public static class NetCombat
    {
        /// <summary>Read at run time (a property, not a constant), so a companion mod sees the installed version.</summary>
        public static int ApiVersion => 1;

        /// <summary>How long a forwarded hit waits for the host's result before it resolves as Lost.</summary>
        public static float ResultTimeoutSeconds => 2f;

        /// <summary>Client only. See <see cref="LocalHitForwardedArgs"/>.</summary>
        public static event Action<LocalHitForwardedArgs>? LocalHitForwarded;

        /// <summary>Client only. See <see cref="LocalHitResolvedArgs"/>.</summary>
        public static event Action<LocalHitResolvedArgs>? LocalHitResolved;

        /// <summary>Client only. See <see cref="LocalKillCreditedArgs"/>.</summary>
        public static event Action<LocalKillCreditedArgs>? LocalKillCredited;

        /// <summary>Host: true while the host settles a remote peer's hit through the game's damage function — including
        /// damage the game deals from inside it (Crusader, Voodoo, thorns). A damage-feedback mod on the host uses it to
        /// tell such damage from the host player's own. Always false on a client and offline.</summary>
        public static bool IsResolvingRemoteHit => SULFURTogether.Networking.Gameplay.RemoteHitResolver.IsResolving;

        /// <summary>Host: the session peer id whose hit is being settled while <see cref="IsResolvingRemoteHit"/>, else null.</summary>
        public static string? RemoteHitPeerId => SULFURTogether.Networking.Gameplay.RemoteHitResolver.RemotePeerId;

        internal static void RaiseForwarded(LocalHitForwardedArgs a) => Raise(LocalHitForwarded, a, nameof(LocalHitForwarded));
        internal static void RaiseResolved(LocalHitResolvedArgs a) => Raise(LocalHitResolved, a, nameof(LocalHitResolved));
        internal static void RaiseKillCredited(LocalKillCreditedArgs a) => Raise(LocalKillCredited, a, nameof(LocalKillCredited));

        private static void Raise<T>(Action<T>? handlers, T args, string name)
        {
            if (handlers == null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<T>)d)(args); }
                catch (Exception ex)
                {
                    Plugin.Log.Warn($"[NetCombat] a {name} subscriber ({d.Method.DeclaringType?.FullName}.{d.Method.Name}) threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
