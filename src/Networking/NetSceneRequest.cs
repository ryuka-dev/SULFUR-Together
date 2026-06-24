using System.Collections.Generic;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 2.5 host scene request metadata.
    /// This is a protocol skeleton only. Receiving this request must never load a scene by itself.
    /// </summary>
    public sealed class NetHostSceneRequest
    {
        public string RequestId       { get; set; } = "";
        public string HostPeerId      { get; set; } = "host";
        public string HostPlayerName  { get; set; } = "Host";
        public string ChapterName     { get; set; } = "<unknown>";
        public int    LevelIndex      { get; set; } = -1;
        public string LoadingMode     { get; set; } = "";
        public string SpawnIdentifier { get; set; } = "";
        public string HostGameState   { get; set; } = "<unknown>";
        public bool   HasLevelSeed    { get; set; }
        public int    LevelSeed       { get; set; }
        public string LevelGenerator  { get; set; } = "";
        public int    HostRevision    { get; set; }
        public string Reason          { get; set; } = "HostSceneAuthority";
        public bool   AutoLoadAllowed { get; set; }

        // Phase 5.3-I: deterministic generation-input exclusion sets captured from the Host's GameManager
        // at level entry. HasUsedSets=true means the Host explicitly sent these (even if empty, count=0),
        // so the Client must overwrite its local sets rather than keep potentially-contaminated values.
        public bool         HasUsedSets               { get; set; }
        public List<string> UsedChunksThisRun         { get; set; } = new List<string>();
        public List<string> UsedEventsThisRun         { get; set; } = new List<string>();
        public List<string> UsedEventsThisEnvironment { get; set; } = new List<string>();

        // Phase 5.3-J P0-6: graph / MakerSet name + run id, for verifying Host vs Client are on the same
        // generator (e.g. Host graph=Caves2 vs Client graph=Caves1). Diagnostic; not used to drive the game.
        public string GraphName       { get; set; } = "";
        public string GenerationRunId { get; set; } = "";

        public NetHostUsedSets ToUsedSets() => new NetHostUsedSets
        {
            Captured = HasUsedSets,
            UsedChunksThisRun         = new List<string>(UsedChunksThisRun ?? new List<string>()),
            UsedEventsThisRun         = new List<string>(UsedEventsThisRun ?? new List<string>()),
            UsedEventsThisEnvironment = new List<string>(UsedEventsThisEnvironment ?? new List<string>()),
        };

        public void SetUsedSets(NetHostUsedSets sets)
        {
            if (sets == null) { HasUsedSets = false; return; }
            HasUsedSets = true;
            UsedChunksThisRun         = new List<string>(sets.UsedChunksThisRun ?? new List<string>());
            UsedEventsThisRun         = new List<string>(sets.UsedEventsThisRun ?? new List<string>());
            UsedEventsThisEnvironment = new List<string>(sets.UsedEventsThisEnvironment ?? new List<string>());
        }

        public bool HasTargetScene => !string.IsNullOrWhiteSpace(ChapterName) && ChapterName != "<unknown>" && LevelIndex >= 0;

        public string TargetSceneKey()
        {
            string chapter = string.IsNullOrWhiteSpace(ChapterName) ? "<unknown>" : ChapterName.Trim();
            return $"{chapter}:{LevelIndex}";
        }

        public string ToCompactString()
        {
            string id = string.IsNullOrWhiteSpace(RequestId) ? "?" : RequestId;
            string host = string.IsNullOrWhiteSpace(HostPeerId) ? "host" : HostPeerId;
            string seed = HasLevelSeed ? $" seed={LevelSeed}" : " seed=?";
            return $"request={id} host={host} target={TargetSceneKey()}{seed} state={Clean(HostGameState)} rev={HostRevision} autoLoad={AutoLoadAllowed}";
        }

        private static string Clean(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<unknown>" : value.Trim();
        }
    }

    /// <summary>
    /// Phase 2.5 client response to a HostSceneRequest.
    /// Ack means the client is already in the target scene. Refused means automatic scene follow is not available yet.
    /// </summary>
    public sealed class NetClientSceneResponse
    {
        public string RequestId       { get; set; } = "";
        public string ClientPeerId    { get; set; } = "";
        public string ClientPlayerName { get; set; } = "";
        public string ChapterName     { get; set; } = "<unknown>";
        public int    LevelIndex      { get; set; } = -1;
        public string GameState       { get; set; } = "<unknown>";
        public bool   HasLevelSeed    { get; set; }
        public int    LevelSeed       { get; set; }
        public string LevelGenerator  { get; set; } = "";
        public int    LocalRevision   { get; set; }
        public bool   IsInTargetScene { get; set; }
        public string FollowPhase     { get; set; } = "None";
        public string Message         { get; set; } = "";

        public string SceneKey()
        {
            string chapter = string.IsNullOrWhiteSpace(ChapterName) ? "<unknown>" : ChapterName.Trim();
            return $"{chapter}:{LevelIndex}";
        }

        public string ToCompactString()
        {
            string id = string.IsNullOrWhiteSpace(RequestId) ? "?" : RequestId;
            string client = string.IsNullOrWhiteSpace(ClientPeerId) ? "?" : ClientPeerId;
            string seed = HasLevelSeed ? $" seed={LevelSeed}" : " seed=?";
            return $"request={id} client={client} scene={SceneKey()}{seed} state={Clean(GameState)} rev={LocalRevision} inTarget={IsInTargetScene} phase={Clean(FollowPhase)} msg='{Clean(Message)}'";
        }

        private static string Clean(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        }
    }
}
