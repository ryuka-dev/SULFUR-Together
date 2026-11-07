using System;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
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
    /// </list>
    /// </summary>
    internal static class ClientHitEffects
    {
        public static int LifestealCalls, KillsCounted;

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

        public static string FormatSummary() => $"lifestealCalls={LifestealCalls} killsCounted={KillsCounted}";

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
