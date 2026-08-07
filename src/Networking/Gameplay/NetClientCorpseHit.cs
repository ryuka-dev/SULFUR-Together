using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// CG-1b (Client → host): the local player hit a body that is already dead.
    ///
    /// <para><b>Why this needs a channel of its own rather than the ordinary hit request.</b> The host applies a
    /// <see cref="NetClientHitRequest"/> with a raw health write — deliberately, because that path exists to move a
    /// number, not to re-run combat — and a raw write never reaches <c>Npc.ReceiveDamage</c>'s <c>unitState == Dead</c>
    /// section, which is where a corpse bursts. So a forwarded corpse hit has to arrive as what it is and be replayed
    /// through the real call.</para>
    ///
    /// <para><b>What has to travel, and why exactly this.</b> Vanilla's four burst conditions read the damage TYPE
    /// (a type whose asset sets <c>explodesCorpseIfDiedByThis</c> bursts a frozen body instantly, and Explosive above
    /// 75 bursts any body) and whether the blow was MELEE (one melee hit bursts a frozen body; ranged needs five).
    /// Neither is recoverable on the host from the amount alone. The counting itself is not sent: <c>frozenDamageInstances</c>
    /// lives on the host's own body and stays there, which is what keeps five clients' shots from being counted five
    /// different ways.</para>
    ///
    /// <para>Nothing about the outcome is decided here. The host replays the hit, vanilla decides whether the body
    /// bursts, and if it does the host's own CG-1a postfix mirrors the burst back out — including to the client that
    /// asked, whose local burst was suppressed. Loot lands on the host, where the shared-loot model owns it.</para>
    /// </summary>
    internal sealed class NetClientCorpseHit
    {
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        public int    RequestSeq   { get; set; }

        public int    TargetHostSpawnIndex { get; set; } = -1;
        public string TargetUnitIdentifier { get; set; } = "";

        public float  Damage        { get; set; }
        /// <summary>Raw <c>DamageTypes</c> id. Host re-validates it against the enum before use.</summary>
        public int    DamageTypeInt { get; set; }
        public bool   Melee         { get; set; }

        public float  SentAt { get; set; }

        public bool MatchesScene(NetRunState localState)
        {
            if (!localState.HasLevel) return false;
            if (!string.Equals(localState.ChapterName, ChapterName, System.StringComparison.Ordinal)) return false;
            if (localState.LevelIndex != LevelIndex) return false;
            if (Plugin.Cfg.EnableLevelSeedAuthority.Value)
            {
                if (!HasLevelSeed || !localState.HasLevelSeed) return false;
                if (localState.LevelSeed != LevelSeed) return false;
            }
            return true;
        }

        public string ToCompact() => $"seq={RequestSeq} idx={TargetHostSpawnIndex} dmg={Damage:F1} type={DamageTypeInt} melee={Melee}";
    }

    internal static class NetClientCorpseHitCodec
    {
        public static void Write(NetDataWriter w, NetClientCorpseHit m)
        {
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            if (m.HasLevelSeed) w.Put(m.LevelSeed);
            w.Put(m.RequestSeq);
            w.Put(m.TargetHostSpawnIndex);
            w.Put(m.TargetUnitIdentifier ?? "");
            w.Put(m.Damage);
            w.Put(m.DamageTypeInt);
            w.Put(m.Melee);
            w.Put(m.SentAt);
        }

        public static bool TryRead(NetDataReader r, out NetClientCorpseHit m)
        {
            m = null!;
            try
            {
                var msg = new NetClientCorpseHit
                {
                    ChapterName  = r.GetString(),
                    LevelIndex   = r.GetInt(),
                    HasLevelSeed = r.GetBool(),
                };
                if (msg.HasLevelSeed) msg.LevelSeed = r.GetInt();
                msg.RequestSeq           = r.GetInt();
                msg.TargetHostSpawnIndex = r.GetInt();
                msg.TargetUnitIdentifier = r.GetString();
                msg.Damage               = r.GetFloat();
                msg.DamageTypeInt        = r.GetInt();
                msg.Melee                = r.GetBool();
                msg.SentAt               = r.GetFloat();
                m = msg;
                return true;
            }
            catch { return false; }
        }
    }
}
