using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetClientHitRequestCodec
    {
        /// <summary>v1 (no version byte) carried only a raw damage number; v2 (HR-1) carries the full hit input and a
        /// target kind; v3 (HR-3) adds the shot's projectile count. A peer on another protocol is refused at the
        /// handshake, so the byte guards against a malformed packet rather than negotiating anything.</summary>
        public const byte Version = 3;

        private const int MaxStringLength = 256;

        /// <summary>LiteNetLib's <c>GetString(max)</c> returns "" for an over-long string instead of failing, which would
        /// silently blank a field (an empty unit identifier skips the host's type guard). Refuse it instead.</summary>
        private static bool TryGetBounded(NetDataReader r, out string value)
        {
            value = r.GetString();
            return value != null && value.Length <= MaxStringLength;
        }

        public static void Write(NetDataWriter w, NetClientHitRequest r)
        {
            w.Put(Version);
            w.Put(r.ChapterName ?? "");
            w.Put(r.LevelIndex);
            w.Put(r.HasLevelSeed);
            if (r.HasLevelSeed) w.Put(r.LevelSeed);

            w.Put(r.RequestSeq);
            w.Put((byte)r.TargetKind);
            switch (r.TargetKind)
            {
                case NetHitTargetKind.Roster:
                case NetHitTargetKind.Corpse:
                    w.Put(r.TargetHostSpawnIndex);
                    w.Put(r.TargetUnitIdentifier ?? "");
                    break;
                case NetHitTargetKind.BossRole:
                    w.Put(r.EncounterKey ?? "");
                    w.Put(r.BossType ?? "");
                    w.Put(r.RootName ?? "");
                    w.Put(r.TargetRole ?? "");
                    break;
            }

            NetHitInputCodec.Write(w, r.Hit);
            w.Put(r.SentAt);
        }

        public static bool TryRead(NetDataReader r, out NetClientHitRequest result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != Version) return false;

                var req = new NetClientHitRequest();
                if (!TryGetBounded(r, out string chapter)) return false;
                req.ChapterName  = chapter;
                req.LevelIndex   = r.GetInt();
                req.HasLevelSeed = r.GetBool();
                if (req.HasLevelSeed) req.LevelSeed = r.GetInt();

                req.RequestSeq = r.GetInt();
                byte kind = r.GetByte();
                switch ((NetHitTargetKind)kind)
                {
                    case NetHitTargetKind.Roster:
                    case NetHitTargetKind.Corpse:
                        req.TargetHostSpawnIndex = r.GetInt();
                        if (!TryGetBounded(r, out string unitId)) return false;
                        req.TargetUnitIdentifier = unitId;
                        break;
                    case NetHitTargetKind.BossRole:
                        if (!TryGetBounded(r, out string enc) || !TryGetBounded(r, out string bossType)
                            || !TryGetBounded(r, out string rootName) || !TryGetBounded(r, out string role)) return false;
                        req.EncounterKey = enc;
                        req.BossType     = bossType;
                        req.RootName     = rootName;
                        req.TargetRole   = role;
                        break;
                    case NetHitTargetKind.EmperorWormTail:
                    case NetHitTargetKind.EmperorSpider:
                        break;
                    default:
                        return false;
                }
                req.TargetKind = (NetHitTargetKind)kind;

                req.Hit    = NetHitInputCodec.Read(r);
                req.SentAt = r.GetFloat();
                if (r.AvailableBytes != 0) return false; // trailing garbage = not a request of this version

                result = req;
                return true;
            }
            catch { return false; }
        }
    }
}
