using HarmonyLib;

namespace SULFURTogether.Patches
{
    public static class PatchBootstrap
    {
        public static void ApplyAll(Harmony harmony)
        {
            Plugin.Log.Info("[PatchBootstrap] Applying reverse probe patches...");
            ReverseProbePatches.Apply(harmony);
            AwaitStartLevelPatches.Apply(harmony);
            SeamlessStartLevelPatches.Apply(harmony);   // NP-3 — must follow AWAIT-3 (shares its MoveNext seam)
            RangedShootProbePatches.Apply(harmony);     // FH-6 — split the TriggerShoot stall (diagnostic)
            GhostSpawnPatches.Apply(harmony);
            CryptChallengePatches.Apply(harmony);
            CardinalPatches.Apply(harmony);             // BGC — Black Guild Cardinal alcove fight
            LevelGenTracePatches.Apply(harmony);
            BossEncounterPatches.Apply(harmony);
            Networking.Gameplay.Boss.BossMusicSync.Apply(harmony); // BM-1 — boss music cues reach every end
            RoomIsolationPatches.Apply(harmony);        // ISO-1 — a room isolation only for a player inside it
            BossSpawnPatches.Apply(harmony);
            CousinArmPatches.Apply(harmony);
            DeathSpawnSyncPatches.Apply(harmony);
            WeaponFirePatches.Apply(harmony);
            BreakablePatches.Apply(harmony);
            SapperMinePatches.Apply(harmony);           // SM-1 — a sapper's mines reach every end
            ThrowablePatches.Apply(harmony);
            TriggerSpawnPatches.Apply(harmony);
            MetalGatePatches.Apply(harmony);
            DoorBlockerPatches.Apply(harmony);
            OpenableDoorPatches.Apply(harmony);
            PickupPatches.Apply(harmony);
            LootRollSuppressionPatches.Apply(harmony);
            ChestPatches.Apply(harmony);
            LootablePatches.Apply(harmony);
            TargetDummyPatches.Apply(harmony);
            PauseControlPatches.Apply(harmony);
            HazardProbePatches.Apply(harmony);
            EndlessModeProbePatches.Apply(harmony);
            EndlessSyncPatches.Apply(harmony);
            EndlessBossBarFixPatches.Apply(harmony);
            GhostPlayerPatches.Apply(harmony);
            UnitStatusPatches.Apply(harmony);
            RemoteHitPatches.Apply(harmony);            // HR-1 — settle client hits through the real ReceiveDamage
            RemoteHitReactionPatches.Apply(harmony);    // HR-5 — enemy reactions to a player's hit see a client's hit too
            StandInCoverPatches.Apply(harmony);         // HR-7 — enemies take cover from a remote player too
            EnemyRevealPatches.Apply(harmony);          // HR-8 — a Snipa discovered on the host is revealed on clients
            DesertPosePatches.Apply(harmony);           // HR-9 — a Digga's burrow and a Sticka's spin are shown on clients
            TeamMarkPatches.Apply(harmony);             // #26 team marks (the host's own hits) and the tougher-enemies balance settings
            EnemyRemovalPatches.Apply(harmony);         // RM-1 — a live unit the host destroys is removed on clients
            DesertPikeRiderPatches.Apply(harmony);      // PK-6 — a desert pike seats the same riders on every machine
            LocalTransitionPatches.Apply(harmony);      // JF-1 — a client starts a host follow only between levels, not mid-generation
            CopyDamagePatches.Apply(harmony);           // ED-2 — a copy's hit on the client's own player defers to the host's forwarded hit
            ForwardedHitPatches.Apply(harmony);         // ED-3 — enemy explosions resolved on the client; forwarded hits parryable
            Plugin.Log.Info("[PatchBootstrap] Done.");
        }
    }
}
