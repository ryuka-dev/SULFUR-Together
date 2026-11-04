using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Utilities;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// BM-1: a boss fight's music reaches every end.
    /// <para>Boss music is started by a scene <c>MusicTrigger</c> (StartMusic / EnableLoopSingleTrack / PlayNextSong /
    /// StopMusic), and for several bosses that call sits on a path only the end that started the fight runs. Lucia's
    /// arena trigger (<c>PlayerTrigger_StartEvent</c>) fires <c>Npc.Interact</c> + <c>MusicTrigger.StartMusic</c> +
    /// <c>EnableLoopSingleTrack</c> when a player walks through it; a client brought in by the host-authoritative start
    /// (the arena entry teleport) never crosses it, and the entrance's own <c>StopMusic</c> trigger has already silenced
    /// its level music — so it fought Lucia in silence (round 546). Cousin's music comes from an animation event of the
    /// intro the client can miss (#20).</para>
    /// <para>Capture (host): a postfix on those four methods records each call on a <c>MusicTrigger</c> within
    /// <see cref="BossRadius"/> of a live boss unit (<c>UnitType.Boss</c>) and broadcasts it as a boss discrete event
    /// "Music:&lt;op&gt;:&lt;ordinal&gt;:&lt;arg&gt;" positioned at the trigger. The radius keeps a level's own music cues
    /// local: Lucia's entrance StopMusic is 104 m from her, her start trigger 20 m.</para>
    /// <para>Apply (client): the same call on the local <c>MusicTrigger</c> at that position. These calls are not
    /// idempotent (StartMusic resets the playlist and restarts its first song), and a client may also run the same cue
    /// itself (it walks through the trigger, or its own fight script plays it). So each cue is numbered per
    /// (trigger, op) and every occurrence runs once per end, whichever arrives first: a prefix on the client skips a
    /// local call whose occurrence the mirror already played, and the mirror skips an occurrence the client already
    /// played. A boss that legitimately repeats a cue (PlayNextSong at each phase) keeps every repeat.</para>
    /// </summary>
    internal static class BossMusicSync
    {
        private const float BossRadius = 60f;
        private const float MatchRadius = 0.5f;
        private const string EventPrefix = "Music:";

        private enum Op { Start = 1, Stop = 2, Loop = 3, Next = 4 }

        private static Type? _musicTriggerType;
        private static readonly Dictionary<Op, MethodInfo> _methods = new Dictionary<Op, MethodInfo>();

        // (trigger instance id, op) → highest occurrence already played on this end / seen from the local game.
        private static readonly Dictionary<long, int> _played = new Dictionary<long, int>();
        private static readonly Dictionary<long, int> _localSeen = new Dictionary<long, int>();
        // HOST: (trigger instance id, op) → occurrences broadcast so far.
        private static readonly Dictionary<long, int> _hostOrdinal = new Dictionary<long, int>();
        private static bool _applyingMirror;

        public static void Apply(Harmony harmony)
        {
            try
            {
                _musicTriggerType = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.Triggers.MusicTrigger");
                if (_musicTriggerType == null)
                {
                    Plugin.Log.Warn("[BossMusic] MusicTrigger not found — boss music sync not supported on this game build");
                    return;
                }
                Bind(Op.Start, "StartMusic", Type.EmptyTypes);
                Bind(Op.Stop, "StopMusic", new[] { typeof(float) });
                Bind(Op.Loop, "EnableLoopSingleTrack", new[] { typeof(bool) });
                Bind(Op.Next, "PlayNextSong", new[] { typeof(bool) });
                if (!_methods.ContainsKey(Op.Start))
                {
                    Plugin.Log.Warn("[BossMusic] MusicTrigger.StartMusic() not found — boss music sync not supported on this game build");
                    _methods.Clear();
                    return;
                }

                var pre = new HarmonyMethod(typeof(BossMusicSync).GetMethod(nameof(MusicTrigger_Pre), BindingFlags.Static | BindingFlags.NonPublic));
                var post = new HarmonyMethod(typeof(BossMusicSync).GetMethod(nameof(MusicTrigger_Post), BindingFlags.Static | BindingFlags.NonPublic));
                foreach (var m in _methods.Values) harmony.Patch(m, prefix: pre, postfix: post);
                Plugin.Log.Info($"[BossMusic] patched MusicTrigger ({string.Join(", ", _methods.Values.Select(m => m.Name))})");
            }
            catch (Exception ex) { Plugin.Log.Error($"[BossMusic] patch failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void Bind(Op op, string name, Type[] args)
        {
            var m = AccessTools.Method(_musicTriggerType, name, args);
            if (m != null) _methods[op] = m;
            else Plugin.Log.Warn($"[BossMusic] MusicTrigger.{name} not found — that cue is not synced");
        }

        public static void Clear()
        {
            _played.Clear();
            _localSeen.Clear();
            _hostOrdinal.Clear();
        }

        private static long Key(Component trigger, Op op) => ((long)trigger.GetInstanceID() << 8) | (long)op;

        private static bool TryOp(MethodBase method, out Op op)
        {
            foreach (var kv in _methods)
                if (kv.Value == method) { op = kv.Key; return true; }
            op = default;
            return false;
        }

        // ----------------------------------------------------------------- local game calls

        /// <summary>CLIENT: skip a local cue whose occurrence the host's mirror already played on this trigger.</summary>
        private static bool MusicTrigger_Pre(Component __instance, MethodBase __originalMethod)
        {
            try
            {
                if (_applyingMirror || __instance == null) return true;
                if (NetGameplaySyncBridge.BossMode != NetMode.Client || !NetGameplaySyncBridge.IsSessionActive) return true;
                if (!TryOp(__originalMethod, out var op)) return true;
                long key = Key(__instance, op);
                int seen = (_localSeen.TryGetValue(key, out int s) ? s : 0) + 1;
                _localSeen[key] = seen;
                int played = _played.TryGetValue(key, out int p) ? p : 0;
                if (seen <= played)
                {
                    Plugin.Log.Info($"[BossMusic] client skipped local {__originalMethod.Name} #{seen} on {__instance.name} — already played from the host");
                    return false;
                }
                _played[key] = seen;
            }
            catch { }
            return true;
        }

        /// <summary>HOST: broadcast a cue played on a trigger that belongs to a boss.</summary>
        private static void MusicTrigger_Post(Component __instance, MethodBase __originalMethod, object[] __args, bool __runOriginal)
        {
            try
            {
                if (!__runOriginal || __instance == null) return;
                if (NetGameplaySyncBridge.BossMode != NetMode.Host || !NetGameplaySyncBridge.IsSessionActive) return;
                if (!TryOp(__originalMethod, out var op)) return;
                Vector3 pos = __instance.transform.position;
                if (!IsNearLiveBoss(pos, out string boss)) return;
                if (!NetRunStateBridge.TryGetLocalRunState(out var run) || !run.HasLevel) return;

                long key = Key(__instance, op);
                int ordinal = (_hostOrdinal.TryGetValue(key, out int o) ? o : 0) + 1;
                _hostOrdinal[key] = ordinal;
                float arg = ArgOf(op, __args);
                NetGameplaySyncBridge.BroadcastHostBossDiscreteEvent(new NetBossDiscreteEvent
                {
                    EventName = EventPrefix + (int)op + ":" + ordinal + ":" + arg.ToString("R", CultureInfo.InvariantCulture),
                    HasPos = true, Position = pos,
                    ChapterName = run.ChapterName, LevelIndex = run.LevelIndex, HasSeed = run.HasLevelSeed, Seed = run.LevelSeed,
                });
                Plugin.Log.Info($"[BossMusic] host broadcast {__originalMethod.Name} #{ordinal} arg={arg} trigger={__instance.name} pos={pos:F1} boss={boss}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossMusic] capture failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static float ArgOf(Op op, object[] args)
        {
            if (args == null || args.Length == 0) return 0f;
            switch (op)
            {
                case Op.Stop: return args[0] is float f ? f : 2f;
                case Op.Loop:
                case Op.Next: return args[0] is bool b && b ? 1f : 0f;
                default: return 0f;
            }
        }

        private static bool IsNearLiveBoss(Vector3 pos, out string boss)
        {
            boss = "";
            var gm = GameManager.Instance;
            if (gm == null || gm.aliveNpcs == null) return false;
            foreach (Npc npc in gm.aliveNpcs)
            {
                if (npc == null || npc.UnitType != UnitType.Boss) continue;
                if ((npc.transform.position - pos).sqrMagnitude > BossRadius * BossRadius) continue;
                boss = npc.name;
                return true;
            }
            return false;
        }

        // ----------------------------------------------------------------- mirror (client)

        internal static bool IsMusicEvent(NetBossDiscreteEvent msg)
            => msg?.EventName != null && msg.EventName.StartsWith(EventPrefix, StringComparison.Ordinal);

        /// <summary>CLIENT: play the host's cue on this end's trigger at the same position, unless this end already
        /// played that occurrence. Input comes off the wire: scene, op, ordinal and argument are all checked.</summary>
        internal static void ApplyHostCue(NetBossDiscreteEvent msg)
        {
            try
            {
                if (_musicTriggerType == null || !msg.HasPos) return;
                if (!NetRunStateBridge.TryGetLocalRunState(out var run) || !run.HasLevel) return;
                if (!NetSceneName.SameScene(msg.ChapterName, msg.LevelIndex, run.ChapterName, run.LevelIndex)) return;
                if (msg.HasSeed && run.HasLevelSeed && msg.Seed != run.LevelSeed) return;

                var parts = msg.EventName.Substring(EventPrefix.Length).Split(':');
                if (parts.Length != 3
                    || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int opValue)
                    || !Enum.IsDefined(typeof(Op), opValue)
                    || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ordinal) || ordinal <= 0
                    || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float arg)
                    || float.IsNaN(arg) || float.IsInfinity(arg)) return;
                var op = (Op)opValue;
                if (!_methods.TryGetValue(op, out var method)) return;

                Component? trigger = FindTrigger(msg.Position);
                if (trigger == null)
                {
                    Plugin.Log.Info($"[BossMusic] client has no MusicTrigger near {msg.Position:F1} for {method.Name} #{ordinal}");
                    return;
                }
                long key = Key(trigger, op);
                int played = _played.TryGetValue(key, out int p) ? p : 0;
                if (ordinal <= played)
                {
                    Plugin.Log.Info($"[BossMusic] client already played {method.Name} #{ordinal} on {trigger.name}");
                    return;
                }
                _played[key] = ordinal;

                object[] callArgs = op == Op.Start ? Array.Empty<object>()
                                  : op == Op.Stop ? new object[] { Mathf.Clamp(arg, 0f, 30f) }
                                  : new object[] { arg >= 0.5f };
                _applyingMirror = true;
                try { method.Invoke(trigger, callArgs); }
                finally { _applyingMirror = false; }
                Plugin.Log.Info($"[BossMusic] client played host {method.Name} #{ordinal} on {trigger.name} pos={msg.Position:F1}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossMusic] apply failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static Component? FindTrigger(Vector3 pos)
        {
            Component? best = null;
            float bestSq = MatchRadius * MatchRadius;
            foreach (var o in UnityEngine.Object.FindObjectsByType(_musicTriggerType!, FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!(o is Component c) || c == null) continue;
                float d = (c.transform.position - pos).sqrMagnitude;
                if (d <= bestSq) { bestSq = d; best = c; }
            }
            return best;
        }
    }
}
