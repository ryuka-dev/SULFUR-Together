using System;
using UnityEngine;
using PerfectRandom.Sulfur.Core.Units;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase PF (FF14 dialog-sync design evidence): read-only probe over the dialog-open chokepoints so a real co-op
    /// Cousin test reveals HOW the boss dialog/fight is actually wired — whether the fight starts via a trigger volume
    /// that calls the boss start directly (no dialog), or via the boss dialog's Attack (双剑) option, or both. Gated by
    /// <c>LogBossPreFight</c>. Correlate the timestamps of these lines with the existing
    /// <c>[BossLifecycle] CousinHelper.Trigger/Introduction</c> lines to pin down the flow before wiring the sync.
    /// Never opens/closes/changes anything.
    /// </summary>
    internal static class BossDialogFlowProbe
    {
        private static bool LogOn
        {
            get { try { return Plugin.Cfg.LogBossPreFight.Value; } catch { return false; } }
        }

        // EMP-5 probe: fires while an Emperor worm is live, regardless of LogBossPreFight — so a single test reveals the
        // (scene-scripted) Emperor pre-fight dialog wiring: which chokepoint opens it, the speaker Npc + whether it has a
        // reopenable `dialog`, and the trigger that opens dialog vs the one that drives the door. Read-only.
        private static bool EmpOn
        {
            get { try { return NetEmperorWormSync.IsWormActive; } catch { return false; } }
        }

        private static NetMode Mode
        {
            get { try { return NetGameplaySyncBridge.BossMode; } catch { return NetMode.Off; } }
        }

        /// <summary>Postfix on Npc.Interact(Player): logs whether a BOSS dialog was opened this way and its state.</summary>
        public static void OnNpcInteract(object npc, bool ran)
        {
            if ((!LogOn && !EmpOn) || !ran || npc == null) return;
            try
            {
                if (!(npc is Npc n)) return;
                bool isBoss = false;
                try { isBoss = n.UnitType == PerfectRandom.Sulfur.Core.Utilities.UnitType.Boss; } catch { }
                bool hasDialog = false; try { hasDialog = n.HasDialog; } catch { }
                // EMP-5: the Emperor's dialog speaker may NOT be a Boss unit (scene-scripted actor) — log EVERY interact
                // while the worm is live, so we see exactly which Npc opens the dialog and whether it is reopenable.
                if (EmpOn)
                {
                    string ut = "?"; try { ut = n.UnitType.ToString(); } catch { }
                    Plugin.Log.Info($"[EmperorDialog] Npc.Interact mode={Mode} npc={SafeName(npc)} unitType={ut} hasDialog={hasDialog}");
                }
                // Only log boss interactions here (avoid spamming on vendors/NPCs).
                if (LogOn && isBoss)
                    Plugin.Log.Info($"[DialogFlow] Npc.Interact mode={Mode} npc={SafeName(npc)} boss=True hasDialog={hasDialog}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[DialogFlow] Npc.Interact log failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Postfix on DialogController.SetCurrentSpeakable: logs dialog open (speakable!=null) / close (null).</summary>
        public static void OnSetCurrentSpeakable(object speakable)
        {
            if (!LogOn && !EmpOn) return;
            try
            {
                string name = speakable == null ? "NULL(close)" : SafeName(speakable);
                if (LogOn) Plugin.Log.Info($"[DialogFlow] SetCurrentSpeakable mode={Mode} -> {name}");
                // EMP-5: resolve the speaker's actor name + its Npc unit + whether that Npc has a (reopenable) dialog —
                // this is the data needed to decide whether the Emperor dialog can be "deleted" (Npc.dialog=null) or only
                // closed, and why the earlier commit-time lookup logged speaker=unavailable.
                if (EmpOn)
                {
                    if (speakable == null) { Plugin.Log.Info($"[EmperorDialog] SetCurrentSpeakable mode={Mode} -> NULL(close)"); return; }
                    string actor = BossReflect.GetMember(speakable, "ActorName") as string ?? "?";
                    object unit = BossReflect.GetMember(speakable, "unit");
                    string unitName = unit is UnityEngine.Object uo && uo != null ? uo.name : (unit?.GetType().Name ?? "null");
                    bool hasDialog = false; try { if (unit is Npc un) hasDialog = un.HasDialog; } catch { }
                    Plugin.Log.Info($"[EmperorDialog] SetCurrentSpeakable mode={Mode} actor=\"{actor}\" speakerType={speakable.GetType().Name} unit={unitName} unitHasDialog={hasDialog}");
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[DialogFlow] SetCurrentSpeakable log failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Postfix on PlayerTrigger.Trigger(GameObject): identifies room-entry / close-room triggers and what
        /// objects they fire (helps map the room-membership trigger for the FF14 lockdown). Reflection (private fields).</summary>
        public static void OnPlayerTrigger(object pt)
        {
            if ((!LogOn && !EmpOn) || pt == null) return;
            try
            {
                string pos = (pt is Component c && c != null) ? c.transform.position.ToString("F1") : "?";
                string evt = ""; var ev = pt.GetType().GetField("eventReference", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (ev != null) evt = ev.GetValue(pt)?.ToString() ?? "";
                string objs = ReadTriggeredObjects(pt);
                // EMP-5: while the worm is live, log every trigger + the persistent UnityEvent targets/methods it fires,
                // so we can tell the dialog trigger (opens dialog) apart from the door trigger (MetalGate.Close/Open).
                string tag = EmpOn ? "[EmperorDialog]" : "[DialogFlow]";
                if (LogOn || EmpOn)
                    Plugin.Log.Info($"{tag} PlayerTrigger.Trigger mode={Mode} name={SafeName(pt)} pos={pos} event=\"{evt}\" fires=[{objs}] persistent=[{ReadPersistentTargets(pt)}]");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[DialogFlow] PlayerTrigger log failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ============================================================ F4-P2DLG: dialog input probe (re-added, read-only)
        // Investigates the "can't advance the mid-fight dialog by clicking" bug (shelved as the multiple-choice panel not
        // intercepting clicks — SetOptionsInteractable sets CanvasGroup.interactable but NOT blocksRaycasts). This time we
        // also compare HOST vs CLIENT (the client can finish, the host can't). Gated by LogBossPreFight. Read-only.
        private static float _dlgInputLogNext;

        /// <summary>Postfix on DialogController.AcceptDialogOption(): the click/enter/space advance actually reached the
        /// dialog. Log the decisive state — is it a choice panel (InChoices), is a selectedButton set, does the panel block
        /// raycasts, where does a raycast at the cursor land (rayTop), and the cursor lock/visibility.</summary>
        public static void OnAcceptDialogOption(object dc)
        {
            if (!LogOn || dc == null) return;
            try
            {
                bool inChoices = GetField(dc, "InChoices") is bool ic && ic;
                var sel = GetField(dc, "selectedButton");
                bool selNull = sel == null || (sel is UnityEngine.Object so && so == null);
                var cg = GetField(dc, "playerDialogCanvasGroup") as CanvasGroup;
                string cgState = cg == null ? "cg=null" : $"interactable={cg.interactable} blocksRaycasts={cg.blocksRaycasts} alpha={cg.alpha:F1}";
                string cursor = $"lock={Cursor.lockState} vis={Cursor.visible}";
                string rayTop = DescribeCursorRaycast();
                Plugin.Log.Info($"[DialogInput] AcceptDialogOption mode={Mode} InChoices={inChoices} selectedButton={(selNull ? "NULL" : "set")} {cgState} {cursor} rayTop=[{rayTop}]");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[DialogInput] AcceptDialogOption log failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Postfix on DialogController.OnMultipleChoiceRequest(...): a choice panel was just shown. Log the panel's
        /// raycast state right after it appears (the shelved conclusion: interactable set, blocksRaycasts NOT).</summary>
        public static void OnMultipleChoiceRequest(object dc)
        {
            if (!LogOn || dc == null) return;
            try
            {
                var cg = GetField(dc, "playerDialogCanvasGroup") as CanvasGroup;
                // The canvas group is lazily fetched in SetOptionsInteractable; grab it off playerDialog if still null.
                if (cg == null && GetField(dc, "playerDialog") is Component pd && pd != null) cg = pd.GetComponent<CanvasGroup>();
                string cgState = cg == null ? "cg=null" : $"interactable={cg.interactable} blocksRaycasts={cg.blocksRaycasts} alpha={cg.alpha:F1}";
                Plugin.Log.Info($"[DialogInput] MultipleChoiceRequest mode={Mode} {cgState}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[DialogInput] MultipleChoiceRequest log failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Postfix on DialogController.OnDialogueStarted(dlg): the controller set up the dialog (cursor unlock +
        /// controller lock that arms the UI input map). Confirms the airstrike dialog actually goes through here + the
        /// resulting cursor/pause state — if this fires but AcceptDialogOption never does, the advance input is being
        /// eaten (e.g. the gameplay Fire map still owns LMB during combat).</summary>
        public static void OnDialogueStarted(object dc)
        {
            if (!LogOn || dc == null) return;
            try
            {
                Plugin.Log.Info($"[DialogInput] OnDialogueStarted mode={Mode} cursor(lock={Cursor.lockState} vis={Cursor.visible}) timeScale={Time.timeScale:F2}");
            }
            catch { }
        }

        public static void OnDialogueFinished(object dc)
        {
            if (!LogOn || dc == null) return;
            try { Plugin.Log.Info($"[DialogInput] OnDialogueFinished mode={Mode}"); }
            catch { }
        }

        /// <summary>Postfix on DialogController.Update() (throttled ~0.5s): while ANY dialog is active (currentDialogueTree
        /// != null), log the stuck steady state — InChoices, cursor lock/visibility, game timeScale (our no-pause keeps it
        /// 1), the choice panel's blocksRaycasts, and what a cursor raycast hits. Captures the host's "can't advance" state
        /// even though no advance input ever reaches AcceptDialogOption.</summary>
        public static void OnDialogUpdate(object dc)
        {
            if (!LogOn || dc == null) return;
            try
            {
                var tree = GetField(dc, "currentDialogueTree");
                bool active = tree != null && !(tree is UnityEngine.Object to && to == null);
                if (!active) return; // only while a dialog is on-screen
                float now = Time.realtimeSinceStartup;
                if (now < _dlgInputLogNext) return;
                _dlgInputLogNext = now + 0.5f;
                bool inChoices = GetField(dc, "InChoices") is bool ic && ic;
                var cg = GetField(dc, "playerDialogCanvasGroup") as CanvasGroup;
                string cgState = cg == null ? "cg=null" : $"blocksRaycasts={cg.blocksRaycasts} interactable={cg.interactable}";
                Plugin.Log.Info($"[DialogInput] dialogActive mode={Mode} InChoices={inChoices} cursor(lock={Cursor.lockState} vis={Cursor.visible}) timeScale={Time.timeScale:F2} {cgState} rayTop=[{DescribeCursorRaycast()}]");
            }
            catch { }
        }

        // EventSystem raycast at the current cursor position → the topmost UI element name(s) the click would hit. "NONE"
        // = the click falls through everything (the shelved rayTop=NONE symptom). Uses the InputSystem mouse position with
        // a legacy fallback.
        private static string DescribeCursorRaycast()
        {
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es == null) return "no-EventSystem";
                Vector2 mp;
                try { mp = UnityEngine.InputSystem.Mouse.current != null ? UnityEngine.InputSystem.Mouse.current.position.ReadValue() : (Vector2)Input.mousePosition; }
                catch { mp = Input.mousePosition; }
                var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = mp };
                var results = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                es.RaycastAll(ped, results);
                if (results.Count == 0) return "NONE";
                var parts = new System.Collections.Generic.List<string>();
                for (int i = 0; i < results.Count && i < 4; i++) parts.Add(results[i].gameObject != null ? results[i].gameObject.name : "?");
                return string.Join(" > ", parts);
            }
            catch (Exception ex) { return "err:" + ex.GetType().Name; }
        }

        private static object? GetField(object obj, string name)
        {
            try
            {
                for (Type? t = obj.GetType(); t != null; t = t.BaseType)
                {
                    var f = t.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
                    if (f != null) return f.GetValue(obj);
                }
            }
            catch { }
            return null;
        }

        // EMP-5: read the trigger's onTriggerEvents persistent calls (target type + method) — this reveals whether a
        // trigger closes a MetalGate, opens dialog (Npc.Interact), sets doors active, etc. (same data the arena lockdown
        // uses to detect seal triggers).
        private static string ReadPersistentTargets(object pt)
        {
            try
            {
                var f = pt.GetType().GetField("onTriggerEvents", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (!(f?.GetValue(pt) is UnityEngine.Events.UnityEventBase evt)) return "?";
                int n = evt.GetPersistentEventCount();
                if (n == 0) return "<none>";
                var parts = new System.Collections.Generic.List<string>();
                for (int i = 0; i < n; i++)
                {
                    var t = evt.GetPersistentTarget(i);
                    parts.Add($"{(t != null ? t.GetType().Name : "null")}.{evt.GetPersistentMethodName(i)}");
                }
                return string.Join(",", parts);
            }
            catch { return "?"; }
        }

        private static string ReadTriggeredObjects(object pt)
        {
            try
            {
                var f = pt.GetType().GetField("objectsToTrigger", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f == null) return "?";
                if (!(f.GetValue(pt) is System.Collections.IEnumerable list)) return "?";
                var names = new System.Collections.Generic.List<string>();
                foreach (var o in list) names.Add(SafeName(o));
                return names.Count == 0 ? "<none>" : string.Join(",", names);
            }
            catch { return "?"; }
        }

        private static string SafeName(object o)
        {
            try { return o is UnityEngine.Object uo && uo != null ? uo.name : (o?.GetType().Name ?? "?"); }
            catch { return "?"; }
        }
    }
}
