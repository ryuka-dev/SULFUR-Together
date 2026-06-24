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
    }
}
