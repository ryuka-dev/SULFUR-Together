using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase 5.4-G4 (diagnostic-only): timing probe for WitchPhase2.InitPhase / ShowWitches.
    ///
    /// Per the Phase 2 source audit, the dome layout authority must be captured at <c>ShowWitches</c> (after the second
    /// shuffle), and the suspected first-round-invisible root is a TIMING race: the Client receives the Host phase
    /// revision, runs the base <c>StartPhase</c> (which arms a serialized <c>delayPhaseStart</c> before <c>InitPhase</c>
    /// runs <c>ShowWitches</c>); if the Host leaves Phase 2 quickly (LogOutput36: round1 ≈ 7.6s, round2 ≈ 23.8s) the
    /// Client's G2 phase-revision apply tears Phase 2 down before its local delay elapses, so <c>ShowWitches</c> never
    /// runs round 1. This probe nails that down before the full WitchPhase2Manifest is implemented. Read-only.
    /// </summary>
    internal static class WitchPhase2Probe
    {
        private static bool Enabled
        {
            get { try { return Plugin.Cfg.LogWitchPhase2Probe.Value; } catch { return false; } }
        }

        private static string Side()
        {
            try { return NetGameplaySyncBridge.BossMode == NetMode.Host ? "HOST" : (NetGameplaySyncBridge.BossMode == NetMode.Client ? "CLIENT" : "SP"); }
            catch { return "?"; }
        }

        public static void OnInitPhase(object witchPhase2)
        {
            if (!Enabled || witchPhase2 == null) return;
            try { Plugin.Log.Info($"[WitchP2Probe] InitPhase entered ({Side()}) {Snapshot(witchPhase2)}"); }
            catch { }
        }

        public static void OnShowWitchesEnter(object witchPhase2)
        {
            if (!Enabled || witchPhase2 == null) return;
            try { Plugin.Log.Info($"[WitchP2Probe] ShowWitches ENTER ({Side()}) {Snapshot(witchPhase2)}"); }
            catch { }
        }

        public static void OnShowWitchesExit(object witchPhase2)
        {
            if (!Enabled || witchPhase2 == null) return;
            try { Plugin.Log.Info($"[WitchP2Probe] ShowWitches EXIT ({Side()}) {Snapshot(witchPhase2)} realDomeIndex={ResolveRealDomeIndex(witchPhase2)}"); }
            catch { }
        }

        private static string Snapshot(object p2)
        {
            bool created = BossReflect.TryGetBool(p2, "witchesCreated", out bool wc) && wc;
            int sw = CountOf(p2, "spawnedWitches");
            int si = CountOf(p2, "spawnedIllusions");
            int dome = CountOf(p2, "domePositions");
            bool hasInit = BossReflect.TryGetBool(p2, "hasInitPhase", out bool hi) && hi;
            bool active = BossReflect.TryGetBool(p2, "phaseActive", out bool pa) && pa;
            string delay = BossReflect.TryGetFloat(p2, "delayTimer", out float dt) ? dt.ToString("0.00") : "?";
            float now = Time.time;
            return $"witchesCreated={created} spawnedWitches={sw} spawnedIllusions={si} domePositions={dome} hasInitPhase={hasInit} phaseActive={active} delayTimer={delay} time={now:0.00} (delayElapsed={(delay != "?" ? (now >= dt).ToString() : "?")})";
        }

        /// <summary>Final real-witch dome (after the ShowWitches shuffle): spawnedWitches.IndexOf(realWitchUnit).</summary>
        private static int ResolveRealDomeIndex(object p2)
        {
            try
            {
                var real = BossReflect.GetMember(p2, "realWitchUnit");
                if (real == null) return -1;
                if (BossReflect.GetMember(p2, "spawnedWitches") is System.Collections.IList list)
                    for (int i = 0; i < list.Count; i++)
                        if (ReferenceEquals(list[i], real)) return i;
            }
            catch { }
            return -1;
        }

        private static int CountOf(object obj, string member)
            => BossReflect.GetMember(obj, member) is System.Collections.ICollection c ? c.Count : -1;
    }
}
