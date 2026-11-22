using System.Collections.Generic;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Items;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// ED-3 (client): a client can parry an enemy's swing or shot again.
    ///
    /// <para>The game decides a parry on the victim's end, right before the damage: <c>Npc.HandleMeleeHit</c> and the
    /// damage loop of <c>ProjectileUtilities.ProcessUnitHit</c> call <c>Unit.TryParryWithWeapon</c>, which succeeds while the
    /// held weapon has a parry charge for that attack type (<c>Weapon.CanParry</c>) and the attack comes from within its
    /// parry angle of the camera. Since ED-2 an enemy's hit on a client is the host's hit on the stand-in, forwarded — and
    /// the stand-in is not a player, so it never parries, and the forwarded damage landed even when the client had
    /// parried. The host now marks a forwarded hit with the parry type the game offered for it (the stand-in's own
    /// <c>TryParryWithWeapon</c> call, so a hit the game never lets a player parry — a projectile's explicit damage, a
    /// shockwave — stays unmarked).</para>
    ///
    /// <para>Each such attack reaches the client twice: as its copy's own hit (whose damage ED-2 drops) and as the
    /// forwarded hit. The client owns its input, so it decides the parry, once per attack, at whichever comes first:
    /// <list type="bullet">
    /// <item>the forwarded hit first — the game's test runs then, against the client's copy of the attacker
    /// (<see cref="TryParry"/>), and when the copy's hit follows it gets the same answer without a second test
    /// (<see cref="TryAnswerCopyParry"/>), so one attack never spends two charges;</item>
    /// <item>the copy's hit first — the game's own test runs on it as usual, and a parry is remembered
    /// (<see cref="RecordCopyResult"/>) as the answer for the forwarded hit that follows. An unparried copy hit is not
    /// remembered: the forwarded hit then runs the test itself.</item>
    /// </list>
    /// The two are paired by attacker (host SpawnIndex) and attack type within <see cref="PairSeconds"/>; both come from
    /// one host attack and are offset by the same network delay, so they normally arrive within a frame or two. The
    /// pairing has no id for the attack itself, so a hit only one end makes (the copy's swing lands, the host's misses
    /// the stand-in) leaves an entry the next attack of that enemy could take; the short window and remembering only the
    /// copy's parries keep that to a parry decided a moment early, never a hit taken after the player saw it parried. A
    /// parry
    /// decided from a forwarded hit plays the weapon's parry against the copy as the copy's own hit would (its push on the
    /// copy is undone by the next host snapshot).</para>
    /// </summary>
    internal static class ForwardedHitParry
    {
        public const float PairSeconds = 0.5f;

        private struct Entry
        {
            public int AttackerSpawnIndex;
            public byte Kind;
            public float At;
            public bool FromForward;
            public bool Parried;
        }

        private static readonly List<Entry> Pending = new List<Entry>(8);

        // True while this class runs the game's test for a forwarded hit, so the TryParryWithWeapon patches leave it alone.
        public static bool InForwardTest { get; private set; }

        // Diagnostics only (read with an inspector); never consulted.
        public static int ParriedLive, ParriedFromCopy, CopyAnsweredFromForward;

        /// <summary>CLIENT: should this forwarded hit be parried? True when it is (the caller then drops its damage).</summary>
        public static bool TryParry(Unit player, byte kind, int attackerSpawnIndex, Vector3 attackOrigin)
        {
            if (player == null || !IsParryKind(kind)) return false;

            if (Take(attackerSpawnIndex, kind, fromForward: false, out Entry copyFirst))
            {
                if (copyFirst.Parried) ParriedFromCopy++;
                return copyFirst.Parried;
            }

            // The game's test, from where the attack comes from as this end sees it: the copy of the attacker when it is
            // live, else the host's origin.
            Unit? copy = ResolveLiveCopy(attackerSpawnIndex);
            Vector3 from = copy != null ? copy.transform.position : attackOrigin;
            bool parried;
            InForwardTest = true;
            try { parried = player.TryParryWithWeapon(from, (MeleeParryTypes)kind, copy); }
            finally { InForwardTest = false; }
            Add(attackerSpawnIndex, kind, fromForward: true, parried);
            if (parried) ParriedLive++;
            return parried;
        }

        /// <summary>CLIENT, from the <c>Unit.TryParryWithWeapon</c> prefix: the copy of <paramref name="attacker"/> is hitting
        /// the local player. When the forwarded hit of the same attack came first, answer with its result (true) and the
        /// game's own test is skipped.</summary>
        public static bool TryAnswerCopyParry(Unit attacker, byte kind, out bool parried)
        {
            parried = false;
            if (!IsParryKind(kind) || !NetGameplayProbeManager.TryGetCopyHostSpawnIndex(attacker, out int idx)) return false;
            if (!Take(idx, kind, fromForward: true, out Entry forwardFirst)) return false;
            parried = forwardFirst.Parried;
            CopyAnsweredFromForward++;
            return true;
        }

        /// <summary>CLIENT, from the <c>Unit.TryParryWithWeapon</c> postfix when the game's test ran for a copy's hit on the
        /// local player: remember a parry for the forwarded hit of the same attack.</summary>
        public static void RecordCopyResult(Unit attacker, byte kind, bool parried)
        {
            if (!parried || !IsParryKind(kind) || !NetGameplayProbeManager.TryGetCopyHostSpawnIndex(attacker, out int idx)) return;
            Add(idx, kind, fromForward: false, parried: true);
        }

        private static bool IsParryKind(byte kind) => kind == (byte)MeleeParryTypes.Melee || kind == (byte)MeleeParryTypes.Ranged;

        private static Unit? ResolveLiveCopy(int attackerSpawnIndex)
        {
            if (attackerSpawnIndex <= 0) return null;
            if (!NetGameplayProbeManager.TryGetHostBoundRuntimeObject(attackerSpawnIndex, out object? o)) return null;
            return o is Unit u && u != null && u.gameObject.activeInHierarchy ? u : null;
        }

        private static void Add(int attackerSpawnIndex, byte kind, bool fromForward, bool parried)
        {
            Prune();
            Pending.Add(new Entry { AttackerSpawnIndex = attackerSpawnIndex, Kind = kind, At = Time.realtimeSinceStartup, FromForward = fromForward, Parried = parried });
        }

        // The oldest unpaired entry of the other side for this attacker and type. An unknown attacker (0) only pairs with
        // another unknown one.
        private static bool Take(int attackerSpawnIndex, byte kind, bool fromForward, out Entry entry)
        {
            Prune();
            for (int i = 0; i < Pending.Count; i++)
            {
                Entry e = Pending[i];
                if (e.FromForward != fromForward || e.Kind != kind || e.AttackerSpawnIndex != attackerSpawnIndex) continue;
                Pending.RemoveAt(i);
                entry = e;
                return true;
            }
            entry = default;
            return false;
        }

        private static void Prune()
        {
            float cutoff = Time.realtimeSinceStartup - PairSeconds;
            for (int i = Pending.Count - 1; i >= 0; i--)
                if (Pending[i].At < cutoff) Pending.RemoveAt(i);
        }
    }
}
