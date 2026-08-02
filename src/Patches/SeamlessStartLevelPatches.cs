using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.UI;
using SULFURTogether.Networking;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// NP-3 — hand the level over to gameplay while the host is still on the press-to-continue screen.
    ///
    /// <para><b>The defect.</b> Phase 5.7-NP removed every pause a co-op session can hit by dropping the four
    /// gameplay pause padlocks, but a host sitting on the post-load black screen still freezes the world for
    /// everyone — and no padlock is involved, so nothing NP does can reach it. <c>LevelGeneration.ShowLevelNode</c>
    /// parks on <c>while (gameManager.awaitingStartLevel) yield return null;</c> and performs the entire handover
    /// only afterwards. While it is parked, five separate things are still off:</para>
    /// <list type="number">
    ///   <item><c>GameState == Loading</c>, so <c>GameManager.Update</c> writes <c>Time.timeScale = 0</c> every frame</item>
    ///   <item><c>Physics.simulationMode</c> has not been returned to <c>FixedUpdate</c></item>
    ///   <item>every NPC in the level is <c>enabled = false</c> and absent from <c>GameManager.aliveNpcs</c></item>
    ///   <item><c>BatchedNPCRaycasts.enabled == false</c> — no line-of-sight / hostile detection</item>
    ///   <item><c>ProjectileSystem.enabled == false</c></item>
    /// </list>
    /// <para>The last one is decisive: <c>ProjectileSystem.Update</c> is the central manual driver, and
    /// <c>BehaviourTreeCode_Gameplay.ManualUpdate</c> — every enemy's behaviour tree — is registered into its
    /// <c>RunWhileWritingInstanceData</c>. A disabled MonoBehaviour's <c>Update</c> never runs, so the AI driver
    /// never runs. This is not a pause we forgot to block; the level simply has not been handed over yet.</para>
    ///
    /// <para><b>The fix.</b> Let the node's tail run on time, then put the presentation back. The tail welds two
    /// jobs together — the simulation handover (state, timeScale, physics, NPCs, both driver components) and the
    /// presentation handover (<c>loadingOverlay</c> hidden, <c>ClearLocks()</c>, <c>LoadingFade(false)</c>). We want
    /// the first and not the second. So for the duration of one <c>MoveNext</c> we report
    /// <c>awaitingStartLevel == false</c> to the coroutine only, which drops it out of the wait and runs the tail
    /// exactly once in vanilla's own order — no reimplementation, and no duplicate <c>aliveNpcs</c> entries, which
    /// is the trap in running a hand-picked subset early. The flag is restored immediately afterwards, so every
    /// other reader still sees a parked peer: the prompt, the Loading/UI action maps, ESC → <c>ExitFromTransition</c>,
    /// and — the reason this matters — AWAIT-2's "lead the parked host away" relay all behave exactly as before.
    /// We then re-raise the fade and overlay, re-apply the controller lock that <c>ClearLocks()</c> just wiped, and
    /// make the player invulnerable, because they are now genuinely standing in a live level behind a black screen.
    /// The real keypress tears all of that down through <see cref="OnAwaitStateChanged"/>.</para>
    ///
    /// <para><b>Deliberately host-only.</b> A parked client freezes nothing for anybody — its enemies are host-driven
    /// puppets — so there is no reason to change its behaviour and every reason not to widen the blast radius.</para>
    ///
    /// <para><b>Accepted cost.</b> Invulnerability stops the player being killed behind the curtain; it does not stop
    /// enemies walking over and waiting for them. That is what "the world does not wait for you" means, and it was
    /// accepted deliberately.</para>
    /// </summary>
    internal static class SeamlessStartLevelPatches
    {
        // True between "we let the tail run" and "the player actually pressed continue / we were pulled away".
        private static bool _curtainUp;
        // Set for the duration of the one MoveNext we lie to; restored in the postfix.
        private static bool _lyingToNode;

        private static FieldInfo? _awaitBackingField;
        private static bool _backingFieldResolved;

        public static int HandoversPerformed;

        /// <summary>True while this peer's level is live behind our own black screen.</summary>
        public static bool IsCurtainUp => _curtainUp;

        // Whether the ShowLevelNode for the level this peer is currently on has already run its tail. AWAIT-3 asks
        // this before arming a stale-node retirement: a node that completed cannot be stale, so arming would only
        // leave the arm waiting out its frame budget against the INCOMING level's legitimate node.
        //
        // It deliberately does NOT track the curtain. The first attempt used `IsCurtainUp`, and Log533 shows why that
        // is the wrong fact: the ordering on a client-led transition is `level handed over` → `black screen dropped
        // (cleared)` → `transition while parked … retiring stale ShowLevelNode` → `arm expired unused after 10
        // frames`. The level-clear site tears the curtain down BEFORE `ArmIfParked` runs, so the guard read false and
        // armed anyway. Nothing broke — AWAIT-3's frame budget exists for exactly that — but it was correct by the
        // safety valve rather than by the guard. This flag is cleared when a NEW node starts waiting, not when the
        // level clears, so it stays true across the whole teardown.
        private static bool _nodeHandedOver;

        /// <summary>True when this peer's current <c>ShowLevelNode</c> has already completed (NP-3 handed the level
        /// over early), so there is no stale coroutine for AWAIT-3 to retire.</summary>
        public static bool HasNodeAlreadyCompleted => _nodeHandedOver;

        public static void Apply(Harmony harmony)
        {
            // Patched onto the same ShowLevelNode state machine AWAIT-3 already uses; resolution lives there.
            var moveNext = AwaitStartLevelPatches.ResolveShowLevelNodeMoveNext();
            if (moveNext == null)
            {
                Plugin.Log.Warn("[SeamlessStart] ShowLevelNode.MoveNext not resolved — host will still freeze the world on the press-to-continue screen.");
                return;
            }

            try
            {
                harmony.Patch(moveNext,
                    prefix:  new HarmonyMethod(typeof(SeamlessStartLevelPatches).GetMethod(nameof(MoveNext_Pre),  BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(SeamlessStartLevelPatches).GetMethod(nameof(MoveNext_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info("[SeamlessStart] patched ShowLevelNode.MoveNext (host hands the level over behind the black screen)");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[SeamlessStart] ShowLevelNode MoveNext patch failed: {ex.Message}");
            }
        }

        /// <summary>Only a linked HOST changes behaviour. Single player and clients keep vanilla timing.</summary>
        private static bool ShouldHandOverEarly()
        {
            try
            {
                if (!Plugin.Cfg.DisablePauseInMultiplayer.Value) return false;
                return NetConfig.GetMode() == NetMode.Host && NetLinkState.HostLinked;
            }
            catch { return false; }
        }

        // ----------------------------------------------------------------- the one-MoveNext lie

        private static bool MoveNext_Pre()
        {
            _lyingToNode = false;
            try
            {
                if (_curtainUp) return true;             // tail already ran for this level
                if (!ShouldHandOverEarly()) return true;

                var gm = StaticInstance<GameManager>.Instance;
                if (gm == null || !gm.awaitingStartLevel) return true;   // not at the prompt — nothing to release

                // A node is parked at the prompt again: whatever we handed over previously belonged to the
                // level we have since left, so the "already completed" fact is retired here rather than at
                // level-clear time (see the comment on _nodeHandedOver).
                _nodeHandedOver = false;

                // Only lie when the flag is ALREADY set on entry. The node itself calls SetAwaitBeforeStartLevel(true)
                // inside a MoveNext; lying across that call would have the postfix restore the pre-call value and
                // silently strip the prompt state the node had just established.
                if (!TrySetAwaitingFlag(false)) return true;
                _lyingToNode = true;
            }
            catch (Exception ex) { Plugin.Log.Warn($"[SeamlessStart] pre failed: {ex.Message}"); }
            return true;
        }

        private static void MoveNext_Post(bool __runOriginal)
        {
            if (!_lyingToNode) return;
            _lyingToNode = false;

            try
            {
                TrySetAwaitingFlag(true);   // every other reader sees a parked peer again

                // AWAIT-3 may have retired this node instead of running it; then there is no handover to dress up.
                if (!__runOriginal) return;

                var gm = StaticInstance<GameManager>.Instance;
                if (gm == null) return;
                // The tail is what takes the game out of Loading. Until it has, the node is still working through
                // the LOD pass ahead of the wait and there is nothing to hide.
                if (gm.gameState == GameState.Loading) return;

                RaiseCurtain(gm);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[SeamlessStart] post failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- our own press-to-continue screen

        private static void RaiseCurtain(GameManager gm)
        {
            if (_curtainUp) return;
            _curtainUp = true;
            _nodeHandedOver = true;   // survives DropCurtain; only a NEW node waiting clears it
            HandoversPerformed++;

            try
            {
                var ui = StaticInstance<UIManager>.Instance;
                if (ui != null)
                {
                    // The node's tail dropped both. TogglePressToContinue is NOT undone by it — only
                    // SetAwaitBeforeStartLevel(false) clears that — so re-showing the overlay brings back the real
                    // prompt, including its save-and-exit affordance, rather than a lookalike of our own.
                    ui.LoadingFade(state: true);
                    ui.loadingOverlay.SetState(UIState.Shown);
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[SeamlessStart] curtain raise (ui) failed: {ex.Message}"); }

            try
            {
                // ClearLocks() in the tail wiped the controller lock the prompt relies on, and the player is now a
                // real body in a live level: hold them still and unkillable until they press continue.
                gm.ModifyControllerLock(LockStatePadlock.Loading, state: true);
                gm.ModifyPlayerInvulnerability(LockStatePadlock.Loading, state: true);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[SeamlessStart] curtain raise (locks) failed: {ex.Message}"); }

            Plugin.Log.Info("[SeamlessStart] level handed over behind the black screen — enemies and boss timelines are live for every peer while this host reads the prompt");
        }

        private static void DropCurtain(string reason)
        {
            if (!_curtainUp) return;
            _curtainUp = false;

            try
            {
                var gm = StaticInstance<GameManager>.Instance;
                if (gm != null)
                {
                    gm.ModifyControllerLock(LockStatePadlock.Loading, state: false);
                    gm.ModifyPlayerInvulnerability(LockStatePadlock.Loading, state: false);
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[SeamlessStart] curtain drop (locks) failed: {ex.Message}"); }

            try
            {
                var ui = StaticInstance<UIManager>.Instance;
                if (ui != null)
                {
                    ui.LoadingFade(state: false);
                    ui.loadingOverlay.SetState(UIState.Hidden);
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[SeamlessStart] curtain drop (ui) failed: {ex.Message}"); }

            Plugin.Log.Info($"[SeamlessStart] black screen dropped ({reason})");
        }

        /// <summary>Postfix of <c>GameManager.SetAwaitBeforeStartLevel</c> (already patched for AWAIT-1 diagnostics).
        /// <c>false</c> is the player pressing continue — or a transition clearing the prompt out from under them, which
        /// wants the same teardown so the lock and the invulnerability never leak into the next level.</summary>
        public static void OnAwaitStateChanged(bool state)
        {
            if (!state) DropCurtain("await cleared");
        }

        /// <summary>Session end / level change safety net — the locks are ours and must not outlive the window.</summary>
        public static void Clear() => DropCurtain("cleared");

        public static string FormatCounters() => $"seamlessHandovers={HandoversPerformed}";

        // ----------------------------------------------------------------- awaitingStartLevel backing field

        // awaitingStartLevel is `{ get; private set; }`, so the flag lives in the compiler-generated backing field.
        // Writing it directly is what keeps the lie scoped to the coroutine: SetAwaitBeforeStartLevel would also
        // toggle the prompt, the action maps and the resume-state save, none of which we want to disturb.
        private static bool TrySetAwaitingFlag(bool value)
        {
            try
            {
                if (!_backingFieldResolved)
                {
                    _backingFieldResolved = true;
                    _awaitBackingField = AccessTools.Field(typeof(GameManager), "<awaitingStartLevel>k__BackingField");
                    if (_awaitBackingField == null)
                        Plugin.Log.Warn("[SeamlessStart] awaitingStartLevel backing field not found — the host will keep freezing the world on the prompt.");
                }
                if (_awaitBackingField == null) return false;
                _awaitBackingField.SetValue(StaticInstance<GameManager>.Instance, value);
                return true;
            }
            catch (Exception ex) { Plugin.Log.Warn($"[SeamlessStart] await flag write failed: {ex.Message}"); return false; }
        }
    }
}
