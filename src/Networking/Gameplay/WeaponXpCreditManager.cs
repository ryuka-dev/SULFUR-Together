using System;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Items;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Weapons;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Issue #4: weapon XP is personal, client-owned state.
    /// <para>The vanilla game credits weapon XP in <c>Npc.Die() -> GiveExperience()</c>, which adds the enemy's
    /// <c>ExperienceOnKill</c> to the local player's <c>EquipmentManager.lastEquippedWeapon</c> — but only when the
    /// enemy's <c>damagedByPlayer</c> flag is set (any player-faction hit sets it in <c>Unit.ReceiveDamage</c>).</para>
    /// <para>On a client, weapon hits are routed to the host for authoritative damage and the local
    /// <c>Unit.ReceiveDamage</c> is suppressed, so the client's enemy puppet never has <c>damagedByPlayer</c> set and its
    /// <c>GiveExperience()</c> early-returns. The client's own weapon therefore never accrues XP.</para>
    /// <para>This helper reproduces the vanilla credit locally on the machine that dealt the damage: when a puppet the
    /// local player damaged dies, it adds that puppet's <c>ExperienceOnKill</c> to the local last-equipped weapon. The
    /// kill itself stays host-authoritative — only the personal XP is applied client-side, so it stays client-owned and
    /// each participating client's own <c>Stat_BonusXP</c> (applied inside <c>InventoryItem.AddExperience</c>) is honored.
    /// No new network message is introduced; attribution comes from the existing ClientHitRequest path.</para>
    /// </summary>
    internal static class WeaponXpCreditManager
    {
        /// <summary>
        /// Adds <paramref name="npc"/>'s <c>ExperienceOnKill</c> to the local player's last-equipped weapon, mirroring
        /// the vanilla <c>Npc.GiveExperience()</c> credit. Safe on any thread state: fully guarded, never throws.
        /// </summary>
        /// <returns>The experience amount credited, or 0 if nothing was credited.</returns>
        public static int CreditLocalPlayerWeaponForKill(object? npc)
        {
            try
            {
                if (npc is not Unit unit) return 0;

                // Enemy base XP + its own affix (mutation) bonus, evaluated in this machine's context.
                int exp = unit.ExperienceOnKill;
                if (exp <= 0) return 0;

                GameManager gm = GameManager.Instance;
                EquipmentManager em = gm != null ? gm.EquipmentManager : null;
                Weapon weapon = em != null ? em.lastEquippedWeapon : null;
                if (weapon == null) return 0;

                // Vanilla path: Weapon.AddExperience applies the local player's Stat_BonusXP and handles rank-up.
                weapon.AddExperience(exp);

                if (Plugin.Cfg.LogWeaponXpCredit.Value)
                    NetLogger.Info($"[WeaponXp] credited exp={exp} to local last-equipped weapon for a local-player kill");

                return exp;
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.LogWeaponXpCredit.Value)
                    NetLogger.Warn($"[WeaponXp] credit failed: {ex.Message}");
                return 0;
            }
        }
    }
}
