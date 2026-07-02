using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// EMP-6a: Emperor phase-2 SPIDER probe (observe-only — NO sync feature code, same probe-first discipline the
    /// worm used, see Docs/EmperorBossAudit.md §8/§9). Phase 2 begins when the phase-1 worm dies
    /// (<c>DeathAnimation → EmperorBossFightHelper.StartPhase2()</c>, already mirrored by EMP-3c) which activates the
    /// pre-placed spider. Both ends then run their OWN <c>EmperorBossSpider</c>; unlike the worm it is NOT ballistic —
    /// it is kinematic (<c>rb.isKinematic=true</c> every frame) and walks a FIXED <c>waypoints</c> path at a speed
    /// proportional to the distance to the LOCAL player (MaintainDistance/MoveAlongPath). So the two spiders diverge in
    /// PATH PROGRESS, not by physics RNG — the natural fix is EMP-3a-style head/root streaming, not a physics stopgap.
    ///
    /// This probe validates the sync model BEFORE any sync code. It tags every line with side (host/client) so two
    /// logs diff to expose divergence, and answers the open unknowns:
    ///   (1) who calls <c>Initialize()</c> / <c>StandUp()</c> and when (public, no C# caller in the DLL → animation
    ///       event / scene UnityEvent) — one-shot stack traces, same trick as the worm's StartMovement (Log254);
    ///   (2) does the client's StartPhase2 (via the EMP-3c worm-death mirror) actually produce an initialized,
    ///       standing spider? (does Initialize/StandUp fire on the client at all?);
    ///   (3) damage routing — the spider npc is PRE-PLACED but activated late; the roster [ClientHit] log cross-refs
    ///       whether a client hit ACCEPTs (rides the roster path) or quarantines like the worm tail (needs a bespoke
    ///       EMP-3d-style route);
    ///   (4) position/path divergence magnitude (waypoint index + speed + pos timeline, throttled, no per-frame alloc);
    ///   (5) defend-phase (50% health, 3 s invuln), damage-particle stages (0.4/0.25/0.1) + rapid-fire, per-end timing;
    ///   (6) the two-stage death (OnNpcDeath walk-to-death → ExecuteActualDeath blow-hole/desert transition).
    /// </summary>
    internal static class EmperorSpiderDiagnostics
    {
        private static readonly Dictionary<string, float> _lastLog = new Dictionary<string, float>();
        private static readonly HashSet<int> _oneShot = new HashSet<int>();
        private const float EventThrottleSeconds = 1.5f;
        private const float PosSampleSeconds = 0.5f;
        private static float _lastPosSampleAt = -999f;

        private static bool DiagEnabled { get { try { return Plugin.Cfg.EnableEmperorSpiderDiagnostics.Value; } catch { return false; } } }
        private static bool LogOn { get { try { return Plugin.Cfg.LogBossEncounter.Value; } catch { return false; } } }

        public static void Apply(Harmony harmony)
        {
            var spider = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.EmperorBossSpider");
            if (spider == null) { Plugin.Log.Info("[EmperorSpider] EmperorBossSpider type not found."); return; }

            HarmonyMethod Post(string m) => new HarmonyMethod(typeof(EmperorSpiderDiagnostics).GetMethod(m, BindingFlags.Static | BindingFlags.NonPublic));

            // ---- EMP-6b FUNCTIONAL patches (registered UNCONDITIONALLY, before the diagnostics gate, so turning the
            //      probe off can never disable the sync). See NetEmperorSpiderSync. ----
            //  • LateUpdate prefix: HOST captures the body transform (throttled broadcast); a LINKED CLIENT applies the
            //    streamed pose before native runs. Returns true either way (legs/IK + CheckDeathTriggerReached must run).
            PatchNamed(harmony, spider, "LateUpdate",         prefix: Post(nameof(LateUpdate_Sync_Pre)));
            //  • MaintainDistance prefix: a linked client never advances the path locally (host stream drives it).
            PatchNamed(harmony, spider, "MaintainDistance",   prefix: Post(nameof(MaintainDistance_Suppress_Pre)));
            //  • Initialize prefix: host-authoritative fight-start gate (EMP-4 analog) — all ends stand up together.
            PatchNamed(harmony, spider, "Initialize",         prefix: Post(nameof(Initialize_Gate_Pre)));
            //  • OnNpcDeath / TriggerDefendPhase postfix: host broadcasts the discrete mechanic; client replays it.
            PatchNamed(harmony, spider, "OnNpcDeath",         postfix: Post(nameof(OnNpcDeath_Announce_Post)));
            PatchNamed(harmony, spider, "TriggerDefendPhase", postfix: Post(nameof(TriggerDefendPhase_Announce_Post)));
            // Rocket launcher rapid-fire (≤10 % health) — host broadcasts, client replays.
            var launcherType = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.EmperorBossSpiderRocketLauncher");
            if (launcherType != null)
                PatchNamed(harmony, launcherType, "ActivateRapidFire", postfix: Post(nameof(ActivateRapidFire_Announce_Post)));
            Plugin.Log.Info("[EmperorSpider] EMP-6b sync patches registered (transform stream / fight-start gate / damage / defend / rapid-fire / death).");

            if (!DiagEnabled) { Plugin.Log.Info("[EmperorSpider] sync active; observe-only diagnostics off."); return; }

            // Lifecycle markers (postfix, one-shot / throttled) — no functional change.
            PatchNamed(harmony, spider, "Awake",                postfix: Post(nameof(Awake_Post)));
            PatchNamed(harmony, spider, "Initialize",           postfix: Post(nameof(Initialize_Post)));
            PatchNamed(harmony, spider, "StandUp",              postfix: Post(nameof(StandUp_Post)));
            PatchNamed(harmony, spider, "OnDamageTaken",        postfix: Post(nameof(OnDamageTaken_Post)));
            PatchNamed(harmony, spider, "TriggerDefendPhase",   postfix: Post(nameof(TriggerDefendPhase_Post)));
            PatchNamed(harmony, spider, "OnNpcDeath",           postfix: Post(nameof(OnNpcDeath_Post)));
            PatchNamed(harmony, spider, "ExecuteActualDeath",   postfix: Post(nameof(ExecuteActualDeath_Post)));
            PatchNamed(harmony, spider, "BlowHoleFromAnimation",postfix: Post(nameof(BlowHole_Post)));
            // Position/state timeline (postfix on LateUpdate; hard-throttled by a direct float compare — the common
            // path is one time-compare, no dict lookup, no string alloc; reflection only fires at ~2 Hz).
            PatchNamed(harmony, spider, "LateUpdate",           postfix: Post(nameof(LateUpdate_Post)));

            // Rockets: per-end firing (each launcher targets the LOCAL player/camera). Throttled marker.
            var launcher = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.EmperorBossSpiderRocketLauncher");
            if (launcher != null)
                PatchNamed(harmony, launcher, "LaunchMissile", postfix: Post(nameof(LaunchMissile_Post)));

            Plugin.Log.Info("[EmperorSpider] EMP-6a probe registered (observe-only: lifecycle / startup-caller / position-divergence / defend / death / rockets).");
        }

        // ---- EMP-6b functional patch handlers (always registered) ----

        private static bool LateUpdate_Sync_Pre(object __instance)
        {
            try
            {
                var mode = NetGameplaySyncBridge.BossMode;
                if (mode == NetMode.Host) NetEmperorSpiderSync.HostCapture(__instance);
                else if (mode == NetMode.Client && SULFURTogether.Networking.NetLinkState.ClientLinked)
                    NetEmperorSpiderSync.DriveClientSpider(__instance);
            }
            catch { }
            return true; // never skip native LateUpdate (legs/IK + death-check must run); movement is suppressed separately
        }

        private static bool MaintainDistance_Suppress_Pre()
            => NetEmperorSpiderSync.SuppressClientMaintainDistance();

        private static bool Initialize_Gate_Pre(object __instance)
            => NetEmperorSpiderSync.TryGateFightStart(__instance);

        private static void OnNpcDeath_Announce_Post(object __instance)
        {
            try { if (NetGameplaySyncBridge.BossMode == NetMode.Host) NetEmperorSpiderSync.HostAnnounceDeath(__instance); }
            catch { }
        }

        private static void TriggerDefendPhase_Announce_Post(object __instance)
        {
            try { if (NetGameplaySyncBridge.BossMode == NetMode.Host) NetEmperorSpiderSync.HostAnnounceDefend(__instance); }
            catch { }
        }

        private static void ActivateRapidFire_Announce_Post()
        {
            try { if (NetGameplaySyncBridge.BossMode == NetMode.Host) NetEmperorSpiderSync.HostAnnounceRapidFire(); }
            catch { }
        }

        // ---- lifecycle (observe-only) ----

        private static void Awake_Post(object __instance)
        {
            if (!Ready(__instance)) return;
            Plugin.Log.Info($"[EmperorSpider] Awake | {Manifest(__instance)}");
        }

        private static void Initialize_Post(object __instance, bool __runOriginal)
        {
            if (!Ready(__instance) || !__runOriginal) return; // blocked by the fight-start gate → didn't actually run
            LogCallerOnce(__instance, "Initialize");
            Plugin.Log.Info($"[EmperorSpider] Initialize | {Describe(__instance)}");
        }

        private static void StandUp_Post(object __instance)
        {
            if (!Ready(__instance)) return;
            LogCallerOnce(__instance, "StandUp");
            Plugin.Log.Info($"[EmperorSpider] StandUp (now vulnerable) | {Describe(__instance)}");
        }

        private static void OnDamageTaken_Post(object __instance, float damage)
        {
            if (!Ready(__instance)) return;
            if (Throttled(BossReflect.InstanceId(__instance) + "|dmg", EventThrottleSeconds)) return;
            BossReflect.TryGetBool(__instance, "hasTriggeredDefend", out bool defended);
            Plugin.Log.Info($"[EmperorSpider] OnDamageTaken side={Side()} dmg={damage:F1} hp={HealthFraction(__instance):F3} triggeredDefend={defended}");
        }

        private static void TriggerDefendPhase_Post(object __instance)
        {
            if (!Ready(__instance)) return;
            Plugin.Log.Info($"[EmperorSpider] TriggerDefendPhase side={Side()} hp={HealthFraction(__instance):F3}");
        }

        private static void OnNpcDeath_Post(object __instance)
        {
            if (!Ready(__instance)) return;
            if (!_oneShot.Add(BossReflect.InstanceId(__instance) * 31 + 1)) return;
            Plugin.Log.Info($"[EmperorSpider] OnNpcDeath (walk-to-death begins) side={Side()} | {Describe(__instance)}");
        }

        private static void ExecuteActualDeath_Post(object __instance)
        {
            if (!Ready(__instance)) return;
            if (!_oneShot.Add(BossReflect.InstanceId(__instance) * 31 + 2)) return;
            Plugin.Log.Info($"[EmperorSpider] ExecuteActualDeath (real death / checkpoint) side={Side()} | {Describe(__instance)}");
        }

        private static void BlowHole_Post(object __instance)
        {
            if (!Ready(__instance)) return;
            if (!_oneShot.Add(BossReflect.InstanceId(__instance) * 31 + 3)) return;
            Plugin.Log.Info($"[EmperorSpider] BlowHoleFromAnimation (desert transition seam, animation event) side={Side()}");
        }

        // ---- position / state timeline (throttled, alloc-free common path) ----

        private static void LateUpdate_Post(object __instance)
        {
            if (!DiagEnabled || !LogOn) return;
            float now = Time.realtimeSinceStartup;
            if (now - _lastPosSampleAt < PosSampleSeconds) return; // cheap: single float compare on the hot path
            _lastPosSampleAt = now;
            try
            {
                if (!(__instance is Component c) || c == null) return;
                BossReflect.TryGetInt(__instance, "currentWaypointIndex", out int cur);
                BossReflect.TryGetInt(__instance, "targetWaypointIndex", out int tgt);
                BossReflect.TryGetFloat(__instance, "currentSpeed", out float spd);
                BossReflect.TryGetBool(__instance, "isInStartupAnimation", out bool startup);
                BossReflect.TryGetBool(__instance, "isDefending", out bool defending);
                BossReflect.TryGetBool(__instance, "isWalkingToDeath", out bool toDeath);
                BossReflect.TryGetBool(__instance, "isDead", out bool dead);
                BossReflect.TryGetBool(__instance, "stoodUp", out bool stood);
                Plugin.Log.Info($"[EmperorSpider] state side={Side()} pos={c.transform.position:F1} wp={cur}->{tgt} spd={spd:F1} hp={HealthFraction(__instance):F3} startup={startup} stood={stood} defending={defending} toDeath={toDeath} dead={dead}");
            }
            catch { }
        }

        private static float _lastRocketLogAt = -999f;
        private static void LaunchMissile_Post()
        {
            if (!DiagEnabled || !LogOn) return;
            float now = Time.realtimeSinceStartup;
            if (now - _lastRocketLogAt < 2f) return;
            _lastRocketLogAt = now;
            Plugin.Log.Info($"[EmperorSpider] rocket LaunchMissile side={Side()} (targets local player/camera)");
        }

        // ---- helpers ----

        private static bool Ready(object inst) => DiagEnabled && LogOn && inst != null;

        private static string Side() => NetGameplaySyncBridge.BossMode.ToString();

        private static bool Throttled(string key, float window)
        {
            float now = Time.realtimeSinceStartup;
            if (_lastLog.TryGetValue(key, out float last) && (now - last) < window) return true;
            _lastLog[key] = now;
            return false;
        }

        /// <summary>Fraction of max health (npc.GetCurrentHealth()/maxHealthValue), matching the game's own defend/particle
        /// gates. -1 if not resolvable yet (before Initialize captures maxHealthValue).</summary>
        private static float HealthFraction(object spider)
        {
            try
            {
                var npc = BossReflect.GetMember(spider, "npc");
                if (npc == null || !BossReflect.TryCallFloat(npc, "GetCurrentHealth", out float cur)) return -1f;
                BossReflect.TryGetFloat(spider, "maxHealthValue", out float max);
                return (max > 0.0001f) ? cur / max : -1f;
            }
            catch { return -1f; }
        }

        // One-shot stack trace: who invokes Initialize/StandUp (public, no C# caller → animation event / scene
        // UnityEvent). Same technique that pinned the worm's StartMovement to a NodeCanvas dialog action (Log254).
        private static void LogCallerOnce(object spider, string method)
        {
            try
            {
                if (!_oneShot.Add(BossReflect.InstanceId(spider) * 131 + method.GetHashCode())) return;
                var frames = new System.Diagnostics.StackTrace().GetFrames();
                var chain = frames == null ? "?" :
                    string.Join(" <- ", frames.Take(10)
                        .Select(f => f.GetMethod())
                        .Where(m => m != null)
                        .Select(m => $"{m.DeclaringType?.Name}.{m.Name}"));
                Plugin.Log.Info($"[EmperorSpider] {method} caller chain side={Side()}: {chain}");
            }
            catch { }
        }

        private static string Manifest(object spider)
        {
            try
            {
                int left  = (BossReflect.GetMember(spider, "leftClaws")  is Array la) ? la.Length : -1;
                int right = (BossReflect.GetMember(spider, "rightClaws") is Array ra) ? ra.Length : -1;
                int wps   = (BossReflect.GetMember(spider, "waypoints")  is System.Collections.ICollection wc) ? wc.Count : -1;
                var npc = BossReflect.GetMember(spider, "npc");
                string npcId = BossReflect.ReadUnitId(npc);
                // spawnableUnit is itself a UnitId struct (not a Unit) → read its own value field / ToString directly.
                var addUnit = BossReflect.GetMember(spider, "spawnableUnit");
                string addId = (BossReflect.GetMember(addUnit, "value") ?? addUnit)?.ToString() ?? "?";
                return $"side={Side()} inst={BossReflect.InstanceId(spider)} legs={left}L/{right}R waypoints={wps} npcUnit={(string.IsNullOrEmpty(npcId) ? "?" : npcId)} addUnit={addId}";
            }
            catch (Exception ex) { return $"manifest-failed: {ex.Message}"; }
        }

        private static string Describe(object spider)
        {
            BossReflect.TryGetBool(spider, "isInStartupAnimation", out bool startup);
            BossReflect.TryGetBool(spider, "stoodUp", out bool stood);
            BossReflect.TryGetBool(spider, "isDefending", out bool defending);
            BossReflect.TryGetBool(spider, "isWalkingToDeath", out bool toDeath);
            BossReflect.TryGetBool(spider, "isDead", out bool dead);
            Vector3 pos = (spider is Component c && c != null) ? c.transform.position : Vector3.zero;
            return $"side={Side()} inst={BossReflect.InstanceId(spider)} pos={pos:F1} hp={HealthFraction(spider):F3} startup={startup} stood={stood} defending={defending} toDeath={toDeath} dead={dead}";
        }

        private static void PatchNamed(Harmony harmony, Type type, string name, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            foreach (var mi in AccessTools.GetDeclaredMethods(type).Where(m => m.Name == name && !m.IsStatic))
            {
                try { harmony.Patch(mi, prefix: prefix, postfix: postfix); }
                catch (Exception ex) { Plugin.Log.Error($"[EmperorSpider] probe patch {name} failed: {ex.Message}"); }
            }
        }
    }
}
