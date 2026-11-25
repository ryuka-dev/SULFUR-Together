using System;
using System.Collections.Generic;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// ED-3: an enemy's explosion reaches a client as an event the client resolves against its own player.
    ///
    /// <para>The game resolves an explosion against the player in <c>ExplosionManager.Update</c>: the player is hit when its
    /// <c>ExplosionCenter</c> is within the damage radius and the explosion has line of sight to its eyes (two raycasts on
    /// <c>occlusionMask</c>, only while the eyes are inside the radius), for <c>(1 - distance / radius) × damage</c>. On the
    /// host the client is a stand-in: not a player (no line-of-sight check), and no <c>mainCollider</c>, so its centre is its
    /// feet. Judging the stand-in and forwarding the hit (ED-2) therefore gave a client a different set of explosions than
    /// its own player would take (round 693: 3 forwarded for 5 the copy landed).</para>
    ///
    /// <para>Here the host stays the authority on THAT an enemy explosion happened and sends it to each client whose
    /// stand-in is near the point; the client applies the game's own test with its real player. The host reads them where
    /// the game resolves them, at the start of <c>ExplosionManager.Update</c>: the explosions queued through
    /// <c>QueueExplosion</c> (<c>_queuedExplosions</c>) and the ones a projectile's hit job writes straight into
    /// <c>commandData</c> (a weapon with an explosion on hit), with the owner resolved from <c>ownerID</c> in the same frame
    /// the stand-in's hit is judged, so an owner destroyed in between drops out of both. The stand-in's own hit by that explosion is
    /// absorbed on the host (<see cref="IsRelayedOwner"/>, <c>Unit_ReceiveDamage_Pre</c>), and the client's copy of the
    /// enemy no longer damages the local player with its explosion (ED-2), so each explosion is judged once, on the end
    /// that has the player.</para>
    ///
    /// <para>The point is taken relative to the enemy that owns the explosion, as this end shows it: the host sends the
    /// owner's position with the event, and the client moves the point by how far its copy of that enemy stands from it.
    /// A copy follows the host's enemy within a tolerance, and an enemy's own explosion goes off where the player sees
    /// the enemy (round 696: the copy of a Goblin Barrel Boy landed 1.7–2 m nearer the client than the host's did, every
    /// landing, so the host's point missed a player the explosion visibly hit). Only an explosion centred on its owner is
    /// moved (within <see cref="OwnerCentred"/> of the owner on the host — a slam, a self-destruct); one the owner threw
    /// or that merely carries its name (dynamite, a corpse a killer blew up) keeps the host's point. A copy farther than
    /// <see cref="MaxCopyOffset"/> from the host's enemy, or inactive, is not trusted for that either.</para>
    ///
    /// <para>Only explosions an enemy owns are relayed: a player's own explosion (the host player's, friendly fire) and one
    /// owned by a remote player's stand-in keep their existing paths. The status an explosion applies is not relayed; the
    /// copy's local explosion still applies it as before.</para>
    ///
    /// <para>A Sapper's mine is not relayed either (SM-1, <see cref="SapperMineSync"/>): every end has the mine and its
    /// explosion, at the same point, and judges its own player by it. The stand-in's hit by the host's mine explosion is
    /// absorbed (<see cref="StandInAbsorbsExplosion"/>). Mine explosions are told by their explosion type and point: the
    /// mine that owns one is destroyed by the break that queued it, usually before the explosion resolves.</para>
    /// </summary>
    internal static class EnemyExplosionRelay
    {
        // The stand-in follows the client a little behind; an explosion this much farther than its radius from the
        // stand-in is still sent, so a client that has just moved into range is not missed. The client does the real test.
        private const float StandInSlack = 3f;

        // How far a copy may stand from the host's enemy and still stand in for it when the point is placed.
        public const float MaxCopyOffset = 4f;

        // An explosion this close to its owner on the host goes off at the owner, so it follows the owner's copy.
        public const float OwnerCentred = 2f;

        private static readonly AccessTools.FieldRef<ExplosionManager, List<ExplosionManager.Data>>? QueuedExplosions =
            TryFieldRef("_queuedExplosions");

        private static AccessTools.FieldRef<ExplosionManager, List<ExplosionManager.Data>>? TryFieldRef(string name)
        {
            try { return AccessTools.FieldRefAccess<ExplosionManager, List<ExplosionManager.Data>>(name); }
            catch { return null; }
        }

        /// <summary>Did the explosion lists resolve? Reported once at patch time.</summary>
        public static bool CanReadQueue => QueuedExplosions != null;

        // Diagnostics only (read with an inspector); never consulted.
        public static int HostSent, ClientResolved, ClientHit, HostMineExplosions;

        // SM-1 (HOST): the points of the Sapper mine explosions the current ExplosionManager.Update resolves. Refilled by
        // the Update prefix, read by the stand-in's ReceiveDamage inside that same Update.
        private static readonly List<Vector3> _mineExplosionPoints = new List<Vector3>();

        /// <summary>HOST, from the stand-in's <c>ReceiveDamage</c> inside <c>ExplosionManager.Update</c>: the hit is not
        /// forwarded to the client, because the client judges this explosion itself: an enemy's is relayed to it as a
        /// HostExplosion, and a Sapper mine's goes off on the client's own end too.</summary>
        public static bool StandInAbsorbsExplosion(DamageSourceData source)
        {
            if (IsRelayedOwner(source.sourceUnit)) return true;
            if (!source.sourcePosition.HasValue || _mineExplosionPoints.Count == 0) return false;
            Vector3 p = source.sourcePosition.Value;
            for (int i = 0; i < _mineExplosionPoints.Count; i++)
                if ((_mineExplosionPoints[i] - p).sqrMagnitude < 0.0001f) return true;
            return false;
        }

        /// <summary>BOTH ends: is an explosion with this owner relayed (and therefore absorbed on the host's stand-in)?
        /// True for an enemy — any unit that is neither a player nor a remote player's stand-in.</summary>
        public static bool IsRelayedOwner(Unit? owner)
        {
            if (owner == null) return false;
            if (owner.isPlayer) return false;
            if (RemotePlayerTargetProxyManager.IsProxyUnit(owner)) return false;
            return true;
        }

        /// <summary>HOST, from the <c>ExplosionManager.Update</c> prefix: every explosion this Update resolves — those the
        /// projectile system wrote into <c>commandData</c> and those queued — is sent to the clients near it.</summary>
        public static void OnHostExplosionsResolving(ExplosionManager em)
        {
            _mineExplosionPoints.Clear();
            if (em == null || NetConfig.GetMode() != NetMode.Host || !NetGameplaySyncBridge.IsSessionActive) return;
            if (em.commandData.IsCreated)
                for (int i = 0; i < em.commandData.Length; i++) Relay(em.commandData[i]);
            List<ExplosionManager.Data>? queued = QueuedExplosions?.Invoke(em);
            if (queued != null)
                for (int i = 0; i < queued.Count; i++) Relay(queued[i]);
        }

        private static void Relay(ExplosionManager.Data data)
        {
            if (data.id == ExplosionTypes.SapperMine)
            {
                _mineExplosionPoints.Add(data.point);
                HostMineExplosions++;
                return;
            }
            // The owner from the same ownerID ExplosionManager.Update resolves (through its cache) a moment later for the
            // stand-in's hit; a destroyed owner is null on both.
            Unit? owner = Resources.EntityIdToObject(data.ownerID) as Unit;
            if (!IsRelayedOwner(owner)) return;
            if (data.muteDamage || !(data.damage > 0f) || !(data.damageRadius > 0f)) return;

            var gm = StaticInstance<GameManager>.Instance;
            if (gm == null || gm.Players == null) return;

            Vector3 point = data.point;
            float reach = data.damageRadius + StandInSlack;
            int damageType = (int)(data.damageType == DamageTypes.None ? DamageTypes.Explosive : data.damageType);
            NetGameplayProbeManager.TryGetHostEntityBinding(owner, out int attackerIdx, out _);

            for (int i = 0; i < gm.Players.Count; i++)
            {
                Player p = gm.Players[i];
                if (p == null) continue;
                Unit standIn = p.playerUnit;
                if (standIn == null || !RemotePlayerTargetProxyManager.TryGetProxyPeer(standIn, out string peerId)) continue;
                // ExplosionManager skips a unit of the owner's own faction when the explosion says so (a charmed enemy).
                if (data.cannotHitOwnFaction && owner!.FactionId != FactionIds.None && owner.FactionId == standIn.FactionId) continue;
                if ((standIn.transform.position - point).sqrMagnitude > reach * reach) continue;

                NetPlayerLifeManager.ReportHostEnemyExplosion(peerId, point, data.damage, data.damageRadius, damageType, attackerIdx, owner!.transform.position);
                HostSent++;
            }
        }

        /// <summary>CLIENT: the explosion point as this end shows it — moved with the copy of the enemy that owns it (see
        /// the class remarks), or the host's point when there is no usable copy.</summary>
        public static Vector3 PlaceRelativeToCopy(Vector3 hostPoint, int attackerSpawnIndex, Vector3 hostOwnerPosition, out float offset)
        {
            offset = 0f;
            if (attackerSpawnIndex <= 0) return hostPoint;
            if ((hostPoint - hostOwnerPosition).sqrMagnitude > OwnerCentred * OwnerCentred) return hostPoint;
            if (!NetGameplayProbeManager.TryGetHostBoundRuntimeObject(attackerSpawnIndex, out object? copy)
                || !(copy is Component c) || c == null || !c.gameObject.activeInHierarchy)
                return hostPoint;
            Vector3 shift = c.transform.position - hostOwnerPosition;
            offset = shift.magnitude;
            if (offset > MaxCopyOffset) return hostPoint;
            return hostPoint + shift;
        }

        /// <summary>CLIENT: the damage this explosion does to <paramref name="player"/> by the game's own rule, or 0 when it
        /// does not reach it.</summary>
        public static float ResolveAgainstPlayer(Unit player, Vector3 point, float damage, float radius, out string detail)
        {
            ClientResolved++;
            detail = "";
            if (player == null || !(radius > 0f) || !(damage > 0f)) { detail = "no player"; return 0f; }

            // Line of sight, as ExplosionManager.Update computes _playerInLOS: the eyes must be inside the radius and
            // neither ray between the point and the eyes may hit the occlusion mask.
            var em = StaticInstance<ExplosionManager>.Instance;
            if (em == null) return 0f;
            Vector3 eyes = player.EyesPosition;
            Vector3 toEyes = eyes - point;
            float eyeDistance = toEyes.magnitude;
            float centreDistance = (player.ExplosionCenter - point).magnitude;
            detail = $"eyes={eyeDistance:F2} centre={centreDistance:F2}";
            if (!(eyeDistance < radius)) { detail += " eyes out of radius"; return 0f; }
            Vector3 dir = toEyes.normalized;
            if (Physics.Raycast(point, dir, out RaycastHit h1, eyeDistance, em.occlusionMask)) { detail += $" occluded by {h1.collider.name}"; return 0f; }
            if (Physics.Raycast(eyes, -dir, out RaycastHit h2, eyeDistance, em.occlusionMask)) { detail += $" occluded by {h2.collider.name}"; return 0f; }

            // Distance from the unit's ExplosionCenter, linear falloff.
            if (centreDistance > radius) { detail += " centre out of radius"; return 0f; }
            float falloff = 1f - Mathf.Clamp01(centreDistance / radius);
            float result = falloff * damage;
            if (result > 0f) ClientHit++;
            return result;
        }
    }
}
