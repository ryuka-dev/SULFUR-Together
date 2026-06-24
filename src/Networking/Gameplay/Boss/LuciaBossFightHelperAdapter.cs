using System.Collections;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase 5.4-E3: Lucia (圣露西亚). Confirmed by decompilation:
    ///   - LuciaBossFightHelper : BossFightHelper, but declares `public new void TriggerFight()` (HIDES the base
    ///     virtual). That is why the generic base-TriggerFight hook never caught Lucia. Lucia.TriggerFight sets
    ///     fightStarted=true, attaches boss UI, then calls StartPhase(1) (Lucia uses its own `currentPhase` int,
    ///     not bossPhases — hence the harmless "No valid phases defined!" log).
    ///   - The dialog "attack" option calls LuciaBossFightTrigger.TriggerFight() -> luciaBossFightHelper.TriggerFight().
    ///   - Adds/special: spawnedEyes (List&lt;BlackGuildLuciaEye&gt;) — diagnostic only this phase.
    /// Registered BEFORE the generic BossFightHelperAdapter so Lucia resolves here, not to the generic adapter.
    /// </summary>
    internal sealed class LuciaBossFightHelperAdapter : BossAdapterBase
    {
        public override string AdapterName => "LuciaBossFightHelper";
        protected override string TypeShortName => "LuciaBossFightHelper";
        protected override string[] TypeFullNames => new[]
        {
            "PerfectRandom.Sulfur.Core.LuciaBossFightHelper",
        };

        // fightStarted is the inherited BossFightHelper field, set by Lucia's `new TriggerFight()`.
        public override bool IsStarted(object component)
            => BossReflect.TryGetBool(component, "fightStarted", out bool v) && v;

        // Lucia damages the inherited bossUnit (the boss bar + death unit).
        public override object? GetHealthUnit(object component) => BossReflect.GetMember(component, "bossUnit");

        // The real start entry is the `new` TriggerFight (the BossReflect DeclaredOnly walk resolves the hiding one).
        public override string[] StartChainMethods => new[] { "TriggerFight" };

        // Lucia is dialog-gated like Cousin: the fight begins from a dialog choice.
        public override bool IsDialogBoss(object component) => true;
        public override bool IsDialogCommitSource(string source) => ParseSourceMethod(source) == "TriggerFight";

        public override bool ShouldSuppressDuplicateDialogEntry(object component, string source)
            => IsStarted(component) && ParseSourceMethod(source) == "TriggerFight";

        public override bool TryApplyDialogCommit(object component, NetBossDialogCommit commit, out string detail)
        {
            bool finalized = BossDialogReflect.TryFinalizeCurrentDialog(out string dlgDetail);
            string startDetail = "already-started";
            if (!IsStarted(component))
                BossReflect.TryInvoke(component, "TriggerFight", out startDetail);
            detail = $"dialogFinalized={finalized} ({dlgDetail}); start={startDetail}";
            return true;
        }

        // ---- Phase 5.4-F5: eye defeat authority (count/cycle). Confirmed by decompilation of LuciaBossFightHelper:
        //   - Eye phase = Phase5Routine (currentPhase==5): SetInvulnerable(true) + ToggleDarkness(true) + SpawnEyes().
        //   - SpawnEyes(): each eye Unit gets `onDeath += EyeDied`, added to spawnedEyes (List<BlackGuildLuciaEye>).
        //   - EyeDied(Unit): spawnedEyes.Remove → when Count==0 → RestartPhases() → restartCounter++ → StartPhase(1).
        //   - BlackGuildLuciaEye.owner is the eye's Unit (where onDeath/EyeDied is wired). Calling owner.Die() runs the
        //     REAL death path → fires the host's vanilla EyeDied → natural RestartPhases on the last eye. ----
        private const int EyePhaseIndex = 5;

        public override bool IsEyeBoss => true;

        public override bool TryReadEyePhase(object component, out int cycle, out int livingEyes)
        {
            cycle = 0; livingEyes = 0;
            BossReflect.TryGetInt(component, "restartCounter", out cycle);
            if (BossReflect.GetMember(component, "spawnedEyes") is ICollection eyes) livingEyes = eyes.Count;
            bool hasPhase = BossReflect.TryGetInt(component, "currentPhase", out int phase);
            return hasPhase && phase == EyePhaseIndex;
        }

        public override bool TryConsumeOneEye(object component, out int remaining, out string detail)
        {
            remaining = 0; detail = "";
            if (!(BossReflect.GetMember(component, "spawnedEyes") is IList list)) { detail = "no spawnedEyes list"; return false; }
            // Snapshot first: owner.Die() fires EyeDied which mutates the list mid-call.
            object? targetOwner = null; string ownerName = "?";
            foreach (var eye in new ArrayList(list))
            {
                var owner = BossReflect.GetMember(eye, "owner");
                if (owner == null) continue;
                if (BossReflect.TryGetBool(owner, "IsAlive", out bool alive) && !alive) continue;
                targetOwner = owner; ownerName = BossReflect.RootName(owner); break;
            }
            if (targetOwner == null) { remaining = list.Count; detail = $"no living host eye (count={list.Count})"; return false; }

            bool died = BossReflect.TryInvoke(targetOwner, "Die", out string dieDetail);
            // Re-read after the native EyeDied has had its synchronous effect.
            remaining = (BossReflect.GetMember(component, "spawnedEyes") is ICollection after) ? after.Count : -1;
            detail = $"selected={ownerName} nativeDie={died} ({dieDetail}) remaining={remaining}";
            return died;
        }

        public override bool TryRemoveDeadEyeFromList(object component, object eyeUnit)
        {
            if (eyeUnit == null || !(BossReflect.GetMember(component, "spawnedEyes") is IList list)) return false;
            for (int i = 0; i < list.Count; i++)
            {
                var owner = BossReflect.GetMember(list[i], "owner");
                if (ReferenceEquals(owner, eyeUnit)) { list.RemoveAt(i); return true; }
            }
            return false;
        }

        public override bool TryApplyEyePhaseComplete(object component, out int cleared, out string detail)
        {
            cleared = 0;
            // 1) Destroy any residual local eyes (DestroySelf — NOT Die — so we don't re-fire the local EyeDied chain).
            //    On the Client the eyes it killed already died; any still-alive ones were killed host-side, so clear them.
            if (BossReflect.GetMember(component, "spawnedEyes") is IList list)
            {
                foreach (var eye in new ArrayList(list))
                {
                    var owner = BossReflect.GetMember(eye, "owner");
                    if (owner != null && BossReflect.TryInvoke(owner, "DestroySelf", out _)) cleared++;
                }
                list.Clear();
            }

            // 2) Run the REAL host-authorized recovery: RestartPhases() → RestartRoutine (ToggleDarkness(false),
            //    eventAnimator "Center" return-to-centre, candle burn, +9s → restartCounter++ → StartPhase(1)).
            //    StartPhase(1) also StopAllCoroutines() which kills the lingering EyeRoutine fly-around. This is what
            //    actually takes the Client out of Phase 5 — a plain ToggleDarkness/SetInvulnerable would skip all of it.
            bool restartInvoked = BossReflect.TryInvoke(component, "RestartPhases", out string restartDetail);
            string fallback = "";
            if (!restartInvoked)
            {
                // RestartPhases missing/failed — at least lift the darkness so the Client isn't stuck black-screened.
                bool darkOff = BossReflect.TryInvokeBool(component, "ToggleDarkness", false, out _);
                fallback = $" RestartPhases-FAILED({restartDetail}) fallback-darknessLifted={darkOff}";
            }
            detail = $"residualCleared={cleared} RestartPhasesInvoked={restartInvoked} ({restartDetail}){fallback}";
            return restartInvoked;
        }

        /// <summary>Read body invuln from <c>bossUnit.isInvulnerable</c> (confirmed Unit property) + currentPhase /
        /// restartCounter / bossUnit position for before→after completion diagnostics.</summary>
        public override bool TryReadEyeCompletionDiag(object component, out int phase, out int restartCounter, out bool invulnerable, out Vector3 pos)
        {
            phase = -1; restartCounter = -1; invulnerable = false; pos = Vector3.zero;
            BossReflect.TryGetInt(component, "currentPhase", out phase);
            BossReflect.TryGetInt(component, "restartCounter", out restartCounter);
            var bossUnit = BossReflect.GetMember(component, "bossUnit");
            BossReflect.TryGetBool(bossUnit, "isInvulnerable", out invulnerable);
            BossReflect.TryGetPosition(bossUnit, out pos);
            return true;
        }

        // ---- Phase 5.4-F6: Lucia terminal death. Confirmed by decompilation of LuciaBossFightHelper.OnBossDead(Unit):
        //   PRESENTATION (safe on client): eventAnimator/bossAnimator.speed=1, eventAnimator.SetBool("Dead",true),
        //     bossUnit.AttachToBossUI(false), destructionAnimator.SetInteger("DestructionLevel",3), StopAllCoroutines(),
        //     MusicPlayer.StopPlaying(5f).
        //   HOST-ONLY world results (must NOT run on client): PlayerProgress.SaveCheckpoints, SulfurSave.SaveBackup,
        //     luciaLoot[i].PlaceLoot(), LootManager.SetBossFightLoot — these are isolated by the OnBossDead prefix
        //     (the client's bossUnit.Die() → OnBossDead is blocked), so we replay only the presentation subset here. ----
        public override bool TryApplyLuciaDeath(object component, out string detail)
        {
            var bossUnit = BossReflect.GetMember(component, "bossUnit");
            if (bossUnit == null) { detail = "no bossUnit"; return false; }

            // Real Unit death (dead state / death animation / despawn). The OnBossDead prefix blocks the loot/save body
            // on the client, so this Die() does not duplicate world results.
            bool alreadyDead = BossReflect.TryGetBool(bossUnit, "IsAlive", out bool alive) && !alive;
            bool died = false; string dieDetail = "already-dead";
            if (!alreadyDead) died = BossReflect.TryInvoke(bossUnit, "Die", out dieDetail);

            // Presentation subset (the loot/save-free parts of OnBossDead).
            bool barOff = BossReflect.TryInvokeBool(bossUnit, "AttachToBossUI", false, out _);
            var eventAnimator = BossReflect.GetMember(component, "eventAnimator");
            bool deadAnim = TrySetAnimatorBool(eventAnimator, "Dead", true);
            bool destrSet = TrySetAnimatorInt(BossReflect.GetMember(component, "destructionAnimator"), "DestructionLevel", 3);
            BossReflect.TryInvoke(component, "StopAllCoroutines", out _);

            detail = $"died={died}({dieDetail}) alreadyDead={alreadyDead} barDetached={barOff} deadAnim={deadAnim} destruction={destrSet}";
            return true;
        }

        private static bool TrySetAnimatorBool(object? animator, string param, bool value)
        {
            try
            {
                if (animator is Animator a && a != null) { a.SetBool(param, value); return true; }
            }
            catch { }
            return false;
        }

        private static bool TrySetAnimatorInt(object? animator, string param, int value)
        {
            try
            {
                if (animator is Animator a && a != null) { a.SetInteger(param, value); return true; }
            }
            catch { }
            return false;
        }

        public override void TryReadState(object component, out bool hasPhase, out int phaseIndex, out bool hasPos, out Vector3 pos)
        {
            hasPhase = BossReflect.TryGetInt(component, "currentPhase", out phaseIndex);
            hasPos = BossReflect.TryGetPosition(component, out pos);
        }

        public override string DescribeForLog(object component)
        {
            bool started = IsStarted(component);
            bool hasPhase = BossReflect.TryGetInt(component, "currentPhase", out int phase);
            BossReflect.TryGetBool(component, "fightComplete", out bool complete);
            BossReflect.TryGetInt(component, "candlesLeft", out int candles);
            int eyes = -1;
            var spawnedEyes = BossReflect.GetMember(component, "spawnedEyes");
            if (spawnedEyes is ICollection col) eyes = col.Count;
            var bossUnit = BossReflect.GetMember(component, "bossUnit");
            // BlackGuildLuciaEye are Lucia special/adds — flagged here, left for the BossSpecialEvent phase.
            return $"adapter={AdapterName} type={component.GetType().Name} root={BossReflect.RootName(component)} fightStarted={started} currentPhase={(hasPhase ? phase.ToString() : "?")} fightComplete={complete} candlesLeft={candles} luciaEyes[special/add]={(eyes >= 0 ? eyes.ToString() : "?")} bossUnit={(bossUnit != null ? "yes" : "no")}";
        }
    }
}
