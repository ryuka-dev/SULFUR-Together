using System;
using UnityEngine;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>Phase 5.4-E: shared adapter plumbing (type resolution, id building, default state read).</summary>
    internal abstract class BossAdapterBase : IBossEncounterAdapter
    {
        public abstract string AdapterName { get; }
        protected abstract string TypeShortName { get; }
        protected abstract string[] TypeFullNames { get; }

        private Type? _type;
        private bool _resolved;

        public Type? ResolveType()
        {
            if (!_resolved) { _resolved = true; _type = BossReflect.FindType(TypeShortName, TypeFullNames); }
            return _type;
        }

        public virtual bool CanHandle(object component)
        {
            if (component == null) return false;
            var t = ResolveType();
            return t != null && t.IsInstanceOfType(component);
        }

        public NetBossEncounterId BuildEncounterId(object component, in BossEncounterContext ctx)
        {
            return new NetBossEncounterId
            {
                RunKey = ctx.RunKey,
                ChapterName = ctx.ChapterName,
                LevelIndex = ctx.LevelIndex,
                HasSeed = ctx.HasSeed,
                Seed = ctx.Seed,
                GraphName = ctx.GraphName,
                BossType = component.GetType().Name,
                RootName = BossReflect.RootName(component),
                RootPath = BossReflect.GameObjectPath(component),
                InstanceId = BossReflect.InstanceId(component),
            };
        }

        public abstract bool IsStarted(object component);

        /// <summary>Default: no chain (diagnostic-only). Families with a start chain override this.</summary>
        public virtual string[] StartChainMethods => Array.Empty<string>();

        public bool IsContinuationSource(string source)
        {
            var method = ParseSourceMethod(source);
            if (string.IsNullOrEmpty(method)) return false;
            foreach (var m in StartChainMethods)
                if (string.Equals(m, method, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// Phase 5.4-E2: source-aware host start. Invokes the method the host broadcast when it exists on this
        /// component (so DesertClause replays OnStartInteractWithBoss, Witch replays EventStarted, Cousin replays
        /// Trigger), else the first existing chain entry. The later chain steps are allowed by the manager's
        /// authorized-continuation window — we do NOT try to drive the whole chain synchronously here.
        /// </summary>
        public virtual bool TryApplyHostStart(object component, NetBossEncounterState state, out string detail)
        {
            if (StartChainMethods.Length == 0) { detail = $"{AdapterName}: no start chain (diagnostic-only adapter)"; return false; }
            if (IsStarted(component)) { detail = "already-started"; return true; }
            string method = ResolveApplyMethod(component, state?.StartSource ?? "");
            if (string.IsNullOrEmpty(method)) { detail = "no invokable start-chain method on component"; return false; }
            return BossReflect.TryInvoke(component, method, out detail);
        }

        /// <summary>Pick which chain method to invoke: prefer the host's broadcast source method (replays the same
        /// entry), else the first chain method that actually exists on the component.</summary>
        protected string ResolveApplyMethod(object component, string source)
        {
            var srcMethod = ParseSourceMethod(source);
            if (!string.IsNullOrEmpty(srcMethod)
                && IsContinuationSource(source)
                && BossReflect.HasMethod(component, srcMethod))
                return srcMethod;
            foreach (var m in StartChainMethods)
                if (BossReflect.HasMethod(component, m)) return m;
            return StartChainMethods.Length > 0 ? StartChainMethods[0] : "";
        }

        /// <summary>Extract the bare method name from a "Type.Method" / "ClientRequest:Type.Method" source string.</summary>
        protected static string ParseSourceMethod(string? source)
        {
            if (string.IsNullOrEmpty(source)) return "";
            var s = source!;
            int colon = s.IndexOf(':');           // strip "ClientRequest:" (or any prefix:) tag
            if (colon >= 0 && colon < s.Length - 1) s = s.Substring(colon + 1);
            int dot = s.LastIndexOf('.');          // take the method after the type name
            return dot >= 0 && dot < s.Length - 1 ? s.Substring(dot + 1) : s;
        }

        public virtual void TryReadState(object component, out bool hasPhase, out int phaseIndex, out bool hasPos, out Vector3 pos)
        {
            hasPhase = BossReflect.TryGetInt(component, "currentPhaseIndex", out phaseIndex);
            hasPos = BossReflect.TryGetPosition(component, out pos);
        }

        public virtual string DescribeForLog(object component)
        {
            bool started = IsStarted(component);
            TryReadState(component, out bool hasPhase, out int phaseIndex, out _, out _);
            return $"adapter={AdapterName} type={component.GetType().Name} root={BossReflect.RootName(component)} path={BossReflect.GameObjectPath(component)} started={started} phase={(hasPhase ? phaseIndex.ToString() : "?")}";
        }

        // ---- Phase 5.4-E3 dialog/state capabilities: default = not a dialog/phase boss ----
        public virtual bool IsDialogBoss(object component) => false;
        public virtual bool IsDialogCommitSource(string source) => false;

        /// <summary>Phase PF (Plan B): true if this boss's fight must NOT auto-start from its behavior tree but instead
        /// wait until the intro DIALOG is dismissed by an in-room player. In single-player the boss dialog pauses the
        /// game (timeScale=0), freezing the behavior tree's WaitForSeconds so StartFight effectively waits for the
        /// dialog to close; co-op disables that pause (Phase 5.7-NP), so StartFight would fire ~1.1s after the dialog
        /// OPENS and overlap it. When true the manager blocks StartFight until a host-authoritative dialog-close commit.</summary>
        public virtual bool GatesFightOnDialogClose(object component) => false;
        public virtual bool ShouldSuppressDuplicateDialogEntry(object component, string source) => false;
        public virtual bool TryApplyDialogCommit(object component, NetBossDialogCommit commit, out string detail)
        { detail = "dialog commit not supported by this adapter"; return false; }

        /// <summary>The Npc the boss dialog interactable talks to. Default = the boss's health Unit (Cousin owner,
        /// Lucia/Desert bossUnit). Overridden when the dialog speaker differs from the damageable unit.</summary>
        public virtual object? ResolveDialogNpc(object component) => GetHealthUnit(component);

        /// <summary>Fix A (root): make the boss un-talkable once the fight has started, so nothing can re-open its
        /// dialog (the LogOutput121/122 host stale-dialog loop).
        /// <para>The PRIMARY action is trigger-agnostic: null the boss <see cref="Npc"/>'s <c>dialog</c>
        /// (<c>HasDialog =&gt; dialog != null</c>), so <c>Npc.Interact</c> skips the dialog branch no matter what opens
        /// it — Cousin's dialog is a STEP TRIGGER that calls <c>Npc.Interact()</c>, NOT a <see cref="UnitInteractable"/>
        /// (LogOutput122: scanned=0). As a secondary belt-and-suspenders we also remove any matching
        /// <see cref="UnitInteractable"/> (Witch/Lucia/vendor-style hold-interact dialogs), exactly as vanilla Witch
        /// does in FightStartRoutine.</para>
        /// Core types referenced directly; only the boss helper lives in the Gameplay DLL (reflection via GetHealthUnit).</summary>
        public virtual bool TryRemoveDialogInteractable(object component, out string detail)
        {
            detail = "";
            try
            {
                var bossUnit = ResolveDialogNpc(component) as UnityEngine.Object;
                if (bossUnit == null) { detail = "no boss unit to match"; return false; }

                // PRIMARY: null the boss Npc's dialog so Npc.Interact can no longer open it (trigger-agnostic).
                // dialog's TYPE (DialogueTreeController) lives in the unreferenced ParadoxNotion assembly, so set it
                // via reflection — SetValue(null) needs no compile-time knowledge of the type.
                bool dialogCleared = false;
                var dialogProp = bossUnit.GetType().GetProperty("dialog",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (dialogProp != null && dialogProp.CanWrite && dialogProp.GetValue(bossUnit) != null)
                {
                    dialogProp.SetValue(bossUnit, null);
                    dialogCleared = true;
                }

                // SECONDARY: remove any UnitInteractable that talks to this boss (hold-interact dialog bosses).
                var all = UnityEngine.Object.FindObjectsOfType<UnitInteractable>();
                var im = StaticInstance<InteractionManager>.Instance;
                int removed = 0, disabled = 0;
                foreach (var ui in all)
                {
                    if (ui == null || ui.npc == null) continue;
                    if ((UnityEngine.Object)ui.npc != bossUnit) continue;
                    if (im != null) { im.RemoveInteractable(ui); removed++; }
                    if (ui.gameObject != null) { ui.gameObject.SetActive(false); disabled++; }
                }
                detail = $"boss={bossUnit.name} dialogCleared={dialogCleared} interactableRemoved={removed} disabled={disabled} scannedUI={all.Length}";
                return dialogCleared || removed > 0 || disabled > 0;
            }
            catch (Exception ex) { detail = $"ex {ex.GetType().Name}: {ex.Message}"; return false; }
        }

        // ---- Phase 5.4-E4.2 boss health sync (generic; works for any adapter that exposes a GetHealthUnit) ----
        public virtual object? GetHealthUnit(object component) => null;

        public virtual bool ProvidesPhaseState => false;

        /// <summary>Default: fill the host-authoritative health of the boss's damageable unit. Phase bosses override
        /// to also add phase/adds (and should call base for the health part).</summary>
        public virtual void FillBossState(object component, NetBossState state)
        {
            FillHealth(component, state);
        }

        /// <summary>Default: apply host health to the local boss unit (Stats(92)) and make sure the boss bar is
        /// attached so the Client actually shows a synced health bar. Phase bosses override to also apply phase.</summary>
        public virtual bool TryApplyBossState(object component, NetBossState state, out string detail)
        {
            detail = ApplyHealth(component, state);
            return true;
        }

        protected void FillHealth(object component, NetBossState state)
        {
            var hu = GetHealthUnit(component);
            if (hu != null && NetGameplayProbeManager.TryReadBossUnitHealth(hu, out float cur, out float max))
            {
                state.HasHealth = true;
                state.CurrentHealth = cur;
                state.MaxHealth = max;
            }
        }

        /// <summary>Write the host health onto the local boss unit's Stats(92) AND fire onHealthChange so the boss
        /// bar (which listens to that event, not the raw stat) reflects it. The bar is ATTACHED once by the manager.</summary>
        protected string ApplyHealth(object component, NetBossState state)
        {
            var hu = GetHealthUnit(component);
            if (hu == null) return "no health unit";
            if (!state.HasHealth) return "host sent no health";
            bool wrote = NetGameplayProbeManager.TryWriteBossUnitHealth(hu, state.CurrentHealth);
            float normalized = state.MaxHealth > 0.0001f ? Mathf.Clamp01(state.CurrentHealth / state.MaxHealth) : 0f;
            bool bar = BossReflect.TryFireHealthChange(hu, normalized);
            return $"health {(wrote ? "written" : "write-failed")} {state.CurrentHealth:0}/{state.MaxHealth:0} (norm={normalized:0.00}) barEvent={bar}";
        }

        /// <summary>Attach the boss bar to this boss's health unit (called ONCE per encounter by the manager — Attach
        /// re-subscribes onHealthChange each call, so it must not run every state packet).</summary>
        public bool TryAttachBossBar(object component)
        {
            var hu = GetHealthUnit(component);
            if (hu == null) return false;
            return BossReflect.TryInvokeBool(hu, "AttachToBossUI", true, out _);
        }

        // ---- Phase 5.4-F BossDamageAuthority: default role mapping = the single main health unit ----
        public virtual string? ResolveHitTargetRole(object component, object hitUnit)
        {
            if (hitUnit == null) return null;
            var hu = GetHealthUnit(component);
            return hu != null && ReferenceEquals(hu, hitUnit) ? "main" : null;
        }

        public virtual object? ResolveHostTargetForRole(object component, string role)
            => role == "main" ? GetHealthUnit(component) : null;

        public virtual void OnClientPresentationStart(object component) { }

        // ---- Phase 5.4-F5 Lucia eye defeat authority: default = not an eye boss ----
        public virtual bool IsEyeBoss => false;
        public virtual bool TryReadEyePhase(object component, out int cycle, out int livingEyes)
        { cycle = 0; livingEyes = 0; return false; }
        public virtual bool TryConsumeOneEye(object component, out int remaining, out string detail)
        { remaining = 0; detail = "not an eye boss"; return false; }
        public virtual bool TryRemoveDeadEyeFromList(object component, object eyeUnit) => false;
        public virtual bool TryApplyEyePhaseComplete(object component, out int cleared, out string detail)
        { cleared = 0; detail = "not an eye boss"; return false; }
        public virtual bool TryReadEyeCompletionDiag(object component, out int phase, out int restartCounter, out bool invulnerable, out Vector3 pos)
        { phase = -1; restartCounter = -1; invulnerable = false; pos = Vector3.zero; return false; }
        public virtual bool TryApplyLuciaDeath(object component, out string detail)
        { detail = "not a Lucia boss"; return false; }

        // ---- Phase 5.4-F4 discrete-event authority: default = no fixed-point events ----
        public virtual string[] DiscreteEventMethods => Array.Empty<string>();
        public virtual bool BuildDiscreteEvent(object component, string eventName, out bool hasPos, out Vector3 pos, out string diag)
        { hasPos = false; pos = Vector3.zero; diag = ""; return false; }
        public virtual bool TryApplyDiscreteEvent(object component, string eventName, bool hasPos, Vector3 pos, out string detail)
        { detail = "discrete events not supported"; return false; }
        public virtual bool IsTerminalEvent(string eventName) => false;
    }
}
