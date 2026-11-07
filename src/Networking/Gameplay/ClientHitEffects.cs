using System;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Effects;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Weapons;
using PerfectRandom.Sulfur.Core.Utilities;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// HR-3 (CLIENT): what the game does for the attacking player at the end of a hit, which on the host happens to the
    /// client's stand-in unit and is therefore lost. Driven by <see cref="ClientHitTracker"/> from the host's verdicts,
    /// before it raises the public events, and independent of whether anything subscribes to them.
    /// <list type="bullet">
    /// <item><b>Lifesteal.</b> Vanilla <c>Unit.ReceiveDamage</c> ends with <c>sourceData.sourceUnit.OnGaveDamage(target,
    /// damage)</c>, which heals by the attacker's own <c>Stat_Lifesteal</c>. The host reports the amounts it passed to
    /// the stand-in (the hit's own call and each secondary instance, e.g. Crusader's Holy damage); they are handed to
    /// the local player's real <c>OnGaveDamage</c> here, one call per instance as in vanilla.</item>
    /// <item><b>Kill count.</b> Vanilla <c>Unit.UpdateKillCounter</c> needs <c>sourceData.isPlayer</c>, which a remote hit
    /// does not set, so the client's kills never reached its <c>ENEMIES_KILLED</c> or the <c>enemies_killed</c> stat.
    /// A kill credit counts here when the client dealt the killing blow or did damage to the enemy (an assist): two
    /// players who both damaged an enemy each count it. A credit with no damage (every hit arrived after the death)
    /// does not count.</item>
    /// <item><b>Organs (HR-5).</b> Vanilla <c>Npc.CreateDamageEffects</c> rolls an organ pickup (or loose organ gibs) for the
    /// hit that left an NPC dead, when <c>sourceData.isPlayer</c>. The host settles a client's killing hit without the
    /// flag, so it never rolled one — and loot is the client's own: an organ the host rolled would land in the host's
    /// world in Independent loot mode. Once the enemy has died on this end, the killing hit's effects run on the local
    /// copy with this player as the source (the game's own method: the organ roll, blood and bullet hole of that hit).
    /// The same runs for the client's hits of that volley that reached the host after the death (a shotgun's later
    /// pellets, resolved TargetDead): in vanilla each of them lands on the dead body as a player's hit and rolls too.
    /// A pickup it spawns follows the loot mode like any local pickup — in Shared mode the client's spawn is mirrored
    /// through the host like the organs a client already rolls when it shoots a corpse; that roll stays the shooter's,
    /// as in single player. Hits on a body that is already dead before they are fired stay local and roll there as
    /// before.</item>
    /// </list>
    /// </summary>
    internal static class ClientHitEffects
    {
        public static int LifestealCalls, KillsCounted, KillingHitEffects;

        private static System.Reflection.MethodInfo? _createDamageEffects;
        private static bool _createDamageEffectsResolved;

        /// <summary>Apply the lifesteal amounts of one resolved hit to the local player.</summary>
        public static void ApplyLifesteal(object? localTarget, NetHostHitResult r)
        {
            if (r == null) return;
            bool any = Usable(r.GaveDamage) || Usable(r.GaveDamageOther);
            if (!any && r.Secondary != null)
                foreach (var s in r.Secondary) if (Usable(s.GaveDamage)) { any = true; break; }
            if (!any) return;

            Unit? player = LocalPlayer();
            if (player == null) return;
            Unit target = localTarget is Unit t && t != null ? t : null!;
            try
            {
                if (Usable(r.GaveDamage)) { player.OnGaveDamage(target, r.GaveDamage); LifestealCalls++; }
                if (r.Secondary != null)
                    foreach (var s in r.Secondary)
                        if (Usable(s.GaveDamage)) { player.OnGaveDamage(target, s.GaveDamage); LifestealCalls++; }
                // Damage to other units (Voodoo shares): vanilla calls it once per share with that unit; the host sends
                // the sum, which heals the same (the heal is linear in the amount). The hit's own target stands in.
                if (Usable(r.GaveDamageOther)) { player.OnGaveDamage(target, r.GaveDamageOther); LifestealCalls++; }
            }
            catch (Exception ex) { NetLogger.Warn($"[ClientHit] lifesteal failed seq={r.RequestSeq}: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>A kill credit fired for <paramref name="localTarget"/>.</summary>
        /// <param name="isNpc">The target's <c>Unit.isNpc</c>, read while it was alive (the unit may be gone by now).</param>
        public static void OnKillCredited(bool isNpc, bool killingBlow, float damageDealt)
        {
            try
            {
                if (!killingBlow && !(damageDealt > 0f)) return;
                // Vanilla counts only NPC kills (UpdateKillCounter: isNpc && isPlayer).
                if (!isNpc) return;
                var achievements = StaticInstance<AchievementManager>.Instance;
                if (achievements != null) achievements.AddToStat(AchievementStats.enemies_killed, 1);
                if (SulfurSaveState.active != null) SulfurSaveState.active.ENEMIES_KILLED++;
                KillsCounted++;
            }
            catch (Exception ex) { NetLogger.Warn($"[ClientHit] kill count failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Run the killing hit's damage effects on the dead local copy (organ roll included).</summary>
        public static void RollKillingHitOrgans(object? localTarget, NetHitInput hit, object? weapon, UnityEngine.Vector3? position)
        {
            try
            {
                if (!(localTarget is Npc npc) || npc == null || npc.IsAlive || npc.hitmesh == null) return;
                var shape = new HitboxColliders.ShapeId { partAndIndex = hit.ShapeId };
                if (shape.part == HitboxColliders.Parts.None) return;
                if (!TryFindShape(npc, shape, out Hitmesh.Data hitbox)) return;
                if (!_createDamageEffectsResolved)
                {
                    _createDamageEffectsResolved = true;
                    _createDamageEffects = HarmonyLib.AccessTools.Method(typeof(Npc), "CreateDamageEffects",
                        new[] { typeof(DamageSourceData), typeof(DamageTypes), typeof(Hitmesh.Data), typeof(UnityEngine.Vector3?) });
                    if (_createDamageEffects == null) NetLogger.Warn("[ClientHit] Npc.CreateDamageEffects not found — a client's killing hit rolls no organ");
                }
                if (_createDamageEffects == null) return;

                var f = (NetHitStateFlags)hit.StateFlags;
                var sd = new DamageSourceData
                {
                    name         = "CoopKillingHit",
                    isPlayer     = true,
                    melee        = hit.Melee,
                    damageType   = (DamageTypes)hit.DamageType,
                    sourceWeapon = weapon as Weapon,
                    sourceUnit   = LocalPlayerAnyState()!,
                    projectile   = new FiredProjectileState
                    {
                        caliberType = (CaliberTypes)hit.CaliberType,
                        effect      = (ProjectileEffect)hit.ProjectileEffect,
                    },
                    states = new AttackStates
                    {
                        factionId          = FactionIds.Player,
                        replacesBloodType  = (DecalType)hit.ReplacesBloodType,
                        alwaysSpawnOrgans  = (f & NetHitStateFlags.AlwaysSpawnOrgans) != 0,
                        dropDisabledOrgan  = (f & NetHitStateFlags.DropDisabledOrgan) != 0,
                        disableBloodOnHit  = (f & NetHitStateFlags.DisableBloodOnHit) != 0,
                    },
                };
                _createDamageEffects.Invoke(npc, new object?[] { sd, (DamageTypes)hit.DamageType, hitbox, position });
                KillingHitEffects++;
            }
            catch (Exception ex) { NetLogger.Warn($"[ClientHit] killing hit effects failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>The unit's own <c>Hitmesh.Data</c> for the shape — on its main hitmesh or one it owns further down
        /// the hierarchy (vanilla melee takes the shape from whichever hitmesh it struck).</summary>
        private static bool TryFindShape(Npc npc, HitboxColliders.ShapeId shape, out Hitmesh.Data hitbox)
        {
            hitbox = npc.hitmesh.GetHitShape(shape);
            if (hitbox.shapeId.partAndIndex == shape.partAndIndex) return true;
            foreach (var hm in npc.GetComponentsInChildren<Hitmesh>(true))
            {
                if (hm == null || hm == npc.hitmesh || hm.owner != npc) continue;
                hitbox = hm.GetHitShape(shape);
                if (hitbox.shapeId.partAndIndex == shape.partAndIndex) return true;
            }
            return false;
        }

        public static string FormatSummary() => $"lifestealCalls={LifestealCalls} killsCounted={KillsCounted} killingHitEffects={KillingHitEffects}";

        private static Unit? LocalPlayerAnyState()
        {
            try { var gm = GameManager.Instance; return gm != null ? gm.PlayerUnit : null; } catch { return null; }
        }

        private static bool Usable(float v) => RemoteHitResolver.IsFinite(v) && v > 0f && v <= RemoteHitResolver.MaxHitDamage;

        /// <summary>The local player while it can take a heal: alive and not downed (a downed player's health is held by
        /// the downed state; a heal must not lift it).</summary>
        private static Unit? LocalPlayer()
        {
            try
            {
                var gm = GameManager.Instance;
                Unit? pu = gm != null ? gm.PlayerUnit : null;
                if (pu == null || !pu.IsAlive) return null;
                if (NetPlayerLifeManager.IsDownedLocalPlayerUnit(pu)) return null;
                return pu;
            }
            catch { return null; }
        }
    }
}
