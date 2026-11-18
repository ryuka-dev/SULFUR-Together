using System;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Audio;
using PerfectRandom.Sulfur.Core.Units;
using Sonity;
using UnityEngine;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// HR-9: a client sees a <c>HellShrewDigga</c> burrow and erupt and a <c>HellShrewSticka</c> spin, without its copy
    /// taking over any of the host's gameplay state.
    ///
    /// <para><b>Host — the event.</b> The pose changes are method calls on the host: the AI calls <c>Digga.Dig</c>,
    /// <c>Digga.Appear</c>, <c>Sticka.StartWhirlwind</c> and <c>Sticka.StopWhirlwind</c>; the dig clip's animation event
    /// calls <c>Digga.EnterGround</c>; a hit while digging runs <c>Digga.ResetDigIfHitTaken</c>. A postfix on each sends
    /// the action to clients (<see cref="EnemyVisualStateSync.OnHostPoseAction"/>).</para>
    ///
    /// <para><b>Client — the presentation only.</b> <see cref="ApplyOnClient"/> repeats what those methods do to the
    /// animator and the sound, nothing else. The methods themselves must not run on the copy: they switch the unit's
    /// invulnerability, hitboxes, collider, targetability, physics and AI, which the host owns and which already decide
    /// the client's hits.</para>
    ///
    /// <para><b>Client — the copy's own animation events.</b> Once the copy plays the dig, erupt or spin clips, their
    /// animation events fire on it: <c>Digga.EnterGround</c> (invulnerable, untargetable, collider off, digging sound
    /// on), <c>Digga.EruptStart</c> (spawns the erupt shockwave), <c>Sticka.ApplyWhirlwindDamage</c> (area damage) and
    /// <c>Sticka.AllowMovementInWhirlwind</c> (sets the copy's velocity). A prefix skips them on a copy the host drives;
    /// an unbound local unit, which runs its own AI, keeps them. Without this, a copy that dug in stayed invulnerable
    /// and kept its digging sound — the symptom players reported.</para>
    ///
    /// <para>One animation event is deliberately left running: the spin clip's <c>SetInvulnerableFromAnimation(1)</c>.
    /// The host's Sticka is invulnerable while it spins (the deflect players see), so the copy being so too keeps the
    /// client's view honest; the StickaStop replay clears it the way <c>StopWhirlwind</c> does on the host.</para>
    /// </summary>
    // Executes on: Host (capture) | Client (guards, replay)
    internal static class DesertPosePatches
    {
        public enum Action
        {
            DiggaDig = 1,
            DiggaEnterGround = 2,
            DiggaAppear = 3,
            DiggaResetDig = 4,
            StickaStart = 5,
            StickaStop = 6,
        }

        private static Type? _diggaType, _stickaType;
        private static FieldInfo? _diggaNpc, _diggaAnimator, _diggaMoving, _diggaErupt, _diggaBeginClip, _diggaEnterClip;
        private static FieldInfo? _stickaNpc, _stickaAnimator, _stickaSpin;
        private static PropertyInfo? _diggaIsDigging;
        private static MethodInfo? _diggaShake, _diggaRumble;

        public static void Apply(Harmony harmony)
        {
            try
            {
                if (!Plugin.Cfg.EnableDesertPoseSync.Value)
                {
                    Plugin.Log.Info("[DesertPose] desert pose sync disabled by config.");
                    return;
                }
                _diggaType = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.HellShrewDigga");
                _stickaType = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.HellShrewSticka");
                if (_diggaType == null || _stickaType == null)
                {
                    Plugin.Log.Warn("[DesertPose] HellShrewDigga / HellShrewSticka not found — burrow and spin are not shown on clients");
                    return;
                }
                _diggaNpc       = AccessTools.Field(_diggaType, "Npc");
                _diggaAnimator  = AccessTools.Field(_diggaType, "animator");
                _diggaMoving    = AccessTools.Field(_diggaType, "movingSound");
                _diggaErupt     = AccessTools.Field(_diggaType, "eruptSound");
                _diggaBeginClip = AccessTools.Field(_diggaType, "drillBeginClip");
                _diggaEnterClip = AccessTools.Field(_diggaType, "drillEnterGroundClip");
                _diggaIsDigging = AccessTools.Property(_diggaType, "IsDigging");
                _diggaShake     = AccessTools.DeclaredMethod(_diggaType, "TriggerScreenShake", Type.EmptyTypes);
                _diggaRumble    = AccessTools.DeclaredMethod(_diggaType, "PlayRumbleClip", Type.EmptyTypes);
                _stickaNpc      = AccessTools.Field(_stickaType, "npc");
                _stickaAnimator = AccessTools.Field(_stickaType, "animator");
                _stickaSpin     = AccessTools.Field(_stickaType, "spinSound");

                // Guards first: if they cannot be installed, the copy would run the methods the replay sets off, so
                // the replay must stay off too.
                bool guarded =
                    Patch(harmony, _diggaType, "EnterGround", prefix: nameof(Guard_Pre), postfix: nameof(DiggaEnterGround_Post))
                    & Patch(harmony, _diggaType, "EruptStart", prefix: nameof(Guard_Pre))
                    & Patch(harmony, _stickaType, "ApplyWhirlwindDamage", prefix: nameof(Guard_Pre))
                    & Patch(harmony, _stickaType, "AllowMovementInWhirlwind", prefix: nameof(Guard_Pre));
                bool captured =
                    Patch(harmony, _diggaType, "Dig", postfix: nameof(DiggaDig_Post))
                    & Patch(harmony, _diggaType, "Appear", postfix: nameof(DiggaAppear_Post))
                    & Patch(harmony, _diggaType, "ResetDigIfHitTaken", prefix: nameof(DiggaReset_Pre), postfix: nameof(DiggaReset_Post))
                    & Patch(harmony, _stickaType, "StartWhirlwind", postfix: nameof(StickaStart_Post))
                    & Patch(harmony, _stickaType, "StopWhirlwind", postfix: nameof(StickaStop_Post));
                _replayEnabled = guarded && _diggaNpc != null && _diggaAnimator != null && _stickaNpc != null && _stickaAnimator != null;
                if (!_replayEnabled || !captured)
                    Plugin.Log.Warn($"[DesertPose] a HellShrewDigga / HellShrewSticka member is missing (guards={guarded}, capture={captured}) — burrow and spin may not show on clients");
                else
                    Plugin.Log.Info("[DesertPose] Patched HellShrewDigga / HellShrewSticka (host pose actions replayed on clients).");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[DesertPose] Apply failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static bool _replayEnabled;

        // Diagnostics only (read with an inspector); never consulted by the sync.
        public static int HostActionsSent, ClientEventsSkipped;

        private static bool Patch(Harmony harmony, Type type, string method, string? prefix = null, string? postfix = null)
        {
            var m = AccessTools.DeclaredMethod(type, method);
            if (m == null) return false;
            harmony.Patch(m,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(DesertPosePatches).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(DesertPosePatches).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)));
            return true;
        }

        private static Npc? NpcOf(object comp)
        {
            try
            {
                if (_diggaType != null && _diggaType.IsInstanceOfType(comp)) return _diggaNpc?.GetValue(comp) as Npc;
                if (_stickaType != null && _stickaType.IsInstanceOfType(comp)) return _stickaNpc?.GetValue(comp) as Npc;
            }
            catch { }
            return null;
        }

        /// <summary>CLIENT: is this the copy of a host enemy (its state is the host's)? An unbound local unit runs its own
        /// AI and keeps its own animation events.</summary>
        private static bool IsHostDrivenCopy(Npc? npc)
        {
            if (npc == null || NetConfig.GetMode() != NetMode.Client || !NetGameplaySyncBridge.IsSessionActive) return false;
            return NetGameplayProbeManager.IsHostBoundEntity(npc) || NetGameplayProbeManager.IsRuntimeMirroredEntity(npc);
        }

        // ------------------------------------------------------------------ client guards

        private static bool Guard_Pre(object __instance)
        {
            try
            {
                if (!IsHostDrivenCopy(NpcOf(__instance))) return true;
                ClientEventsSkipped++;
                return false;
            }
            catch { return true; }
        }

        // ------------------------------------------------------------------ host capture

        private static void Send(object instance, Action action)
        {
            if (NetConfig.GetMode() != NetMode.Host) return;
            var npc = NpcOf(instance);
            if (npc == null) return;
            EnemyVisualStateSync.OnHostPoseAction(npc, (int)action);
            HostActionsSent++;
        }

        private static void DiggaDig_Post(object __instance) => Send(__instance, Action.DiggaDig);
        private static void DiggaEnterGround_Post(object __instance) => Send(__instance, Action.DiggaEnterGround);
        private static void DiggaAppear_Post(object __instance) => Send(__instance, Action.DiggaAppear);
        private static void StickaStart_Post(object __instance) => Send(__instance, Action.StickaStart);
        private static void StickaStop_Post(object __instance) => Send(__instance, Action.StickaStop);

        // ResetDigIfHitTaken runs on every hit; it only resets when the unit was digging (and not mid unskippable move).
        private static void DiggaReset_Pre(object __instance, out bool __state)
        {
            __state = false;
            if (NetConfig.GetMode() != NetMode.Host) return;
            try { __state = _diggaIsDigging?.GetValue(__instance) is bool d && d; } catch { }
        }

        private static void DiggaReset_Post(object __instance, bool __state)
        {
            if (!__state) return;
            try { if (_diggaIsDigging?.GetValue(__instance) is bool d && !d) Send(__instance, Action.DiggaResetDig); } catch { }
        }

        // ------------------------------------------------------------------ client replay

        /// <summary>CLIENT: repeat the animator and sound part of a host pose action on the copy. Mirrors, line for line,
        /// the presentation statements of the corresponding 1.0.6 method; returns whether something was applied.</summary>
        public static bool ApplyOnClient(Component npc, int actionValue)
        {
            if (!_replayEnabled || npc == null) return false;
            var action = (Action)actionValue;
            bool digga = action >= Action.DiggaDig && action <= Action.DiggaResetDig;
            Type? type = digga ? _diggaType : (action == Action.StickaStart || action == Action.StickaStop ? _stickaType : null);
            if (type == null) return false;
            var comp = npc.GetComponentInChildren(type, true);
            if (comp == null) return false;
            var animator = (digga ? _diggaAnimator : _stickaAnimator)!.GetValue(comp) as Animator;
            bool anim = animator != null && animator.isActiveAndEnabled;
            Transform at = comp.transform;

            switch (action)
            {
                case Action.DiggaDig:
                    if (anim)
                    {
                        animator!.ResetTrigger("Attack");
                        animator.SetBool("IsDigging", true);
                        animator.SetTrigger("Dig");
                    }
                    PlayClip(_diggaBeginClip, comp, at.position);
                    return true;

                case Action.DiggaEnterGround:
                    if (anim)
                    {
                        animator!.ResetTrigger("Land");
                        animator.ResetTrigger("TakeDamage");
                        animator.ResetTrigger("Attack");
                        animator.SetTrigger("EnterGround");
                        animator.SetBool("Hidden", true);
                    }
                    PlayClip(_diggaEnterClip, comp, at.position);
                    SoundOf(_diggaMoving, comp)?.Play(at);
                    return true;

                case Action.DiggaAppear:
                    if (anim)
                    {
                        animator!.ResetTrigger("Land");
                        animator.SetBool("isFalling", false);
                        animator.SetTrigger("Attack");
                        animator.SetBool("Hidden", false);
                    }
                    try { _diggaShake?.Invoke(comp, null); } catch { }   // shakes the local camera by distance — presentation
                    try { _diggaRumble?.Invoke(comp, null); } catch { }
                    SoundOf(_diggaMoving, comp)?.Stop(at);
                    SoundOf(_diggaErupt, comp)?.Play(at);
                    return true;

                case Action.DiggaResetDig:
                    if (anim)
                    {
                        animator!.SetBool("IsDigging", false);
                        animator.SetBool("Hidden", false);
                        animator.ResetTrigger("EnterGround");
                        animator.ResetTrigger("Dig");
                        animator.SetTrigger("Land");
                    }
                    return true;

                case Action.StickaStart:
                    if (anim)
                    {
                        animator!.SetTrigger("TriggerWhirlwind");
                        animator.SetBool("Whirlwind", true);
                    }
                    SoundOf(_stickaSpin, comp)?.Play(at);
                    return true;

                case Action.StickaStop:
                    if (anim)
                    {
                        animator!.SetBool("Whirlwind", false);
                        animator.ResetTrigger("Land");
                        animator.ResetTrigger("TakeDamage");
                    }
                    SoundOf(_stickaSpin, comp)?.Stop(at);
                    // The spin clip's own SetInvulnerableFromAnimation(1) ran on the copy, as it does on the host; the
                    // host's StopWhirlwind clears it, and the copy never runs StopWhirlwind while alive, so clear it here.
                    // Without this the copy kept every hitbox invulnerable after its first spin and the client's shots
                    // could no longer reach the host. This only puts the copy back in step with the host unit.
                    if (npc is Unit unit)
                    {
                        unit.SetInvulnerable(false);
                        unit.SetHitboxesInvulnerable(false);
                    }
                    return true;
            }
            return false;
        }

        private static SoundEvent? SoundOf(FieldInfo? field, object comp)
        {
            try { return field?.GetValue(comp) as SoundEvent; } catch { return null; }
        }

        private static void PlayClip(FieldInfo? field, object comp, Vector3 at)
        {
            try
            {
                if (field?.GetValue(comp) is AudioClip clip && clip != null)
                    StaticInstance<SoundBank>.Instance.PlayWorldPointEffect(clip, at);
            }
            catch { }
        }
    }
}
