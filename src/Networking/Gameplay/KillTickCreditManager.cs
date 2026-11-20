using System;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// TK-1: the world tick that restocks the vendors is personal, client-owned state (it lives in each player's save).
    /// <para>Vanilla restocks a vendor when the save's <c>GAME_TICK</c> has moved past the tick recorded the last time
    /// that vendor's shop was closed (<c>ShopKeeper.LoadItemsFromSave</c>). <c>GAME_TICK</c> advances in
    /// <c>TickManager.DoTickUpdate</c>, at most once per chapter, and the ordinary way to get there is
    /// <c>Npc.Die</c>: <c>if (LastDamagedBy.isPlayer) tickManager.RegisterKill()</c>, ten kills per tick.</para>
    /// <para>On a client the local <c>Unit.ReceiveDamage</c> on an enemy is suppressed and the hit is sent to the host,
    /// so the puppet's <c>LastDamagedBy</c> is never set. When the host's death arrives and the client runs the
    /// puppet's real <c>Npc.Die</c>, that check fails for every kill: the client's tick never moved, and its vendors
    /// never restocked however far the run went. The host counts its own and the client's kills as vanilla does.</para>
    /// <para>This counts the kill on the client for a puppet the local player hit (the same mark that credits weapon
    /// XP, Issue #4). Vanilla asks whether the last hit was a player's; the client cannot see the host's last hit
    /// without a protocol change, so any local hit counts. A puppet whose local <c>LastDamagedBy</c> was set after
    /// all already went through vanilla's own count and is skipped. No network message is involved.</para>
    /// </summary>
    internal static class KillTickCreditManager
    {
        private static int _credited;
        private static bool _failedLogged;

        /// <summary>CLIENT: the puppet <paramref name="npc"/>, which the local player hit, has just run its real
        /// <c>Npc.Die</c>. Count the kill toward the local tick the way vanilla would have. Never throws.</summary>
        public static void CreditLocalKill(object? npc)
        {
            try
            {
                if (NetConfig.GetMode() != NetMode.Client) return;
                if (npc is not Npc unit || unit == null) return;
                if (unit.LastDamagedBy.isPlayer) return; // vanilla Die already counted this one
                var ticks = StaticInstance<GameManager>.Instance != null ? StaticInstance<GameManager>.Instance.tickManager : null;
                if (ticks == null) return;
                ticks.RegisterKill();
                _credited++;
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Info($"[KillTick] client kill counted toward the vendor tick (total this session {_credited})");
            }
            catch (Exception ex)
            {
                if (_failedLogged) return;
                _failedLogged = true;
                NetLogger.Warn($"[KillTick] could not count a client kill: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
