using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// CG-1a (Host → all clients): a corpse burst. The host's <c>Npc.ReceiveDamage</c> took one of its dead-unit
    /// branches and gibbed the body.
    ///
    /// <para><b>Why corpses need a channel of their own.</b> A dead enemy is still an interactive object in this game:
    /// <c>Npc.ReceiveDamage</c> has a whole <c>unitState == Dead</c> section that turns a body into gibs and
    /// <b>drops loot</b> — a frozen-solid corpse shatters on one melee hit, on the fifth ranged hit, or instantly to a
    /// damage type whose asset sets <c>explodesCorpseIfDiedByThis</c>, and any corpse bursts to explosive damage over
    /// 75. None of that was mirrored, so each end kept its own ice statue and only the machine that shot it saw the
    /// body break. The maintainer reported exactly that once ST-3e made the statues appear on clients at all.</para>
    ///
    /// <para><b>Only the burst travels.</b> <c>SpawnLoot</c> stays host-side, where the shared-loot model already owns
    /// what drops and who may take it; a client that reproduced it locally would be inventing items. So the mirror
    /// calls <c>SpawnGibExplosion</c> and nothing else.</para>
    ///
    /// <para><see cref="FrozenSolid"/> carries which flavour vanilla used: its frozen branches force ice gibs
    /// explicitly, while the explosive branch passes nothing and lets the unit's own state decide. Sent rather than
    /// re-derived for the same reason ST-3c sends it — a client's copy of a decaying status is not a reliable witness
    /// at one exact instant.</para>
    /// </summary>
    internal sealed class NetHostCorpseGib
    {
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        public int    HostSpawnIndex { get; set; } = -1;
        public string UnitIdentifier { get; set; } = "";

        /// <summary>Vanilla forced ice gibs for this burst.</summary>
        public bool   FrozenSolid { get; set; }

        public int    Sequence { get; set; }
        public float  SentAt   { get; set; }

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

        public string ToCompact() => $"idx={HostSpawnIndex} unit={UnitIdentifier} frozen={FrozenSolid} seq={Sequence}";
    }

    internal static class NetHostCorpseGibCodec
    {
        public static void Write(NetDataWriter w, NetHostCorpseGib m)
        {
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            if (m.HasLevelSeed) w.Put(m.LevelSeed);
            w.Put(m.HostSpawnIndex);
            w.Put(m.UnitIdentifier ?? "");
            w.Put(m.FrozenSolid);
            w.Put(m.Sequence);
            w.Put(m.SentAt);
        }

        public static bool TryRead(NetDataReader r, out NetHostCorpseGib m)
        {
            m = null!;
            try
            {
                var msg = new NetHostCorpseGib
                {
                    ChapterName  = r.GetString(),
                    LevelIndex   = r.GetInt(),
                    HasLevelSeed = r.GetBool(),
                };
                if (msg.HasLevelSeed) msg.LevelSeed = r.GetInt();
                msg.HostSpawnIndex = r.GetInt();
                msg.UnitIdentifier = r.GetString();
                msg.FrozenSolid    = r.GetBool();
                msg.Sequence       = r.GetInt();
                msg.SentAt         = r.GetFloat();
                m = msg;
                return true;
            }
            catch { return false; }
        }
    }
}
