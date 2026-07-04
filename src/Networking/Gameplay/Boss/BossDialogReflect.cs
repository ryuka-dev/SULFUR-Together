using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase 5.4-E3: reflection wrapper over the real dialog API so we can finalize a boss dialog the same way the
    /// game does, instead of hard-hiding UI or nulling CurrentSpeakable. Confirmed by decompilation:
    ///   - NodeCanvas.DialogueTrees.DialogueTree (ParadoxNotion.dll) has static `currentDialogue` and inherits
    ///     `Graph.Stop(bool success = true)`; Stop() raises OnGraphStoped -> DialogueTree.OnDialogueFinished,
    ///     which DialogController handles by unlocking and calling SetCurrentSpeakable(null).
    ///   - DialogController (PerfectRandom.Sulfur.Core) exposes `CurrentSpeakable` (DialogSpeaker) and a static
    ///     `Stack&lt;DialogController&gt; instances`. We read those only for diagnostics.
    /// Everything is best-effort and never throws — if the dialog assembly/shape differs, we log and no-op.
    /// </summary>
    internal static class BossDialogReflect
    {
        private static bool _resolved;
        private static Type? _dialogueTreeType;
        private static PropertyInfo? _currentDialogueProp;
        private static MethodInfo? _stopMethod;

        private static Type? _dialogControllerType;
        private static FieldInfo? _instancesField;
        private static PropertyInfo? _currentSpeakableProp;
        private static FieldInfo? _inChoicesField;
        private static FieldInfo? _selectedButtonField;
        private static FieldInfo? _playerDialogButtonsField;
        private static MethodInfo? _acceptMethod;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _dialogueTreeType = AccessTools.TypeByName("NodeCanvas.DialogueTrees.DialogueTree");
                if (_dialogueTreeType != null)
                {
                    _currentDialogueProp = _dialogueTreeType.GetProperty("currentDialogue",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    // Graph.Stop(bool success = true) — match the (bool) overload.
                    _stopMethod = AccessTools.Method(_dialogueTreeType, "Stop", new[] { typeof(bool) });
                }

                _dialogControllerType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.DialogController");
                if (_dialogControllerType != null)
                {
                    _instancesField = _dialogControllerType.GetField("instances",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    _currentSpeakableProp = _dialogControllerType.GetProperty("CurrentSpeakable",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    // The mid-choice flags Graph.Stop leaves dangling (see ResetDialogChoiceState). AccessTools.Field
                    // walks the base types so a field declared private on a base class is still found (a plain
                    // GetField on the derived type would miss it — Log321 run1: the reset silently no-op'd).
                    _inChoicesField = AccessTools.Field(_dialogControllerType, "InChoices");
                    _selectedButtonField = AccessTools.Field(_dialogControllerType, "selectedButton");
                    _acceptMethod = AccessTools.Method(_dialogControllerType, "AcceptDialogOption");
                    _playerDialogButtonsField = AccessTools.Field(_dialogControllerType, "playerDialogButtons");
                    Plugin.Log.Info($"[BossDialogCommit] dialog choice fields: InChoices={( _inChoicesField != null)} selectedButton={(_selectedButtonField != null)}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[BossDialogCommit] dialog API resolve failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>True if a NodeCanvas dialogue is currently running on this end.</summary>
        public static bool IsDialogActive()
        {
            Resolve();
            try { return _currentDialogueProp?.GetValue(null, null) is UnityEngine.Object o && o != null; }
            catch { return false; }
        }

        /// <summary>The current dialog speaker's actor name (for diagnostics), or "unavailable".</summary>
        public static string CurrentSpeakableName()
        {
            Resolve();
            try
            {
                if (_instancesField?.GetValue(null) is System.Collections.IEnumerable insts)
                {
                    foreach (var dc in insts)
                    {
                        var spk = _currentSpeakableProp?.GetValue(dc, null);
                        if (spk == null) continue;
                        var name = BossReflect.GetMember(spk, "ActorName") as string;
                        if (!string.IsNullOrEmpty(name)) return name!;
                        var unit = BossReflect.GetMember(spk, "unit");
                        if (unit != null) return unit.GetType().Name;
                        return spk.GetType().Name;
                    }
                }
            }
            catch { }
            return "unavailable";
        }

        /// <summary>Finalize the currently-running dialog via the real Graph.Stop(true), exactly as the game does
        /// when a dialogue tree completes. Returns true if a dialog was active and stopped.</summary>
        public static bool TryFinalizeCurrentDialog(out string detail)
        {
            Resolve();
            try
            {
                if (_currentDialogueProp == null || _stopMethod == null) { detail = "dialog API unavailable"; return false; }
                var current = _currentDialogueProp.GetValue(null, null);
                if (current is UnityEngine.Object uo && uo != null)
                {
                    string speaker = CurrentSpeakableName();
                    _stopMethod.Invoke(current, new object[] { true });
                    ResetDialogChoiceState();
                    detail = $"stopped dialog (speaker={speaker})";
                    return true;
                }
                detail = "no active dialog";
                return false;
            }
            catch (Exception ex)
            {
                detail = $"finalize failed: {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}";
                return false;
            }
        }

        /// <summary>Advance the active dialog one step, exactly as the game's advance input does, so a linear dialog
        /// plays through to its NATURAL end — which runs the dialogue tree's terminal ExecuteFunction nodes (the boss
        /// start/airstrike/AI-resume calls live there as data, not code). <c>Graph.Stop</c> aborts and skips those, which
        /// freezes the boss (Log324). Returns true if an active dialog was advanced. Used to fast-forward the host's own
        /// mid-fight dialog to completion when a client dismisses it, so the boss resumes for everyone.</summary>
        public static bool TryAdvanceActiveDialog()
        {
            Resolve();
            try
            {
                if (_acceptMethod == null) return false;
                // The static `instances` stack does NOT reliably hold the live controller (same gap the reset hit —
                // FindObjectsOfTypeAll is what actually reaches it), so scan every live DialogController.
                foreach (var dc in EnumerateControllers())
                {
                    if (_currentSpeakableProp?.GetValue(dc, null) == null) continue; // only the controller actually showing a dialog
                    _acceptMethod.Invoke(dc, null);
                    return true;
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossDialogCommit] TryAdvanceActiveDialog failed: {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}"); }
            return false;
        }

        /// <summary>True if the active dialog is currently showing a multiple-choice (its options panel). A linear line
        /// advances via <see cref="TryAdvanceActiveDialog"/>; a choice node needs an option picked (<see
        /// cref="TrySelectActiveDialogOption"/>) — AcceptDialogOption no-ops on a choice with no selectedButton.</summary>
        public static bool IsActiveDialogInChoices()
        {
            Resolve();
            try
            {
                foreach (var dc in EnumerateControllers())
                {
                    if (_currentSpeakableProp?.GetValue(dc, null) == null) continue;
                    return _inChoicesField != null && _inChoicesField.GetValue(dc) is bool ic && ic;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Pick option <paramref name="index"/> on the active choice dialog (clamped), driving it past the choice
        /// node to its natural continuation — the same as the player clicking that option. Invokes the option Button's
        /// onClick (→ Finalize → SelectOption), which runs the tree's post-choice ExecuteFunction nodes (boss resume), so a
        /// client dismissing a multiple-choice mid-fight dialog can close the host's copy the same way a linear one does.
        /// Returns true if an option was invoked. (F4-DLGCHOICE — host auto-picks option 0; boss taunt choices converge.)</summary>
        public static bool TrySelectActiveDialogOption(int index)
        {
            Resolve();
            try
            {
                foreach (var dc in EnumerateControllers())
                {
                    if (_currentSpeakableProp?.GetValue(dc, null) == null) continue;
                    if (!(_playerDialogButtonsField?.GetValue(dc) is System.Collections.IList buttons) || buttons.Count == 0) return false;
                    int idx = index < 0 ? 0 : (index >= buttons.Count ? buttons.Count - 1 : index);
                    var btn = buttons[idx];
                    if (btn == null) return false;
                    // Button.onClick (UnityEvent) — invoke via reflection to avoid a hard UnityEngine.UI reference.
                    var onClick = btn.GetType().GetProperty("onClick")?.GetValue(btn);
                    var invoke = onClick?.GetType().GetMethod("Invoke", Type.EmptyTypes);
                    if (invoke == null) return false;
                    invoke.Invoke(onClick, null);
                    return true;
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossDialogCommit] TrySelectActiveDialogOption failed: {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}"); }
            return false;
        }

        // Every live DialogController: the static instance stack plus FindObjectsOfTypeAll (the stack can miss the active
        // one). De-duplicated.
        private static System.Collections.Generic.IEnumerable<object> EnumerateControllers()
        {
            var seen = new System.Collections.Generic.HashSet<object>();
            System.Collections.Generic.List<object> all = new System.Collections.Generic.List<object>();
            try { if (_instancesField?.GetValue(null) is System.Collections.IEnumerable insts) foreach (var dc in insts) if (dc != null) all.Add(dc); } catch { }
            try { if (_dialogControllerType != null) foreach (var dc in Resources.FindObjectsOfTypeAll(_dialogControllerType)) if (dc != null) all.Add(dc); } catch { }
            foreach (var dc in all) if (seen.Add(dc)) yield return dc;
        }

        /// <summary>Clear the mid-choice state (<c>InChoices</c> / <c>selectedButton</c>) on every DialogController.
        /// Accepting a choice normally clears these, but a forced <c>Graph.Stop</c> tears the dialog down without going
        /// through that path, so the flags dangle and the NEXT dialog opens stuck in "waiting for a choice" mode — its
        /// opening statement can't be advanced and it never reaches its own choice node. Log320: a client-first intro
        /// finalize left the host's <c>InChoices=True</c>, freezing the P2 airstrike dialog (no MultipleChoiceRequest,
        /// interactable stayed False). Called right after every forced finalize so a torn-down dialog leaves clean state.</summary>
        private static void ResetDialogChoiceState()
        {
            try
            {
                int n = 0, total = 0;
                foreach (var dc in EnumerateControllers())
                {
                    total++;
                    // Resolve the fields on the instance's ACTUAL type (walking base types) — a plain lookup on the
                    // declared DialogController type misses a field that is private on a base class or shadowed on a
                    // subclass, which is why Log322's reset silently no-op'd despite the FieldInfos resolving.
                    var fIn = FindInstanceField(dc, "InChoices");
                    var fSel = FindInstanceField(dc, "selectedButton");
                    string before = fIn != null ? fIn.GetValue(dc)?.ToString() ?? "?" : "no-field";
                    try { fIn?.SetValue(dc, false); } catch { }
                    try { fSel?.SetValue(dc, null); } catch { }
                    string after = fIn != null ? fIn.GetValue(dc)?.ToString() ?? "?" : "no-field";
                    if (before != after) n++;
                    Plugin.Log.Info($"[BossDialogCommit] reset choice state on {dc.GetType().Name} InChoices {before}->{after}");
                }
                Plugin.Log.Info($"[BossDialogCommit] ResetDialogChoiceState cleared={n}/{total}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossDialogCommit] ResetDialogChoiceState failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // Find an instance field by name on the object's ACTUAL runtime type, walking base types (incl. private bases).
        private static FieldInfo? FindInstanceField(object obj, string name)
        {
            for (Type? t = obj.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }
    }
}
