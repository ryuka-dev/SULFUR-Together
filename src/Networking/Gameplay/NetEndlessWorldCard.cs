using System;
using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// IND-1 (client → host, Independent mode): a client picked a world card whose object must be host-authoritative to
    /// function. Currently only the <b>companion</b> (<c>SpawnRandomAllies</c>): a client-local companion can't damage the
    /// host-authoritative enemy puppets, so the client suppresses its local spawn and asks the host to spawn the real
    /// companion at the client's ghost position, charmed to the picker. The host mirrors it back through the RuntimeSpawn
    /// puppet pipeline (source <c>"EndlessCompanion"</c>, EM-7c), so the picker sees it charmed to itself.
    ///
    /// <para>Other world cards (shop / chest / station / plain loot) work locally in Independent mode as personal objects,
    /// so they are not routed. The <see cref="Kind"/> byte leaves room to extend if that changes.</para>
    /// </summary>
    internal sealed class NetEndlessWorldCard
    {
        public const byte KindCompanion = 0;

        public byte   Kind;          // KindCompanion (0) — reserved for future world-card kinds
        public int    UnitIdValue;   // UnitSO.id.value the client resolved for the companion
        public string ChapterName = ""; // run-context guard (host ignores a stale pick for a level it isn't in)
        public int    LevelIndex;
    }

    internal static class NetEndlessWorldCardCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetEndlessWorldCard m)
        {
            w.Put(Version);
            w.Put(m.Kind);
            w.Put(m.UnitIdValue);
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
        }

        public static bool TryRead(NetDataReader r, out NetEndlessWorldCard m)
        {
            m = new NetEndlessWorldCard();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;
                m.Kind        = r.GetByte();
                m.UnitIdValue = r.GetInt();
                m.ChapterName = r.GetString();
                m.LevelIndex  = r.GetInt();
                return true;
            }
            catch { return false; }
        }
    }
}
