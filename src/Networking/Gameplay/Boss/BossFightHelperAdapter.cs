using System;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase 5.4-E: handles every BossFightHelper-derived boss (DesertClauseBossFightHelper,
    /// TerrorbaumBossFightHelper, LuciaBossFightHelper, ...). Start = BossFightHelper.TriggerFight(), which the
    /// reverse-engineering confirms sets fightStarted, attaches the boss UI, starts boss phases and loot/music.
    /// </summary>
    internal sealed class BossFightHelperAdapter : BossAdapterBase
    {
        public override string AdapterName => "BossFightHelper";
        protected override string TypeShortName => "BossFightHelper";
        protected override string[] TypeFullNames => new[]
        {
            "PerfectRandom.Sulfur.Core.BossFightHelper",
            "PerfectRandom.Sulfur.Gameplay.BossFightHelper",
        };

        public override bool IsStarted(object component)
            => BossReflect.TryGetBool(component, "fightStarted", out bool v) && v;

        // BossFightHelper / DesertClause / Lucia all damage `bossUnit` (the boss bar + death unit).
        public override object? GetHealthUnit(object component) => BossReflect.GetMember(component, "bossUnit");

        // DesertClause: OnStartInteractWithBoss() (player interact) -> DelayIntro coroutine -> anim event ->
        // TriggerFight() (sets fightStarted). Generic helpers (Terrorbaum/Lucia) only expose TriggerFight; the base
        // ResolveApplyMethod falls back to it when OnStartInteractWithBoss is absent on the type.
        public override string[] StartChainMethods => new[] { "OnStartInteractWithBoss", "TriggerFight" };

        // Phase 5.4-F2: on the CLIENT, do NOT replay OnStartInteractWithBoss. LogOutput28 showed two Desert problems
        // caused by it: (1) it adds a Cinematic controller lock that is only released at the END of the intro->
        // sandstorm animation chain (StartSandstorm), which doesn't complete client-side -> camera stays locked;
        // (2) its DelayIntro does RepositionBossFromCamera(...) based on the CLIENT camera -> the boss is moved off
        // its mount/placed position (~3u offset). Invoking TriggerFight() directly starts the fight without the
        // cinematic lock and without the client-camera reposition, so the boss stays on its real position. The Host
        // still replays the full intro for itself (this override is client-only).
        public override bool TryApplyHostStart(object component, NetBossEncounterState state, out string detail)
        {
            bool shortcut = false; try { shortcut = Plugin.Cfg.EnableBossClientPresentation.Value; } catch { }
            if (shortcut
                && NetGameplaySyncBridge.BossMode == NetMode.Client
                && BossReflect.HasMethod(component, "OnStartInteractWithBoss")
                && BossReflect.HasMethod(component, "TriggerFight"))
            {
                if (IsStarted(component)) { detail = "already-started"; return true; }
                bool ok = BossReflect.TryInvoke(component, "TriggerFight", out detail);
                detail = $"client-presentation-safe TriggerFight (skipped OnStartInteractWithBoss cinematic): {detail}";
                return ok;
            }
            return base.TryApplyHostStart(component, state, out detail);
        }

        // F3 diagnostic for DesertClause (composite boss). LogOutput28/29: replaying OnStartInteractWithBoss keeps the
        // old man VISIBLE but leaves the Cinematic controller lock stuck (it is released ONLY at the end of the
        // StartSandstorm() coroutine, which the Client's intro animation chain doesn't reach). Skipping the intro made
        // him invisible (regression). The real fix (next phase) is to let the Client run the intro chain + a Desert-
        // specific Cinematic cleanup. This logs the boss position/visibility so the next test can confirm.
        public override void OnClientPresentationStart(object component)
        {
            try
            {
                if (NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                if (!BossReflect.HasMethod(component, "OnStartInteractWithBoss")) return; // DesertClause only
                if (!Plugin.Cfg.LogBossEncounter.Value) return;
                var bossUnit = BossReflect.GetMember(component, "bossUnit");
                Vector3 pos = (bossUnit is Component bc && bc != null) ? bc.transform.position : Vector3.zero;
                bool active = bossUnit is Component ac && ac != null && ac.gameObject.activeInHierarchy;
                bool started = IsStarted(component);
                Plugin.Log.Info($"[BossPresentation] Desert client state: bossUnit={(bossUnit != null ? "yes" : "no")} active={active} fightStarted={started} pos={pos:F1} — Cinematic lock is released only at StartSandstorm() end; client intro chain must reach it (F4).");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossPresentation] Desert failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        public override string DescribeForLog(object component)
        {
            bool started = IsStarted(component);
            int phase = -1; bool hasPhase = false;
            var phases = BossReflect.GetMember(component, "bossPhases");
            if (phases != null) hasPhase = BossReflect.TryGetInt(phases, "currentPhaseIndex", out phase);
            var bossUnit = BossReflect.GetMember(component, "bossUnit");
            BossReflect.TryGetBool(component, "canManuallyTransition", out bool canManual);
            return $"adapter={AdapterName} type={component.GetType().Name} root={BossReflect.RootName(component)} fightStarted={started} phase={(hasPhase ? phase.ToString() : "?")} bossUnit={(bossUnit != null ? "yes" : "no")} canManualTransition={canManual}";
        }

        public override void TryReadState(object component, out bool hasPhase, out int phaseIndex, out bool hasPos, out Vector3 pos)
        {
            phaseIndex = 0; hasPhase = false;
            var phases = BossReflect.GetMember(component, "bossPhases");
            if (phases != null) hasPhase = BossReflect.TryGetInt(phases, "currentPhaseIndex", out phaseIndex);
            hasPos = BossReflect.TryGetPosition(component, out pos);
        }

        // ---- Phase 5.4-E4: generalize dialog-commit to interact/dialog-gated BossFightHelpers ----
        // DesertClause enters via OnStartInteractWithBoss() (player interact -> DelayIntro -> anim event -> TriggerFight).
        // Generic helpers (Terrorbaum) have no OnStartInteractWithBoss and are NOT dialog bosses.
        public override bool IsDialogBoss(object component) => BossReflect.HasMethod(component, "OnStartInteractWithBoss");

        public override bool IsDialogCommitSource(string source)
        {
            var m = ParseSourceMethod(source);
            return m == "OnStartInteractWithBoss" || m == "TriggerFight";
        }

        public override bool ShouldSuppressDuplicateDialogEntry(object component, string source)
        {
            if (!IsStarted(component)) return false;
            // After the fight is started, a re-fired interact would re-open the boss dialog.
            return ParseSourceMethod(source) == "OnStartInteractWithBoss";
        }

        public override bool TryApplyDialogCommit(object component, NetBossDialogCommit commit, out string detail)
        {
            bool finalized = BossDialogReflect.TryFinalizeCurrentDialog(out string dlgDetail);
            string startDetail = "already-started";
            if (!IsStarted(component))
                BossReflect.TryInvoke(component, "TriggerFight", out startDetail);
            detail = $"dialogFinalized={finalized} ({dlgDetail}); start={startDetail}";
            return true;
        }
    }
}
