using System.Collections.Generic;
using System.Linq;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Tracks local and remote run/scene metadata.
    /// It only detects mismatch and formats diagnostics; it never corrects or loads scenes.
    /// </summary>
    public sealed class NetRunStateManager
    {
        private readonly Dictionary<string, NetRunState> _remoteStates = new Dictionary<string, NetRunState>();
        private readonly Dictionary<string, string> _lastMismatchKeys = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _lastAuthorityKeys = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _lastStateNoticeKeys = new Dictionary<string, string>();

        public NetRunState LocalState { get; private set; } = new NetRunState();
        public IReadOnlyCollection<NetRunState> RemoteStates => _remoteStates.Values.ToList().AsReadOnly();

        public void Clear()
        {
            LocalState = new NetRunState();
            _remoteStates.Clear();
            _lastMismatchKeys.Clear();
            _lastAuthorityKeys.Clear();
            _lastStateNoticeKeys.Clear();
        }

        public void SetLocalIdentity(string peerId, string playerName)
        {
            LocalState.PeerId = string.IsNullOrWhiteSpace(peerId) ? LocalState.PeerId : peerId;
            LocalState.PlayerName = string.IsNullOrWhiteSpace(playerName) ? LocalState.PlayerName : playerName;
        }

        public NetRunState UpdateLocalGoToLevel(string chapterName, int levelIndex, string loadingMode, string spawnIdentifier, float now)
        {
            LocalState.ChapterName     = Clean(chapterName, "<unknown>");
            LocalState.LevelIndex      = levelIndex;
            LocalState.LoadingMode     = Clean(loadingMode, "");
            LocalState.SpawnIdentifier = Clean(spawnIdentifier, "");
            LocalState.HasLevelSeed    = false;
            LocalState.LevelSeed       = 0;
            LocalState.LevelGenerator  = "";
            LocalState.LastUpdatedAt   = now;
            LocalState.Revision++;
            return LocalState.Clone();
        }

        public NetRunState UpdateLocalGameState(string gameState, float now)
        {
            LocalState.GameState     = Clean(gameState, "<unknown>");
            LocalState.LastUpdatedAt = now;
            LocalState.Revision++;
            return LocalState.Clone();
        }

        public NetRunState UpdateLocalClearLevel(float now)
        {
            LocalState.LastUpdatedAt = now;
            LocalState.HasLevelSeed = false;
            LocalState.LevelSeed = 0;
            LocalState.LevelGenerator = "";
            LocalState.Revision++;
            return LocalState.Clone();
        }

        /// <summary>
        /// Phase 5.3-M P0-A/B: overwrite the local run identity from a FINALIZED generation-input snapshot.
        /// On the Host this is the only path that corrects the stale GoToLevel sub-level (e.g. Act_01_Caves:0)
        /// to the real generated cave (Act_01_Caves:1 graph=Caves2). On the Client it reinforces the
        /// host-driven load. Only bumps Revision when something actually changed (no rebroadcast spam).
        /// Returns true when the local state changed, plus whether the LEVEL specifically was corrected.
        /// </summary>
        public bool ApplyFinalizedGenerationSnapshot(string chapter, int levelIndex, bool hasSeed, int seed, string graphName, float now, out bool levelCorrected, out NetRunState updated)
        {
            bool changed = false;
            levelCorrected = false;

            if (!string.IsNullOrWhiteSpace(chapter) && chapter != "<unknown>" && LocalState.ChapterName != chapter)
            {
                LocalState.ChapterName = Clean(chapter, "<unknown>");
                changed = true;
            }
            if (levelIndex >= 0 && LocalState.LevelIndex != levelIndex)
            {
                LocalState.LevelIndex = levelIndex;
                changed = true;
                levelCorrected = true;
            }
            if (hasSeed && (!LocalState.HasLevelSeed || LocalState.LevelSeed != seed))
            {
                LocalState.HasLevelSeed = true;
                LocalState.LevelSeed = seed;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(graphName) && LocalState.LevelGenerator != graphName)
            {
                LocalState.LevelGenerator = Clean(graphName, "");
                changed = true;
            }

            if (changed)
            {
                LocalState.LastUpdatedAt = now;
                LocalState.Revision++;
            }
            updated = LocalState.Clone();
            return changed;
        }

        public NetRunState UpdateLocalLevelSeed(int seed, string generatorName, float now)
        {
            LocalState.HasLevelSeed = true;
            LocalState.LevelSeed = seed;
            LocalState.LevelGenerator = Clean(generatorName, "");
            LocalState.LastUpdatedAt = now;
            LocalState.Revision++;
            return LocalState.Clone();
        }

        public void UpdateRemote(string peerId, NetRunState state, float now)
        {
            if (string.IsNullOrWhiteSpace(peerId)) peerId = state.PeerId;
            if (string.IsNullOrWhiteSpace(peerId)) peerId = "unknown";

            state.PeerId = peerId;
            state.LastUpdatedAt = now;
            _remoteStates[peerId] = state.Clone();
        }

        public void RemoveRemote(string peerId)
        {
            if (string.IsNullOrWhiteSpace(peerId)) return;
            _remoteStates.Remove(peerId);
            _lastMismatchKeys.Remove(peerId);
            _lastAuthorityKeys.Remove(peerId);
            _lastStateNoticeKeys.Remove(peerId);
        }

        public bool TryGetRemote(string peerId, out NetRunState state)
        {
            state = new NetRunState();
            if (string.IsNullOrWhiteSpace(peerId)) return false;
            if (!_remoteStates.TryGetValue(peerId, out var stored)) return false;
            state = stored.Clone();
            return true;
        }

        public bool TryBuildMismatchWarning(string peerId, out string warning)
        {
            warning = "";
            if (string.IsNullOrWhiteSpace(peerId)) return false;
            if (!_remoteStates.TryGetValue(peerId, out var remote)) return false;
            if (!LocalState.HasLevel || !remote.HasLevel) return false;

            bool requireSeed = RequireSameSeedForSceneMatch();
            if (LocalState.SameLevelInstanceAs(remote, requireSeed)) return false;
            if (IsTransientSceneComparison(LocalState, remote, requireSeed)) return false;

            string mismatchKey = $"scene|{LocalState.LevelInstanceKey()}|{remote.LevelInstanceKey()}";
            if (_lastMismatchKeys.TryGetValue(peerId, out var last) && last == mismatchKey)
                return false;

            _lastMismatchKeys[peerId] = mismatchKey;
            warning = $"[RunState] Scene mismatch with {peerId}: local={LocalState.ToCompactString()} remote={remote.ToCompactString()}";
            return true;
        }

        public bool TryBuildStateDifferenceNotice(string peerId, out string notice)
        {
            notice = "";
            if (string.IsNullOrWhiteSpace(peerId)) return false;
            if (!_remoteStates.TryGetValue(peerId, out var remote)) return false;
            if (!LocalState.HasLevel || !remote.HasLevel) return false;
            if (!LocalState.SameSceneAs(remote)) return false;
            if (!LocalState.IsStableGameState || !remote.IsStableGameState) return false;
            if (LocalState.SameStableStateAs(remote)) return false;

            string noticeKey = $"state|{LocalState.CompareKey()}|{remote.CompareKey()}";
            if (_lastStateNoticeKeys.TryGetValue(peerId, out var last) && last == noticeKey)
                return false;

            _lastStateNoticeKeys[peerId] = noticeKey;
            notice = $"[RunState] Same scene but different stable GameState with {peerId}: local={LocalState.ToCompactString()} remote={remote.ToCompactString()}";
            return true;
        }

        public bool TryBuildHostAuthorityWarning(out string warning)
        {
            warning = "";
            if (!_remoteStates.TryGetValue("host", out var host)) return false;
            if (!LocalState.HasLevel || !host.HasLevel) return false;

            bool requireSeed = RequireSameSeedForSceneMatch();
            if (LocalState.SameLevelInstanceAs(host, requireSeed)) return false;
            if (IsTransientSceneComparison(LocalState, host, requireSeed)) return false;

            string key = $"client-authority|{LocalState.LevelInstanceKey()}|{host.LevelInstanceKey()}";
            if (_lastAuthorityKeys.TryGetValue("host", out var last) && last == key)
                return false;

            _lastAuthorityKeys["host"] = key;
            warning = $"[SceneAuthority] Client is not in host scene: local={LocalState.ToCompactString()} host={host.ToCompactString()} action=manual-only";
            return true;
        }

        public bool TryBuildClientSceneDriftWarning(string peerId, out string warning)
        {
            warning = "";
            if (string.IsNullOrWhiteSpace(peerId)) return false;
            if (!_remoteStates.TryGetValue(peerId, out var client)) return false;
            if (!LocalState.HasLevel || !client.HasLevel) return false;

            bool requireSeed = RequireSameSeedForSceneMatch();
            if (LocalState.SameLevelInstanceAs(client, requireSeed)) return false;
            if (IsTransientSceneComparison(LocalState, client, requireSeed)) return false;

            string key = $"host-authority|{LocalState.LevelInstanceKey()}|{client.LevelInstanceKey()}";
            if (_lastAuthorityKeys.TryGetValue(peerId, out var last) && last == key)
                return false;

            _lastAuthorityKeys[peerId] = key;
            warning = $"[SceneAuthority] Client {peerId} is not in host scene: host={LocalState.ToCompactString()} client={client.ToCompactString()} action=observe-only";
            return true;
        }

        public string FormatStatus()
        {
            string local = LocalState.Revision <= 0 ? "local=<unknown>" : $"local={LocalState.ToCompactString()}";
            if (_remoteStates.Count == 0) return $"run:{local} remotes=0";

            var parts = _remoteStates.Values
                .OrderBy(s => s.PeerId)
                .Select(s => s.ToCompactString());
            return $"run:{local} remotes={_remoteStates.Count} [{string.Join("; ", parts)}]";
        }

        public string FormatAuthorityStatus(NetMode mode)
        {
            if (mode == NetMode.Host)
            {
                int drift = 0;
                if (LocalState.HasLevel)
                {
                    bool requireSeed = RequireSameSeedForSceneMatch();
                    drift = _remoteStates.Values.Count(s =>
                        s.HasLevel
                        && !LocalState.SameLevelInstanceAs(s, requireSeed)
                        && !IsTransientSceneComparison(LocalState, s, requireSeed));
                }
                return $"authority=HostScene localReady={LocalState.HasLevel} clientDrift={drift}";
            }

            if (mode == NetMode.Client)
            {
                bool hostKnown = _remoteStates.TryGetValue("host", out var host) && host.HasLevel;
                bool inHostScene = hostKnown && LocalState.HasLevel && LocalState.SameLevelInstanceAs(host!, RequireSameSeedForSceneMatch());
                return $"authority=HostScene hostKnown={hostKnown} inHostScene={inHostScene}";
            }

            return "authority=Off";
        }

        /// <summary>Phase PF-0: compact local-vs-remote scene+seed convergence summary for the boss pre-fight probe.
        /// <paramref name="allConverged"/> is true only when at least one remote is known AND every known remote shares
        /// the local level instance (same scene + same seed when seed authority is on). Read-only.</summary>
        public string FormatBossConvergence(out bool allConverged, out int peerCount, out int convergedCount)
        {
            bool requireSeed = RequireSameSeedForSceneMatch();
            peerCount = 0; convergedCount = 0;
            var parts = new List<string>();
            foreach (var remote in _remoteStates.Values)
            {
                peerCount++;
                bool same = LocalState.HasLevel && remote.HasLevel && LocalState.SameLevelInstanceAs(remote, requireSeed);
                bool seedSplit = LocalState.HasKnownSeedMismatch(remote);
                if (same) convergedCount++;
                string tag = same ? "OK" : (seedSplit ? "SEED-SPLIT" : (remote.HasLevel ? "DIFF-SCENE" : "NO-LEVEL"));
                parts.Add($"{remote.ToCompactString()}=>{tag}");
            }
            allConverged = peerCount > 0 && convergedCount == peerCount;
            string remotes = parts.Count == 0 ? "<no-peers>" : string.Join(" ", parts);
            return $"local={LocalState.ToCompactString()} requireSeed={requireSeed} converged={convergedCount}/{peerCount} | {remotes}";
        }

        private static bool RequireSameSeedForSceneMatch()
        {
            try
            {
                return SULFURTogether.Plugin.Cfg.EnableLevelSeedAuthority.Value
                    && SULFURTogether.Plugin.Cfg.RequireSameLevelSeedForSceneMatch.Value;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTransientSceneComparison(NetRunState left, NetRunState right, bool requireSeed)
        {
            if (left.IsLoadingLikeState || right.IsLoadingLikeState) return true;
            if (!requireSeed) return false;

            // While either side has not captured a seed yet, the peers are not proven
            // compatible, but this is usually a short loading/capture window. Avoid
            // warning spam until both seeds are known and a real drift remains.
            return !left.HasLevelSeed || !right.HasLevelSeed;
        }

        private static string Clean(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            value = value.Trim();
            return value.Length > 64 ? value.Substring(0, 64) : value;
        }
    }
}
