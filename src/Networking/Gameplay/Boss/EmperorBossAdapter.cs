using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase 5.4-E: Emperor is a multi-entity worm/spider boss (EmperorBossFightHelper + EmperorBossWorm +
    /// EmperorWormSectionController). Its start is StartPhase1/StartPhase2 rather than a single TriggerFight,
    /// and full sync needs per-section state. This phase is DIAGNOSTIC ONLY — it identifies the encounter and
    /// describes state but does NOT auto-start (the next phase will add the worm/section pipeline).
    /// </summary>
    internal sealed class EmperorBossAdapter : BossAdapterBase
    {
        public override string AdapterName => "EmperorBoss";
        protected override string TypeShortName => "EmperorBossFightHelper";
        // Confirmed by decompilation: EmperorBossFightHelper lives in namespace `PerfectRandom` (NOT
        // PerfectRandom.Sulfur.Gameplay/.Core) inside PerfectRandom.Sulfur.Gameplay.dll. The old guesses caused
        // the "Could not find type named ..." log. Start is StartPhase1/StartPhase2 driven by OnPlayerSpawned,
        // and the boss is multi-entity (worm + spider + sections) — diagnostic-only this phase (empty start chain).
        protected override string[] TypeFullNames => new[]
        {
            "PerfectRandom.EmperorBossFightHelper",
        };

        // No "started" flag on EmperorBossFightHelper. Use phase2Root activation as a best-effort proxy purely for
        // diagnostics; TryApplyHostStart never runs (empty StartChainMethods) so this never gates a real start.
        public override bool IsStarted(object component)
        {
            var p2 = BossReflect.GetMember(component, "phase2Root");
            return p2 is Transform t && t != null && t.gameObject.activeSelf;
        }

        // Intentionally empty: multi-section worm/spider needs its own pipeline (next phase). The base
        // TryApplyHostStart returns false ("diagnostic-only adapter") so the Emperor is never auto-started.
        public override string[] StartChainMethods => System.Array.Empty<string>();

        public override string DescribeForLog(object component)
        {
            string Active(string field)
            {
                var m = BossReflect.GetMember(component, field);
                if (m is Transform tr && tr != null) return tr.gameObject.activeSelf ? "active" : "inactive";
                if (m is Component c && c != null) return c.gameObject.activeSelf ? "active" : "inactive";
                return m != null ? "yes" : "no";
            }
            return $"adapter={AdapterName} type={component.GetType().Name} root={BossReflect.RootName(component)} " +
                   $"phase1Root={Active("phase1Root")} phase2Root={Active("phase2Root")} worm={Active("emperorBossWorm")} spider={Active("emperorBossSpider")}";
        }
    }
}
