using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetHostAttackPhaseEventCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetHostAttackPhaseEvent evt)
        {
            w.Put(Version);
            w.Put(evt.ChapterName ?? "");
            w.Put(evt.LevelIndex);
            w.Put(evt.HasLevelSeed);
            if (evt.HasLevelSeed) w.Put(evt.LevelSeed);
            w.Put(evt.HostSpawnIndex);
            w.Put(evt.UnitIdentifier ?? "");
            w.Put((byte)evt.AttackPhase);
            w.Put((byte)evt.AttackKind);
            w.Put((byte)evt.ActionKind);
            w.Put(evt.ActionState);
            w.Put(evt.Sequence);
            w.Put(evt.HasAimData);
            if (evt.HasAimData)
            {
                w.Put(evt.OriginPosition.x); w.Put(evt.OriginPosition.y); w.Put(evt.OriginPosition.z);
                w.Put(evt.AimPosition.x);    w.Put(evt.AimPosition.y);    w.Put(evt.AimPosition.z);
            }
            w.Put(evt.HasAnimatorHint);
            if (evt.HasAnimatorHint)
            {
                w.Put(evt.AnimatorFullPathHash);
                w.Put(evt.AnimatorNormalizedTime);
            }
            w.Put(evt.SentAt);
        }

        public static bool TryRead(NetDataReader r, out NetHostAttackPhaseEvent evt)
        {
            evt = new NetHostAttackPhaseEvent();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;

                evt.ChapterName  = r.GetString();
                evt.LevelIndex   = r.GetInt();
                evt.HasLevelSeed = r.GetBool();
                if (evt.HasLevelSeed) evt.LevelSeed = r.GetInt();
                evt.HostSpawnIndex = r.GetInt();
                evt.UnitIdentifier = r.GetString();
                evt.AttackPhase  = r.GetByte();
                evt.AttackKind   = r.GetByte();
                evt.ActionKind   = r.GetByte();
                evt.ActionState  = r.GetInt();
                evt.Sequence     = r.GetInt();
                evt.HasAimData   = r.GetBool();
                if (evt.HasAimData)
                {
                    evt.OriginPosition = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    evt.AimPosition    = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                }
                evt.HasAnimatorHint = r.GetBool();
                if (evt.HasAnimatorHint)
                {
                    evt.AnimatorFullPathHash   = r.GetInt();
                    evt.AnimatorNormalizedTime = r.GetFloat();
                }
                evt.SentAt = r.GetFloat();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
