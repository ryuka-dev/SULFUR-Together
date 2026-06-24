using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetGameplayEnemyStateSnapshotCodec
    {
        private const int MaxSnapshotsPerPacket = 128;

        // Phase 4.4.0-D compact wire format:
        // - batch-scoped scene/source metadata
        // - per-enemy strings removed from the hot path
        // - bit-packed booleans
        //
        // Both peers are expected to run the same test build. This intentionally
        // favors lower per-packet allocation/parse cost over backward compatibility
        // with the previous string-heavy test format.
        private const byte WireVersion = 8;

        private const byte FlagHasPosition  = 1 << 0;
        private const byte FlagHasRotationY = 1 << 1;
        private const byte FlagIsDead       = 1 << 2;
        private const byte FlagHasAnimator  = 1 << 3;
        private const byte FlagHasMoving    = 1 << 4;
        private const byte FlagHasAttack    = 1 << 5;
        private const byte FlagHasCowering  = 1 << 6;
        private const byte FlagHasHostCombatAction = 1 << 7;

        private const byte BoolMovingTrue   = 1 << 0;
        private const byte BoolAttackTrue   = 1 << 1;
        private const byte BoolCoweringTrue = 1 << 2;

        private const byte Flag2HasHostCombatAim = 1 << 0;
        private const byte Flag2HasHostCombatAnimatorStates = 1 << 1;
        private const byte Flag2HasAiIntent = 1 << 2;
        private const byte Flag2HasAiIntentLookAt = 1 << 3;

        // Phase 4.4.0-O: flags3 extends the bit-field without touching the existing flags bytes.
        private const byte Flag3HasEnemyIntent = 1 << 0;
        // Phase 5.4-C G: host-authoritative target identity.
        private const byte Flag3HasHostTarget = 1 << 1;

        public static void WriteBatch(NetDataWriter w, IReadOnlyList<NetGameplayEnemyStateSnapshot> snapshots)
        {
            WriteBatch(w, snapshots, 0, snapshots == null ? 0 : snapshots.Count);
        }

        public static void WriteBatch(NetDataWriter w, IReadOnlyList<NetGameplayEnemyStateSnapshot> snapshots, int offset, int requestedCount)
        {
            if (snapshots == null)
            {
                w.Put(WireVersion);
                w.Put(0);
                return;
            }

            if (offset < 0) offset = 0;
            if (offset > snapshots.Count) offset = snapshots.Count;

            int available = snapshots.Count - offset;
            int count = Math.Min(Math.Min(requestedCount, available), MaxSnapshotsPerPacket);
            if (count < 0) count = 0;

            w.Put(WireVersion);
            w.Put(count);
            if (count <= 0) return;

            var first = snapshots[offset];
            w.Put(first.SourcePeerId ?? "");
            w.Put(first.ChapterName ?? "");
            w.Put(first.LevelIndex);
            w.Put(first.HasLevelSeed);
            w.Put(first.LevelSeed);
            w.Put(first.SourceRevision);
            w.Put(first.SentAt);

            for (int i = 0; i < count; i++)
                WriteOneCompact(w, snapshots[offset + i]);
        }

        public static bool TryReadBatch(NetDataReader r, out List<NetGameplayEnemyStateSnapshot> snapshots)
        {
            snapshots = new List<NetGameplayEnemyStateSnapshot>();
            try
            {
                byte version = r.GetByte();
                if (version != WireVersion) return false;

                int count = r.GetInt();
                if (count < 0 || count > MaxSnapshotsPerPacket) return false;
                if (count == 0) return true;

                string sourcePeerId = r.GetString();
                string chapterName = r.GetString();
                int levelIndex = r.GetInt();
                bool hasLevelSeed = r.GetBool();
                int levelSeed = r.GetInt();
                int sourceRevision = r.GetInt();
                float sentAt = r.GetFloat();

                for (int i = 0; i < count; i++)
                {
                    if (!TryReadOneCompact(
                            r,
                            sourcePeerId,
                            chapterName,
                            levelIndex,
                            hasLevelSeed,
                            levelSeed,
                            sourceRevision,
                            sentAt,
                            out var snapshot))
                        return false;
                    snapshots.Add(snapshot);
                }

                return true;
            }
            catch
            {
                snapshots.Clear();
                return false;
            }
        }

        private static void WriteOneCompact(NetDataWriter w, NetGameplayEnemyStateSnapshot s)
        {
            w.Put(s.Sequence);
            w.Put(s.SpawnIndex);

            byte flags = 0;
            if (s.HasPosition) flags |= FlagHasPosition;
            if (s.HasRotationY) flags |= FlagHasRotationY;
            if (s.IsDead) flags |= FlagIsDead;
            if (s.HasAnimatorState) flags |= FlagHasAnimator;
            if (s.HasAnimatorMovingBool) flags |= FlagHasMoving;
            if (s.HasAnimatorAttackBool) flags |= FlagHasAttack;
            if (s.HasAnimatorCoweringBool) flags |= FlagHasCowering;
            if (s.HasHostCombatAction) flags |= FlagHasHostCombatAction;
            byte flags2 = 0;
            if (s.HasHostCombatAim) flags2 |= Flag2HasHostCombatAim;
            if (s.HostCombatAnimatorStateCount > 0) flags2 |= Flag2HasHostCombatAnimatorStates;
            if (s.HasAiIntent) flags2 |= Flag2HasAiIntent;
            if (s.HasAiIntentLookAt) flags2 |= Flag2HasAiIntentLookAt;
            byte flags3 = 0;
            if (s.HasEnemyIntent) flags3 |= Flag3HasEnemyIntent;
            if (s.HasHostTarget) flags3 |= Flag3HasHostTarget;
            w.Put(flags);
            w.Put(flags2);
            w.Put(flags3);

            if (s.HasPosition)
            {
                w.Put(s.Position.x);
                w.Put(s.Position.y);
                w.Put(s.Position.z);
            }

            if (s.HasRotationY)
                w.Put(s.RotationY);

            if (s.HasAnimatorState)
            {
                int layer = s.AnimatorLayer;
                if (layer < 0) layer = 0;
                if (layer > 255) layer = 255;

                w.Put((byte)layer);
                w.Put(s.AnimatorFullPathHash);
                w.Put(s.AnimatorNormalizedTime);
                w.Put(s.AnimatorSpeed);

                byte boolValues = 0;
                if (s.HasAnimatorMovingBool && s.AnimatorMovingBool) boolValues |= BoolMovingTrue;
                if (s.HasAnimatorAttackBool && s.AnimatorAttackBool) boolValues |= BoolAttackTrue;
                if (s.HasAnimatorCoweringBool && s.AnimatorCoweringBool) boolValues |= BoolCoweringTrue;
                w.Put(boolValues);
            }

            if (s.HasHostCombatAction)
            {
                int kind = s.HostCombatActionKind;
                if (kind < 0) kind = 0;
                if (kind > 255) kind = 255;
                w.Put((byte)kind);
                w.Put(s.HostCombatActionState);
                w.Put(s.HostCombatActionSequence);
            }

            if (s.HasHostCombatAim)
            {
                w.Put(s.HostCombatOriginPosition.x);
                w.Put(s.HostCombatOriginPosition.y);
                w.Put(s.HostCombatOriginPosition.z);
                w.Put(s.HostCombatAimPosition.x);
                w.Put(s.HostCombatAimPosition.y);
                w.Put(s.HostCombatAimPosition.z);
            }

            if (s.HasAiIntent)
            {
                int kind = s.AiIntentKind;
                if (kind < 0) kind = 0;
                if (kind > 255) kind = 255;
                w.Put(s.AiIntentSequence);
                w.Put((byte)kind);
                w.Put(s.AiIntentDestination.x);
                w.Put(s.AiIntentDestination.y);
                w.Put(s.AiIntentDestination.z);
            }

            if (s.HasAiIntentLookAt)
            {
                w.Put(s.AiIntentLookAt.x);
                w.Put(s.AiIntentLookAt.y);
                w.Put(s.AiIntentLookAt.z);
            }

            if (s.HostCombatAnimatorStateCount > 0)
            {
                int count = s.HostCombatAnimatorStateCount;
                if (count < 0) count = 0;
                if (count > 4) count = 4;
                w.Put((byte)count);
                for (int i = 0; i < count; i++)
                {
                    int layer = s.HostCombatAnimatorLayers[i];
                    if (layer < 0) layer = 0;
                    if (layer > 255) layer = 255;
                    w.Put(s.HostCombatAnimatorPathHashes[i]);
                    w.Put((byte)layer);
                    w.Put(s.HostCombatAnimatorFullPathHashes[i]);
                    w.Put(s.HostCombatAnimatorNormalizedTimes[i]);
                    w.Put(s.HostCombatAnimatorSpeeds[i]);
                }
            }

            if (s.HasEnemyIntent)
            {
                int kind = s.EnemyIntentKind; if (kind < 0) kind = 0; if (kind > 255) kind = 255;
                w.Put((byte)kind);
                w.Put(s.EnemyIntentSequence);
                w.Put(s.EnemyIntentDuration);
                w.Put(s.EnemyIntentWeaponActionState);
                byte posFlags = 0;
                if (s.EnemyIntentHasTargetPosition) posFlags |= 1;
                if (s.EnemyIntentHasAimPosition) posFlags |= 2;
                if (s.EnemyIntentHasOriginPosition) posFlags |= 4;
                w.Put(posFlags);
                if (s.EnemyIntentHasTargetPosition) { w.Put(s.EnemyIntentTargetPosition.x); w.Put(s.EnemyIntentTargetPosition.y); w.Put(s.EnemyIntentTargetPosition.z); }
                if (s.EnemyIntentHasAimPosition) { w.Put(s.EnemyIntentAimPosition.x); w.Put(s.EnemyIntentAimPosition.y); w.Put(s.EnemyIntentAimPosition.z); }
                if (s.EnemyIntentHasOriginPosition) { w.Put(s.EnemyIntentOriginPosition.x); w.Put(s.EnemyIntentOriginPosition.y); w.Put(s.EnemyIntentOriginPosition.z); }
            }

            if (s.HasHostTarget)
            {
                w.Put(s.HostTargetKind);
                w.Put(s.HostTargetPosition.x);
                w.Put(s.HostTargetPosition.y);
                w.Put(s.HostTargetPosition.z);
            }
        }

        private static bool TryReadOneCompact(
            NetDataReader r,
            string sourcePeerId,
            string chapterName,
            int levelIndex,
            bool hasLevelSeed,
            int levelSeed,
            int sourceRevision,
            float sentAt,
            out NetGameplayEnemyStateSnapshot s)
        {
            s = new NetGameplayEnemyStateSnapshot();
            try
            {
                s.Sequence = r.GetInt();
                s.SourcePeerId = sourcePeerId;
                s.ChapterName = chapterName;
                s.LevelIndex = levelIndex;
                s.HasLevelSeed = hasLevelSeed;
                s.LevelSeed = levelSeed;
                s.SourceRevision = sourceRevision;
                s.SentAt = sentAt;

                s.SpawnIndex = r.GetInt();
                s.Category = "Npc";
                s.UnitIdentifier = "";
                s.ActorName = s.SpawnIndex > 0 ? $"Npc#{s.SpawnIndex}" : "Npc";

                byte flags = r.GetByte();
                byte flags2 = r.GetByte();
                byte flags3 = r.GetByte();

                s.HasPosition = (flags & FlagHasPosition) != 0;
                if (s.HasPosition)
                    s.Position = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());

                s.HasRotationY = (flags & FlagHasRotationY) != 0;
                if (s.HasRotationY)
                    s.RotationY = r.GetFloat();

                s.IsDead = (flags & FlagIsDead) != 0;

                s.HasAnimatorState = (flags & FlagHasAnimator) != 0;
                if (s.HasAnimatorState)
                {
                    s.AnimatorLayer = r.GetByte();
                    s.AnimatorFullPathHash = r.GetInt();
                    s.AnimatorShortNameHash = 0;
                    s.AnimatorNormalizedTime = r.GetFloat();
                    s.AnimatorSpeed = r.GetFloat();

                    byte boolValues = r.GetByte();
                    s.HasAnimatorMovingBool = (flags & FlagHasMoving) != 0;
                    s.AnimatorMovingBool = (boolValues & BoolMovingTrue) != 0;
                    s.HasAnimatorAttackBool = (flags & FlagHasAttack) != 0;
                    s.AnimatorAttackBool = (boolValues & BoolAttackTrue) != 0;
                    s.HasAnimatorCoweringBool = (flags & FlagHasCowering) != 0;
                    s.AnimatorCoweringBool = (boolValues & BoolCoweringTrue) != 0;
                }

                s.HasHostCombatAction = (flags & FlagHasHostCombatAction) != 0;
                if (s.HasHostCombatAction)
                {
                    s.HostCombatActionKind = r.GetByte();
                    s.HostCombatActionState = r.GetInt();
                    s.HostCombatActionSequence = r.GetInt();
                }

                s.HasHostCombatAim = (flags2 & Flag2HasHostCombatAim) != 0;
                if (s.HasHostCombatAim)
                {
                    s.HostCombatOriginPosition = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    s.HostCombatAimPosition = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                }

                s.HasAiIntent = (flags2 & Flag2HasAiIntent) != 0;
                if (s.HasAiIntent)
                {
                    s.AiIntentSequence = r.GetInt();
                    s.AiIntentKind = r.GetByte();
                    s.AiIntentDestination = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                }

                s.HasAiIntentLookAt = (flags2 & Flag2HasAiIntentLookAt) != 0;
                if (s.HasAiIntentLookAt)
                    s.AiIntentLookAt = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());

                if ((flags2 & Flag2HasHostCombatAnimatorStates) != 0)
                {
                    int count = r.GetByte();
                    if (count < 0 || count > 4) return false;
                    s.HostCombatAnimatorStateCount = count;
                    for (int i = 0; i < count; i++)
                    {
                        s.HostCombatAnimatorPathHashes[i] = r.GetInt();
                        s.HostCombatAnimatorLayers[i] = r.GetByte();
                        s.HostCombatAnimatorFullPathHashes[i] = r.GetInt();
                        s.HostCombatAnimatorNormalizedTimes[i] = r.GetFloat();
                        s.HostCombatAnimatorSpeeds[i] = r.GetFloat();
                    }
                }

                s.HasEnemyIntent = (flags3 & Flag3HasEnemyIntent) != 0;
                if (s.HasEnemyIntent)
                {
                    s.EnemyIntentKind = r.GetByte();
                    s.EnemyIntentSequence = r.GetInt();
                    s.EnemyIntentDuration = r.GetFloat();
                    s.EnemyIntentWeaponActionState = r.GetInt();
                    byte posFlags = r.GetByte();
                    s.EnemyIntentHasTargetPosition = (posFlags & 1) != 0;
                    if (s.EnemyIntentHasTargetPosition) s.EnemyIntentTargetPosition = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    s.EnemyIntentHasAimPosition = (posFlags & 2) != 0;
                    if (s.EnemyIntentHasAimPosition) s.EnemyIntentAimPosition = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    s.EnemyIntentHasOriginPosition = (posFlags & 4) != 0;
                    if (s.EnemyIntentHasOriginPosition) s.EnemyIntentOriginPosition = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                }

                s.HasHostTarget = (flags3 & Flag3HasHostTarget) != 0;
                if (s.HasHostTarget)
                {
                    s.HostTargetKind = r.GetByte();
                    s.HostTargetPosition = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                }

                return true;
            }
            catch
            {
                s = new NetGameplayEnemyStateSnapshot();
                return false;
            }
        }
    }
}
