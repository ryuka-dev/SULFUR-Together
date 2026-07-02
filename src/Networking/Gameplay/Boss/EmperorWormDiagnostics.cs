using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Emperor phase-1 worm probe (observe-only). Emperor is a multi-entity worm boss; both host and client
    /// load the same scene, so both run <c>EmperorBossFightHelper.OnPlayerSpawned → StartPhase1</c> and each
    /// drives its OWN <c>EmperorBossWorm</c>. The movement is ballistic physics + RNG + local-player-relative
    /// homing, so the two worms diverge immediately (the "double worm"). See Docs/EmperorBossAudit.md for the
    /// full reverse-engineering + the sync design this probe is meant to validate BEFORE any sync code.
    ///
    /// The probe tags every line with side (host/client) so two logs diff to expose divergence. It captures:
    ///   (1) section spawn manifest (count / UnitIds / order / which is vulnerable),
    ///   (2) jump targets (JumpTo args + velocity + head pos) — the smoking gun for divergence,
    ///   (3) weakpoint hits (damage + tail health + lastActiveIndex + section count),
    ///   (4) section destruction (index + resulting count),
    ///   (5) a throttled FixedUpdate position/state timeline (drift magnitude for the streaming decision),
    ///   (6) death / phase-2 handoff.
    /// The client-side suppression is a REVERSIBLE SCAFFOLD (config default OFF): it blocks the local worm's
    /// autonomous <c>StartMovement</c>. It never Destroys anything and only affects EmperorBossWorm.
    /// </summary>
    internal static class EmperorWormDiagnostics
    {
        private static readonly Dictionary<string, float> _lastLog = new Dictionary<string, float>();
        private static readonly HashSet<int> _manifestLogged = new HashSet<int>();
        private const float EventThrottleSeconds = 1.5f;

        private static bool DiagEnabled { get { try { return Plugin.Cfg.EnableEmperorWormDiagnostics.Value; } catch { return false; } } }
        private static bool LogOn { get { try { return Plugin.Cfg.LogBossEncounter.Value; } catch { return false; } } }
        private static float PerfThresholdMs { get { try { return Plugin.Cfg.EmperorWormPerfThresholdMs.Value; } catch { return 6f; } } }

        public static void Apply(Harmony harmony)
        {
            var worm = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.EmperorBossWorm");
            if (worm == null) { Plugin.Log.Info("[EmperorWorm] EmperorBossWorm type not found."); return; }

            HarmonyMethod Post(string m) => new HarmonyMethod(typeof(EmperorWormDiagnostics).GetMethod(m, BindingFlags.Static | BindingFlags.NonPublic));

            // EMP-3a worm head-streaming — FUNCTIONAL, registered FIRST and UNCONDITIONALLY (before the diagnostics
            // gate) so turning EmperorWormDiagnostics off can never re-enable the client-only 1fps worm. One prefix on
            // FixedUpdate splits by role: HOST captures the head (throttled) then runs native movement; a LINKED
            // CLIENT drives its worm from the host head stream + runs the cheap section-follow and SKIPS the native
            // ballistic FixedUpdate (that raw rigidbody physics is the ~1fps — audit §8.5). Host / single-player /
            // unlinked-solo client run the worm normally. Supersedes the EMP-2b StartMovement block (which left the
            // worm invisible). See NetEmperorWormSync.
            PatchNamed(harmony, worm, "FixedUpdate", prefix: Post(nameof(FixedUpdate_WormSync_Pre)));

            // EMP-3b/3c: section-destruction + terminal-death mirroring — FUNCTIONAL, registered unconditionally
            // (same reasoning as FixedUpdate above: must survive EnableEmperorWormDiagnostics being turned off).
            // Host-only postfixes; the client side is driven entirely by the resulting network messages
            // (NetEmperorWormSync.OnSectionDestroyReceived / OnDeathReceived), not by a patch on this type.
            PatchNamed(harmony, worm, "DestroySection", postfix: Post(nameof(DestroySection_FunctionalPost)));
            PatchNamed(harmony, worm, "DeathAnimation", postfix: Post(nameof(DeathAnimation_FunctionalPost)));

            // EMP-4: fight-start (dialog) authority — FUNCTIONAL, registered unconditionally. StartMovement is the
            // worm's fight-start (Log254: the pre-fight dialog's final choice invokes it, per-end). The prefix makes it
            // host-authoritative: a linked client blocks its own local start and waits for the host's broadcast commit,
            // so Initialize/emergence/music begin in step on every end. See NetEmperorWormSync.TryGateFightStart.
            PatchNamed(harmony, worm, "StartMovement", prefix: Post(nameof(StartMovement_FightGate_Pre)));

            if (!DiagEnabled) { Plugin.Log.Info("[EmperorWorm] worm head-sync active; diagnostics off."); return; }

            var evtPost = Post(nameof(Worm_Post));
            var jumpPost = Post(nameof(JumpTo_Post));
            var weakPost = Post(nameof(WeakpointHit_Post));
            var destroyPre = Post(nameof(DestroySection_Pre));

            // Event markers (throttled): coarse lifecycle points. These fire at natural cadence (per jump /
            // ground-hit), NOT per physics frame — so the probe adds no per-FixedUpdate reflection cost.
            PatchNamed(harmony, worm, "StartMovement", postfix: evtPost);
            PatchNamed(harmony, worm, "HitGround", postfix: evtPost);
            PatchNamed(harmony, worm, "StartUndergroundTravel", postfix: evtPost);
            PatchNamed(harmony, worm, "OnDeath", postfix: evtPost);
            PatchNamed(harmony, worm, "DeathAnimation", postfix: evtPost);

            // Detailed captures (validate the audit's divergence / mechanic claims). JumpTo's freeze prefix is
            // already registered above; here we only add the diagnostic postfix.
            PatchNamed(harmony, worm, "JumpTo", postfix: jumpPost);
            PatchNamed(harmony, worm, "WeakpointHit", postfix: weakPost);
            PatchNamed(harmony, worm, "DestroySection", prefix: destroyPre);

            // EMP-1b perf stopwatch: wrap the suspected ground/underground native calls to localize the
            // ground-slam hitch. Stopwatch only; logs ONLY when a call exceeds EmperorWormPerfThresholdMs.
            // NOTE: the always-per-frame methods (FixedUpdate/UpdateWormSections) are intentionally NOT wrapped —
            // wrapping them per physics-substep is what re-introduced the host hitch. Log218 proved the worm
            // methods peak at ~15ms, so the real 1fps hitch is OUTSIDE these methods (native particle/render);
            // the frame watchdog (Plugin.Update → FrameWatchdogTick) catches that.
            {
                var perfPre = Post(nameof(Perf_Pre));
                var perfPost = Post(nameof(Perf_Post));
                foreach (var name in new[] { "UpdateUndergroundTravelVisuals", "SetGroundEnterParticles", "HitGround" })
                    PatchNamed(harmony, worm, name, prefix: perfPre, postfix: perfPost);
                Plugin.Log.Info($"[EmperorWorm] perf stopwatch registered (logs calls > {PerfThresholdMs:F0}ms; frame watchdog on).");
            }

            Plugin.Log.Info("[EmperorWorm] diagnostics registered (manifest/jump/weakpoint/destroy; event-driven, no per-frame sampling; client phase-1 worm head-driven from host).");
        }

        // EMP-3a: role-split FixedUpdate prefix (not config-gated — gameplay, always on).
        //  • HOST: capture the worm head (throttled 20 Hz broadcast) then let the native ballistic FixedUpdate run.
        //  • LINKED CLIENT: drive the worm from the streamed host head + run the cheap UpdateWormSections, then SKIP
        //    the native FixedUpdate (return false) so its expensive rigidbody physics never runs → no 1fps spiral.
        //  • Host / single-player / UNLINKED solo client: run the worm normally.
        private static bool _loggedWormClientDrive;
        private static bool FixedUpdate_WormSync_Pre(object __instance)
        {
            try
            {
                _lastWormTickAt = Time.realtimeSinceStartup; // heartbeat: a worm's FixedUpdate is actually running now
                var mode = NetGameplaySyncBridge.BossMode;
                if (mode == NetMode.Host)
                {
                    NetEmperorWormSync.HostCapture(__instance);
                    return true; // host runs the authoritative worm
                }
                if (mode == NetMode.Client && SULFURTogether.Networking.NetLinkState.ClientLinked)
                {
                    if (!_loggedWormClientDrive)
                    {
                        _loggedWormClientDrive = true;
                        Plugin.Log.Info($"[EmperorWorm] client worm head-driven (linked client): skipping native FixedUpdate on {Identify(__instance)}; head streamed from host, sections follow locally.");
                    }
                    NetEmperorWormSync.DriveClientWorm(__instance);
                    return false; // skip native ballistic FixedUpdate → no client physics spiral
                }
            }
            catch { }
            return true; // unlinked solo client / anything else: run normally
        }

        // EMP-4: fight-start (dialog) gate. Returns false to block a linked client's local StartMovement (deferred to
        // the host's authoritative commit); true otherwise (single-player, unlinked, host, or our own reentry invoke).
        private static bool StartMovement_FightGate_Pre(object __instance)
            => NetEmperorWormSync.TryGateFightStart(__instance);

        // EMP-3b: host-only. A real DestroySection just ran natively (called from the host's real WeakpointHit) —
        // broadcast it so every linked client mirrors the same destroy on its own worm.
        private static void DestroySection_FunctionalPost(object __instance, int index)
        {
            try
            {
                if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                NetEmperorWormSync.HostAnnounceSectionDestroy(__instance, index);
            }
            catch { }
        }

        // EMP-3c: host-only. The real DeathAnimation coroutine was just kicked off (host's WeakpointHit hit lethal
        // health) — broadcast immediately (not 5s later) so clients start their own mirror in step.
        private static void DeathAnimation_FunctionalPost(object __instance)
        {
            try
            {
                if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                NetEmperorWormSync.HostAnnounceDeath(__instance);
            }
            catch { }
        }

        private static void PatchNamed(Harmony harmony, Type worm, string name, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            foreach (var mi in AccessTools.GetDeclaredMethods(worm).Where(m => m.Name == name && !m.IsStatic))
            {
                try { harmony.Patch(mi, prefix: prefix, postfix: postfix); }
                catch (Exception ex) { Plugin.Log.Error($"[EmperorWorm] probe patch {name} failed: {ex.Message}"); }
            }
        }

        private static void Worm_Post(object __instance, MethodBase __originalMethod, bool __runOriginal)
        {
            try
            {
                if (!Ready(__instance, __runOriginal)) return;
                string method = __originalMethod?.Name ?? "?";
                // Cheap state for the frame watchdog (no reflection): mark worm active + last ground event.
                switch (method)
                {
                    case "StartMovement": _wormActive = true; break;
                    case "HitGround":
                    case "StartUndergroundTravel": _lastGroundEventTime = Time.realtimeSinceStartup; break;
                    case "OnDeath":
                    case "DeathAnimation": _wormActive = false; break;
                }
                if (Throttled(BossReflect.InstanceId(__instance) + "|" + method, EventThrottleSeconds)) return;
                Plugin.Log.Info($"[EmperorWorm] {method} | {Describe(__instance)}");
            }
            catch { }
        }

        private static bool _wormActive;
        private static float _lastGroundEventTime = -999f;
        private static float _lastFrameLogTime = -999f;
        // Heartbeat: set every frame a worm's FixedUpdate actually ticks. The old _wormActive flag was set true on
        // StartMovement and only cleared on death — so after a reload-without-death it stayed true forever and the
        // watchdog mis-tagged ALL later hitches (loading, hub, next fight) as worm hitches (Log263: 80% of
        // [EmperorWormFrame] lines had sinceGroundEvent > 2s = not actually in worm combat). A timeout auto-clears it.
        private static float _lastWormTickAt = -999f;
        private static bool WormTicking => Time.realtimeSinceStartup - _lastWormTickAt < 0.5f;

        private static int _lastGc0 = -1;

        /// <summary>EMP-1b frame watchdog (driven from Plugin.Update). Ultra-cheap: one unscaledDeltaTime read +
        /// compare per frame. Logs a slow frame (a real hitch) only while an Emperor worm is active, and reports
        /// how long after the last ground/underground event it landed — so we can confirm the 1fps hitch is the
        /// ground-enter particle/render cost (invisible to method-level stopwatches).</summary>
        /// <summary>EMP-1b Update profiler: logs a breakdown only when the mod's Update body is slow, so a 1fps
        /// frame is attributed to a specific tick — or, if this stays small while the frame watchdog reports a
        /// hitch, the cost is OUTSIDE Update (LateUpdate/FixedUpdate Harmony patches, native render, or GC).</summary>
        public static void ReportUpdateProfile(long totalMs, long gameplayMs, long bossMs)
        {
            try
            {
                if (!WormTicking || !DiagEnabled) return;
                if (totalMs < 50) return;
                Plugin.Log.Info($"[UpdateProf] side={Side()} updateBody={totalMs}ms (gameplayTick={gameplayMs}ms bossTick={bossMs}ms) — if the frame hitch is bigger than this, the cost is outside Update (LateUpdate/render/GC)");
            }
            catch { }
        }

        public static void FrameWatchdogTick()
        {
            try
            {
                if (!DiagEnabled) return;
                if (!WormTicking) return;
                float dt = Time.unscaledDeltaTime;
                if (dt < 0.12f) return; // ~<8 fps only
                float now = Time.realtimeSinceStartup;
                if (now - _lastFrameLogTime < 0.05f) return; // avoid double-logging the same stall
                _lastFrameLogTime = now;
                float sinceGround = now - _lastGroundEventTime;
                int gc0 = GC.CollectionCount(0);
                int gc0Delta = _lastGc0 < 0 ? 0 : gc0 - _lastGc0;
                _lastGc0 = gc0;
                Plugin.Log.Info($"[EmperorWormFrame] side={Side()} hitch dt={dt * 1000f:F0}ms (~{(dt > 0 ? 1f / dt : 0f):F0}fps) gc0Δ={gc0Delta} sinceGroundEvent={sinceGround * 1000f:F0}ms");
            }
            catch { }
        }

        // (EMP-6e reload-residue census removed after it did its job: Log264/265 showed NO leak — always exactly
        //  liveWorms=1 / liveSpiders=1 / liveLegs=8 / liveHelpers=1 across every reload + hub round-trip. The host 1fps
        //  is a SINGLE worm's native ballistic physics doing long-distance sweeps to chase an out-of-position host, not
        //  accumulated instances. The census's per-3s Resources.FindObjectsOfTypeAll was itself the periodic hitch.)

        // JumpTo(Vector3 targetPosition, float jumpTime): the per-jump destination. Host vs client target lists
        // will NOT match — the empirical proof that the worm must be host-streamed.
        private static void JumpTo_Post(object __instance, Vector3 targetPosition, float jumpTime, bool __runOriginal)
        {
            try
            {
                if (!Ready(__instance, __runOriginal)) return;
                LogManifestOnce(__instance);
                BossReflect.TryGetInt(__instance, "currentJumpCount", out int jumps);
                Vector3 head = HeadPos(__instance);
                Vector3 vel = Velocity(__instance);
                Plugin.Log.Info($"[EmperorWorm] JumpTo side={Side()} target={targetPosition:F1} jumpTime={jumpTime:F2} vel={vel:F1} head={head:F1} jumpCount={jumps} tailHp={TailHealth(__instance):F3}");
            }
            catch { }
        }

        // WeakpointHit(Unit unit, float damage, DamageSourceData src): the only damage entry (tail section).
        // Confirms whether the client's WeakpointHit fires (double-worm) and how destruction tracks health.
        private static void WeakpointHit_Post(object __instance, float damage, bool __runOriginal)
        {
            try
            {
                if (!Ready(__instance, __runOriginal)) return;
                BossReflect.TryGetInt(__instance, "lastActiveIndex", out int lastActive);
                Plugin.Log.Info($"[EmperorWorm] WeakpointHit side={Side()} dmg={damage:F1} tailHp={TailHealth(__instance):F3} lastActiveIndex={lastActive} sections={SectionCount(__instance)} inst={BossReflect.InstanceId(__instance)}");
            }
            catch { }
        }

        // DestroySection(int index): host-authoritative section-destruction event to mirror later.
        private static void DestroySection_Pre(object __instance, int index)
        {
            try
            {
                if (!DiagEnabled || !LogOn) return;
                Plugin.Log.Info($"[EmperorWorm] DestroySection side={Side()} index={index} sectionsBefore={SectionCount(__instance)} lastActiveIndex={ReadInt(__instance, "lastActiveIndex")} tailHp={TailHealth(__instance):F3}");
            }
            catch { }
        }

        // EMP-1b perf stopwatch: __state carries the start timestamp; postfix logs only slow calls.
        private static void Perf_Pre(out long __state) => __state = System.Diagnostics.Stopwatch.GetTimestamp();

        private static void Perf_Post(long __state, MethodBase __originalMethod, object __instance)
        {
            try
            {
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - __state) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (ms < PerfThresholdMs) return;
                BossReflect.TryGetBool(__instance, "isUnderground", out bool under);
                Plugin.Log.Info($"[EmperorWormPerf] {__originalMethod?.Name} side={Side()} ms={ms:F1} underground={under} sections={SectionCount(__instance)}");
            }
            catch { }
        }

        // ----- helpers -----

        private static bool Ready(object inst, bool runOriginal) => DiagEnabled && LogOn && inst != null && runOriginal;

        private static string Side() => NetGameplaySyncBridge.BossMode.ToString();

        private static bool Throttled(string key, float window)
        {
            float now = Time.realtimeSinceStartup;
            if (_lastLog.TryGetValue(key, out float last) && (now - last) < window) return true;
            _lastLog[key] = now;
            return false;
        }

        private static Vector3 HeadPos(object worm) => (worm is Component c && c != null) ? c.transform.position : Vector3.zero;

        private static Vector3 Velocity(object worm)
        {
            try
            {
                var rb = BossReflect.GetMember(worm, "rb") as Rigidbody;
                if (rb == null) return Vector3.zero;
                // Unity renamed Rigidbody.velocity → linearVelocity; read whichever this build exposes via reflection.
                var p = typeof(Rigidbody).GetProperty("linearVelocity") ?? typeof(Rigidbody).GetProperty("velocity");
                if (p != null && p.GetValue(rb) is Vector3 v) return v;
            }
            catch { }
            return Vector3.zero;
        }

        private static float TailHealth(object worm)
        {
            try
            {
                var tail = BossReflect.GetMember(worm, "lastSectionNpc");
                if (tail != null && BossReflect.TryCallFloat(tail, "GetNormalizedHealth", out float h)) return h;
            }
            catch { }
            return -1f;
        }

        private static int SectionCount(object worm)
            => (BossReflect.GetMember(worm, "wormSections") is System.Collections.ICollection c) ? c.Count : -1;

        private static int ReadInt(object worm, string field) => BossReflect.TryGetInt(worm, field, out int v) ? v : -1;

        // One-shot per worm: the section spawn manifest (validates seq-binding + same UnitIds/order both ends).
        private static void LogManifestOnce(object worm)
        {
            try
            {
                if (!(worm is Component)) return;
                int id = BossReflect.InstanceId(worm);
                if (!_manifestLogged.Add(id)) return;
                if (!(BossReflect.GetMember(worm, "wormNpcs") is System.Collections.IEnumerable npcs)) return;
                BossReflect.TryGetInt(worm, "lastActiveIndex", out int lastActive);
                BossReflect.TryGetInt(worm, "numberOfSections", out int total);
                var ids = new List<string>();
                int k = 0;
                foreach (var u in npcs)
                {
                    string uid = BossReflect.ReadUnitId(u);
                    ids.Add($"[{k}]={(string.IsNullOrEmpty(uid) ? "?" : uid)}");
                    k++;
                }
                Plugin.Log.Info($"[EmperorWorm] manifest side={Side()} inst={id} numberOfSections={total} spawned={k} vulnerableIndex={lastActive} units={string.Join(",", ids)}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[EmperorWorm] manifest-once failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        public static string Identify(object worm)
        {
            string root = BossReflect.RootName(worm);
            int id = BossReflect.InstanceId(worm);
            string path = BossReflect.GameObjectPath(worm);
            string mode = NetGameplaySyncBridge.BossMode.ToString();
            return $"root={root} inst={id} path={path} side={mode}";
        }

        public static string Describe(object worm)
        {
            BossReflect.TryGetBool(worm, "bossActive", out bool active);
            BossReflect.TryGetBool(worm, "isUnderground", out bool under);
            BossReflect.TryGetBool(worm, "isTravelingUnderground", out bool travel);
            BossReflect.TryGetInt(worm, "currentJumpCount", out int jumps);
            BossReflect.TryGetInt(worm, "numberOfSections", out int sections);
            int sectionCtrls = (BossReflect.GetMember(worm, "sectionControllers") is System.Collections.ICollection c) ? c.Count : -1;
            int npcs = (BossReflect.GetMember(worm, "wormNpcs") is System.Collections.ICollection c2) ? c2.Count : -1;
            Vector3 pos = (worm is Component cc && cc != null) ? cc.transform.position : Vector3.zero;
            return $"{Identify(worm)} bossActive={active} underground={under} traveling={travel} jumps={jumps} sections={sections} sectionCtrls={sectionCtrls} wormNpcs={npcs} tailHp={TailHealth(worm):F3} pos={pos:F1}";
        }

        /// <summary>One-shot scene scan that explains how many worms exist and which side drives them.</summary>
        public static void LogWormManifest()
        {
            try
            {
                var worm = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.EmperorBossWorm");
                if (worm == null) return;
                var all = Resources.FindObjectsOfTypeAll(worm);
                var live = all.Where(o => o is Component cc && cc != null && cc.gameObject.scene.IsValid() && cc.gameObject.scene.isLoaded).ToList();
                Plugin.Log.Info($"[EmperorWorm] manifest: {live.Count} live EmperorBossWorm instance(s)");
                foreach (var o in live) Plugin.Log.Info($"[EmperorWorm]   - {Describe(o)}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[EmperorWorm] manifest failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
