using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// FF-1: builds and sends the client→host friendly-fire hit report. Called from the
    /// <c>Unit_ReceiveDamage_Pre</c> client branch when the local player's damage lands on a
    /// <see cref="ClientPlayerHitProxyManager"/> hit proxy. Scene context comes from the local run state so the
    /// host's <c>MatchesScene</c> check drops cross-scene stragglers.
    /// </summary>
    internal static class NetFriendlyFireManager
    {
        private static int _seq;

        public static void SendLocalPlayerHit(string victimPeerId, float damage, int damageTypeInt, Vector3 hitPos)
        {
            if (string.IsNullOrWhiteSpace(victimPeerId) || damage <= 0f) return;
            var msg = new NetFriendlyFireHit
            {
                VictimPeerId = victimPeerId,
                Damage = damage,
                DamageTypeInt = damageTypeInt,
                HasPosition = hitPos != Vector3.zero,
                Position = hitPos,
                Seq = ++_seq,
            };
            if (NetRunStateBridge.TryGetLocalRunState(out var local) && local != null)
            {
                msg.SourcePeerId = local.PeerId ?? "";
                msg.ChapterName = local.ChapterName ?? "";
                msg.LevelIndex = local.LevelIndex;
                msg.HasLevelSeed = local.HasLevelSeed;
                msg.LevelSeed = local.LevelSeed;
            }
            NetGameplaySyncBridge.SendFriendlyFireHit(msg);
            if (Plugin.Cfg.LogFriendlyFire.Value)
                Plugin.Log.Info($"[FF] hit request sent: victim={victimPeerId} dmg={damage:F1} type={damageTypeInt} seq={msg.Seq}");
        }
    }
}
