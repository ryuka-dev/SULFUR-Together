using System;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Weapons;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Issue #4: weapon XP is personal, client-owned state.
    /// <para>The vanilla game credits weapon XP in <c>Npc.Die() -> GiveExperience() -> DistributeExperience()</c>, but
    /// only when the enemy's <c>damagedByPlayer</c> flag is set, and splits it across the weapons recorded in the
    /// enemy's <c>WeaponsUsedInKill</c> — both of which <c>Unit.ReceiveDamage</c> fills in.</para>
    /// <para>On a client, weapon hits are routed to the host for authoritative damage and the local
    /// <c>Unit.ReceiveDamage</c> is suppressed, so the client's enemy puppet never gets either and its
    /// <c>GiveExperience()</c> early-returns. The client's own weapons would therefore never accrue XP.</para>
    /// <para>GU102-5: this reproduces the vanilla credit with the game's own code instead of an approximation. When a
    /// client hit is forwarded, <see cref="RegisterLocalHitAttribution"/> records the hitting weapon on the puppet through
    /// the public <c>Npc.RegisterExpAttributionHits</c> (exactly what <c>ReceiveDamage</c> does for a player source);
    /// when that puppet dies, <see cref="CreditLocalPlayerWeaponForKill"/> runs the puppet's own
    /// <c>DistributeExperience(ExperienceOnKill)</c>. So the client gets vanilla's result on its own state: the local
    /// player's <c>Stat_BonusXP</c>, an even split across the weapons it used, no XP to max-rank weapons, and the
    /// current-weapon fallback when no weapon was recorded. (The previous approximation added the raw amount to the
    /// last-equipped weapon only, without the bonus multiplier, which <c>InventoryItem.AddExperience</c> never applies.)
    /// The kill itself stays host-authoritative; no network message is involved.</para>
    /// </summary>
    internal static class WeaponXpCreditManager
    {
        private static MethodInfo? _distributeExperience;
        private static bool _resolved;

        /// <summary>CLIENT: a local player hit on <paramref name="npc"/> was just forwarded to the host. Record the weapon
        /// for XP attribution the way vanilla <c>Unit.ReceiveDamage</c> would have. Never throws.</summary>
        public static void RegisterLocalHitAttribution(object? npc, object? source)
        {
            try
            {
                if (npc is not Npc target || source is not DamageSourceData data) return;
                if (!data.isPlayer) return;
                target.RegisterExpAttributionHits(data.sourceWeapon); // vanilla filters melee/throwable/max-rank itself
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.LogWeaponXpCredit.Value)
                    NetLogger.Warn($"[WeaponXp] hit attribution failed: {ex.Message}");
            }
        }

        /// <summary>
        /// CLIENT: credit a puppet kill the local player took part in, through the puppet's own vanilla
        /// <c>DistributeExperience</c>. Fully guarded, never throws.
        /// </summary>
        /// <returns>The experience amount distributed (before the bonus multiplier), or 0 if nothing was credited.</returns>
        public static int CreditLocalPlayerWeaponForKill(object? npc)
        {
            try
            {
                if (npc is not Npc unit) return 0;

                int exp = unit.ExperienceOnKill; // enemy base XP + its own affix (mutation) bonus, in this machine's context
                if (exp <= 0) return 0;

                MethodInfo? distribute = ResolveDistribute();
                if (distribute == null) return 0;

                distribute.Invoke(unit, new object[] { exp });

                if (Plugin.Cfg.LogWeaponXpCredit.Value)
                    NetLogger.Info($"[WeaponXp] distributed exp={exp} through the puppet's DistributeExperience for a local-player kill");

                return exp;
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.LogWeaponXpCredit.Value)
                    NetLogger.Warn($"[WeaponXp] credit failed: {ex.Message}");
                return 0;
            }
        }

        private static MethodInfo? ResolveDistribute()
        {
            if (_resolved) return _distributeExperience;
            _resolved = true;
            _distributeExperience = AccessTools.DeclaredMethod(typeof(Npc), "DistributeExperience", new[] { typeof(int) });
            if (_distributeExperience == null)
                NetLogger.Warn("[WeaponXp] Npc.DistributeExperience(int) not found — client weapon XP for kills is disabled.");
            return _distributeExperience;
        }
    }
}
