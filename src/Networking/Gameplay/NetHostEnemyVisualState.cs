using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// HR-8: Host → clients. One visual state of a roster enemy that the client's copy cannot reach on its own (see
    /// <see cref="EnemyVisualStateSync"/>): a hidden enemy was discovered (<see cref="KindReveal"/>), or one of its
    /// animator bools changed (<see cref="KindAnimatorBool"/>, <see cref="ParamHash"/> = <c>Animator.StringToHash</c>).
    /// </summary>
    internal sealed class NetHostEnemyVisualState
    {
        public const byte KindReveal = 1;
        public const byte KindAnimatorBool = 2;

        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }
        public int    HostSpawnIndex { get; set; }
        public string UnitIdentifier { get; set; } = "";
        public byte   Kind { get; set; }
        public int    ParamHash { get; set; }
        public bool   Value { get; set; }

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
    }

    internal static class NetHostEnemyVisualStateCodec
    {
        public const byte Version = 1;
        private const int MaxStringLength = 256;

        public static void Write(NetDataWriter w, NetHostEnemyVisualState e)
        {
            w.Put(Version);
            w.Put(e.ChapterName ?? "");
            w.Put(e.LevelIndex);
            w.Put(e.HasLevelSeed);
            if (e.HasLevelSeed) w.Put(e.LevelSeed);
            w.Put(e.HostSpawnIndex);
            w.Put(e.UnitIdentifier ?? "");
            w.Put(e.Kind);
            w.Put(e.ParamHash);
            w.Put(e.Value);
        }

        public static bool TryRead(NetDataReader r, out NetHostEnemyVisualState e)
        {
            e = null!;
            try
            {
                if (r.GetByte() != Version) return false;
                var m = new NetHostEnemyVisualState { ChapterName = r.GetString(), LevelIndex = r.GetInt(), HasLevelSeed = r.GetBool() };
                if (m.ChapterName == null || m.ChapterName.Length > MaxStringLength) return false;
                if (m.HasLevelSeed) m.LevelSeed = r.GetInt();
                m.HostSpawnIndex = r.GetInt();
                m.UnitIdentifier = r.GetString();
                if (m.UnitIdentifier == null || m.UnitIdentifier.Length > MaxStringLength) return false;
                m.Kind = r.GetByte();
                m.ParamHash = r.GetInt();
                m.Value = r.GetBool();
                if (m.Kind != NetHostEnemyVisualState.KindReveal && m.Kind != NetHostEnemyVisualState.KindAnimatorBool) return false;
                if (r.AvailableBytes != 0) return false;
                e = m;
                return true;
            }
            catch { return false; }
        }
    }
}
