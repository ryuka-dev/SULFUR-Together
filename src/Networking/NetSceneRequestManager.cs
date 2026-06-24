using System.Collections.Generic;
using System.Linq;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 2.5+ HostSceneRequest / ClientSceneAck bookkeeping.
    /// It never changes gameplay state by itself. Manual follow is triggered separately.
    /// </summary>
    public sealed class NetSceneRequestManager
    {
        private readonly Dictionary<string, string> _lastRequestKeys = new Dictionary<string, string>();
        private readonly Dictionary<string, float> _lastRequestTimes = new Dictionary<string, float>();
        private readonly Dictionary<string, NetHostSceneRequest> _pendingByPeer = new Dictionary<string, NetHostSceneRequest>();
        private readonly Dictionary<string, NetClientSceneResponse> _lastResponses = new Dictionary<string, NetClientSceneResponse>();
        private readonly Dictionary<string, string> _sentClientResponsePhases = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _lastFollowPhaseByPeer = new Dictionary<string, string>();
        private readonly List<NetClientSceneResponse> _responseHistory = new List<NetClientSceneResponse>();

        public NetHostSceneRequest? LastReceivedHostRequest { get; private set; }
        public NetHostSceneRequest? LastManualFollowRequest { get; private set; }
        public NetClientSceneResponse? LastSentClientResponse { get; private set; }

        public void Clear()
        {
            _lastRequestKeys.Clear();
            _lastRequestTimes.Clear();
            _pendingByPeer.Clear();
            _lastResponses.Clear();
            _sentClientResponsePhases.Clear();
            _lastFollowPhaseByPeer.Clear();
            _responseHistory.Clear();
            LastReceivedHostRequest = null;
            LastManualFollowRequest = null;
            LastSentClientResponse = null;
        }

        public void RemovePeer(string peerId)
        {
            if (string.IsNullOrWhiteSpace(peerId)) return;
            _lastRequestKeys.Remove(peerId);
            _lastRequestTimes.Remove(peerId);
            _pendingByPeer.Remove(peerId);
            _lastResponses.Remove(peerId);
            _lastFollowPhaseByPeer.Remove(peerId);
        }

        public bool TryCreateHostSceneRequest(
            string clientPeerId,
            string hostPlayerName,
            NetRunState hostState,
            NetRunState clientState,
            float now,
            float minIntervalSeconds,
            out NetHostSceneRequest request)
        {
            request = new NetHostSceneRequest();
            if (string.IsNullOrWhiteSpace(clientPeerId)) return false;
            // Phase 5.3-J: the host must also send to a client that has NO level yet — that is exactly the
            // Client load-gate "waiting for host generation input" state. Only the host's level is required.
            if (!hostState.HasLevel) return false;
            bool requireSeed = RequireSameSeedForSceneMatch();
            bool sameChapterLevel = NetSceneName.SameScene(
                hostState.ChapterName,
                hostState.LevelIndex,
                clientState.ChapterName,
                clientState.LevelIndex);

            // Phase 5.4-B: hub / safezone / menu targets are not seed-generated, so the seed gate (and seed-based
            // instance comparison) must not apply — otherwise the host can never ask a client to return to the hub.
            bool combatTarget = NetClientLoadGate.IsCombatTarget(hostState.ChapterName, hostState.LoadingMode, hostState.LevelIndex);

            if (requireSeed && combatTarget)
            {
                if (!hostState.HasLevelSeed) return false;

                // If both sides already report the same chapter/level but the client
                // has not captured its seed yet, wait for the next RunState update.
                // Sending a request here only produces a transient ClientSceneRefused
                // during normal loading. If the chapter/level differs, still request
                // immediately because the client is clearly in the wrong scene.
                if (sameChapterLevel && !clientState.HasLevelSeed) return false;
            }

            bool sameInstance = combatTarget
                ? hostState.SameLevelInstanceAs(clientState, requireSeed)
                : hostState.SameSceneAs(clientState);
            if (sameInstance) return false;

            if (minIntervalSeconds < 1f) minIntervalSeconds = 1f;
            string key = $"{hostState.LevelInstanceKey()}|hostRev={hostState.Revision}|client={clientState.LevelInstanceKey()}";

            if (_lastRequestKeys.TryGetValue(clientPeerId, out var lastKey)
                && lastKey == key
                && _lastRequestTimes.TryGetValue(clientPeerId, out var lastTime)
                && now - lastTime < minIntervalSeconds)
            {
                return false;
            }

            string requestId = $"hsr-{clientPeerId}-{hostState.LevelInstanceKey().Replace(':', '-').Replace('#', '-').Replace('=', '-')}-r{hostState.Revision}-{(int)(now * 1000f)}";
            request = new NetHostSceneRequest
            {
                RequestId       = requestId,
                HostPeerId      = string.IsNullOrWhiteSpace(hostState.PeerId) ? "host" : hostState.PeerId,
                HostPlayerName  = string.IsNullOrWhiteSpace(hostPlayerName) ? hostState.PlayerName : hostPlayerName,
                ChapterName     = hostState.ChapterName,
                LevelIndex      = hostState.LevelIndex,
                LoadingMode     = hostState.LoadingMode,
                SpawnIdentifier = hostState.SpawnIdentifier,
                HostGameState   = hostState.GameState,
                HasLevelSeed    = hostState.HasLevelSeed,
                LevelSeed       = hostState.LevelSeed,
                LevelGenerator  = hostState.LevelGenerator,
                GraphName       = hostState.LevelGenerator, // 5.4-D-0: lets the send path classify hub vs combat
                HostRevision    = hostState.Revision,
                Reason          = "ClientSceneDrift",
                AutoLoadAllowed = false,
            };

            _lastRequestKeys[clientPeerId] = key;
            _lastRequestTimes[clientPeerId] = now;
            _pendingByPeer[clientPeerId] = request;
            return true;
        }

        public void RecordHostRequest(NetHostSceneRequest request)
        {
            LastReceivedHostRequest = request;
        }

        public void RecordManualFollowAttempt(NetHostSceneRequest request)
        {
            LastManualFollowRequest = request;
        }

        public bool HasLastHostRequestTarget(out NetHostSceneRequest request)
        {
            request = LastReceivedHostRequest ?? new NetHostSceneRequest();
            return LastReceivedHostRequest != null && LastReceivedHostRequest.HasTargetScene;
        }

        public bool IsLastHostRequest(NetHostSceneRequest request)
        {
            if (LastReceivedHostRequest == null) return false;
            if (request == null) return false;
            return !string.IsNullOrWhiteSpace(request.RequestId)
                && request.RequestId == LastReceivedHostRequest.RequestId;
        }

        public NetClientSceneResponse BuildClientResponse(NetHostSceneRequest request, NetRunState localState, string clientPeerId, string clientPlayerName, string? messageOverride = null, string? followPhaseOverride = null)
        {
            bool sameChapterLevel = request.HasTargetScene
                && localState.HasLevel
                && NetSceneName.SameScene(localState.ChapterName, localState.LevelIndex, request.ChapterName, request.LevelIndex);
            bool requireSeed = RequireSameSeedForSceneMatch();
            bool seedMatches = !requireSeed
                || (request.HasLevelSeed && localState.HasLevelSeed && request.LevelSeed == localState.LevelSeed);

            // Phase 5.4-D-1 P0-G: when both sides know the graph, it MUST match. Otherwise a wrong-run follow
            // (request graph=Hedgemaze, local graph=Sewers1) with the same chapter:level:seed is falsely accepted
            // as inTarget. Only enforced when both graphs are known so it never blocks acks during loading.
            bool graphKnownBoth = !string.IsNullOrWhiteSpace(request.GraphName) && !string.IsNullOrWhiteSpace(localState.LevelGenerator);
            bool graphMatches = !graphKnownBoth
                || string.Equals(request.GraphName, localState.LevelGenerator, System.StringComparison.OrdinalIgnoreCase);
            if (graphKnownBoth)
            {
                if (graphMatches) NetSceneFollowDiag.SceneAckGraphMatched++;
                else
                {
                    NetSceneFollowDiag.SceneAckGraphMismatch++;
                    SULFURTogether.Plugin.Log.Info($"[SceneRequest] not in target graph request={request.GraphName} local={localState.LevelGenerator}");
                }
            }

            bool inTargetScene = sameChapterLevel && seedMatches && graphMatches;

            string phase = string.IsNullOrWhiteSpace(followPhaseOverride)
                ? (inTargetScene ? "Arrived" : "Refused")
                : followPhaseOverride!.Trim();

            return new NetClientSceneResponse
            {
                RequestId        = request.RequestId,
                ClientPeerId     = string.IsNullOrWhiteSpace(clientPeerId) ? localState.PeerId : clientPeerId,
                ClientPlayerName = string.IsNullOrWhiteSpace(clientPlayerName) ? localState.PlayerName : clientPlayerName,
                ChapterName      = localState.ChapterName,
                LevelIndex       = localState.LevelIndex,
                GameState        = localState.GameState,
                HasLevelSeed     = localState.HasLevelSeed,
                LevelSeed        = localState.LevelSeed,
                LevelGenerator   = localState.LevelGenerator,
                LocalRevision    = localState.Revision,
                IsInTargetScene  = inTargetScene,
                FollowPhase      = phase,
                Message          = string.IsNullOrWhiteSpace(messageOverride)
                    ? (inTargetScene
                        ? "Arrived in requested host scene."
                        : "Automatic scene follow is not implemented; press the manual follow key to attempt it.")
                    : messageOverride!,
            };
        }

        public bool TryRecordSentClientResponse(NetClientSceneResponse response, out string reason)
        {
            reason = "";
            string requestId = string.IsNullOrWhiteSpace(response.RequestId) ? "<no-request>" : response.RequestId;
            string phase = string.IsNullOrWhiteSpace(response.FollowPhase)
                ? (response.IsInTargetScene ? "Arrived" : "Refused")
                : response.FollowPhase.Trim();
            string key = requestId + "|" + phase;

            if (_sentClientResponsePhases.ContainsKey(key))
            {
                reason = $"duplicate response phase '{phase}' for request {requestId}";
                return false;
            }

            _sentClientResponsePhases[key] = phase;
            LastSentClientResponse = response;
            return true;
        }

        public void RecordClientResponse(string peerId, NetClientSceneResponse response)
        {
            if (string.IsNullOrWhiteSpace(peerId)) peerId = response.ClientPeerId;
            if (string.IsNullOrWhiteSpace(peerId)) peerId = "unknown";
            _lastResponses[peerId] = response;
            _responseHistory.Add(response);

            string phase = string.IsNullOrWhiteSpace(response.FollowPhase)
                ? (response.IsInTargetScene ? "Arrived" : "Refused")
                : response.FollowPhase.Trim();
            _lastFollowPhaseByPeer[peerId] = phase;

            // Phase 2.5.1: pending means waiting for any response from the Client.
            // Phase 2.6.2 keeps richer follow phase history separately.
            _pendingByPeer.Remove(peerId);
        }

        public string FormatStatus()
        {
            int pending = _pendingByPeer.Count;
            int responses = _responseHistory.Count;

            if (LastReceivedHostRequest != null)
            {
                string manual = LastManualFollowRequest != null
                    ? $" manualFollow={LastManualFollowRequest.TargetSceneKey()}"
                    : "";
                string sent = LastSentClientResponse != null
                    ? $" lastSent={CleanPhase(LastSentClientResponse.FollowPhase, LastSentClientResponse.IsInTargetScene)}"
                    : "";
                return $"sceneReq:lastHost={LastReceivedHostRequest.TargetSceneKey()} autoLoad={LastReceivedHostRequest.AutoLoadAllowed}{manual}{sent}";
            }

            if (pending == 0 && responses == 0)
                return "sceneReq:pending=0 responses=0";

            string lastResponse = "";
            if (responses > 0)
            {
                var last = _responseHistory[_responseHistory.Count - 1];
                string phase = CleanPhase(last.FollowPhase, last.IsInTargetScene);
                lastResponse = $" lastResponse={last.ClientPeerId}:{phase}";
            }

            string follow = "";
            if (_lastFollowPhaseByPeer.Count > 0)
            {
                var parts = _lastFollowPhaseByPeer.OrderBy(kv => kv.Key).Select(kv => kv.Key + ":" + kv.Value);
                follow = " lastFollow=[" + string.Join(",", parts) + "]";
            }

            return $"sceneReq:pending={pending} responses={responses}{lastResponse}{follow}";
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

        private static string CleanPhase(string phase, bool inTarget)
        {
            if (!string.IsNullOrWhiteSpace(phase)) return phase.Trim();
            return inTarget ? "Arrived" : "Refused";
        }
    }
}
