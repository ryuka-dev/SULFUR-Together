using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// HR-8: enemy visual states a client never saw, driven by the host. The client's copy of an enemy is a puppet: its
    /// own handlers do not run (the client's hits are settled on the host), and the generic animation mirror copies
    /// only the locomotion / attack / cower parameters outside an attack. Two kinds of state are sent:
    /// <list type="bullet">
    /// <item><b>Reveal.</b> A <c>HellShrewSnipa</c> starts the level hidden and comes out (<c>SetHidden(false)</c>, one-way
    /// through <c>HasBeenDiscovered</c>) when a player hurts it from close by. The host's discovery (see
    /// <c>EnemyRevealPatches</c>) is applied to the client's copy with the same method.</item>
    /// <item><b>Block poses.</b> A <c>CrawSentinel</c> (animator bool <c>Blocking</c>) and a <c>ShavWaWarden</c>
    /// (<c>Block</c>, <c>LoadedGunBlock</c>) block on the host; those bools are read from the host unit's own animator
    /// and copied to the client copy's animator. Visual only — the block's invulnerability is the host's and already
    /// decides the client's hits.</item>
    /// </list>
    /// The host re-sends the current states every few seconds, so a client that was loading, joined late or re-bound
    /// a copy catches up; applying a state twice changes nothing. A state for a copy that is not bound yet is held
    /// until it binds. Everything is dropped with the level.
    /// </summary>
    internal static class EnemyVisualStateSync
    {
        private static readonly string[] SentinelBools = { "Blocking" };
        private static readonly string[] WardenBools = { "Block", "LoadedGunBlock" };
        private const float PollInterval = 0.1f;
        private const float ScanInterval = 2f;
        private const float ResendInterval = 3f;

        public static int HostSent, ClientApplied, ClientHeld;

        // ----------------------------------------------------------------- host

        private sealed class Tracked
        {
            public Component Owner = null!;    // CrawSentinel / ShavWaWarden
            public Animator Animator = null!;
            public int[] Hashes = Array.Empty<int>();
            public bool[] Last = Array.Empty<bool>();
        }

        private static readonly Dictionary<int, Tracked> _tracked = new Dictionary<int, Tracked>();  // by owner instance id
        private static readonly Dictionary<int, NetHostEnemyVisualState> _revealed = new Dictionary<int, NetHostEnemyVisualState>(); // by host spawn index
        private static float _nextPoll, _nextScan, _nextResend;
        private static Type? _sentinelType, _wardenType, _snipaType;
        private static FieldInfo? _sentinelAnimator, _wardenAnimator;
        private static MethodInfo? _snipaSetHidden;
        private static PropertyInfo? _snipaDiscovered;
        private static bool _typesResolved;

        private static void ResolveTypes()
        {
            if (_typesResolved) return;
            _typesResolved = true;
            _sentinelType = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.CrawSentinel");
            _wardenType = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.ShavWaWarden");
            _snipaType = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.HellShrewSnipa");
            _sentinelAnimator = _sentinelType == null ? null : AccessTools.Field(_sentinelType, "animator");
            _wardenAnimator = _wardenType == null ? null : AccessTools.Field(_wardenType, "animator");
            _snipaSetHidden = _snipaType == null ? null : AccessTools.DeclaredMethod(_snipaType, "SetHidden", new[] { typeof(bool) });
            _snipaDiscovered = _snipaType == null ? null : AccessTools.Property(_snipaType, "HasBeenDiscovered");
            if (_sentinelAnimator == null || _wardenAnimator == null || _snipaSetHidden == null || _snipaDiscovered == null)
                NetLogger.Warn("[EnemyVisual] a game member is missing (CrawSentinel/ShavWaWarden animator, HellShrewSnipa) — that state is not synced");
        }

        /// <summary>HOST: a HellShrewSnipa was just discovered (EnemyRevealPatches).</summary>
        public static void OnHostRevealed(object npc)
        {
            try
            {
                if (NetConfig.GetMode() != NetMode.Host || !NetGameplaySyncBridge.IsSessionActive) return;
                var evt = NewEvent(npc, NetHostEnemyVisualState.KindReveal, 0, true);
                if (evt == null) { NetLogger.Warn("[EnemyVisual] discovered unit is not in the roster — reveal not sent"); return; }
                _revealed[evt.HostSpawnIndex] = evt;
                Send(evt);
                NetLogger.Info($"[EnemyVisual] host sent reveal hostIdx={evt.HostSpawnIndex} unit={evt.UnitIdentifier}");
            }
            catch (Exception ex) { NetLogger.Warn($"[EnemyVisual] reveal send failed: {ex.Message}"); }
        }

        /// <summary>HOST: poll the tracked block bools and re-send the current states. Called every frame by the probe
        /// tick; does nothing outside a hosted session.</summary>
        public static void TickHost(float now)
        {
            if (NetConfig.GetMode() != NetMode.Host || !NetGameplaySyncBridge.IsSessionActive) return;
            try
            {
                ResolveTypes();
                if (now >= _nextScan) { _nextScan = now + ScanInterval; Scan(); }
                bool resend = now >= _nextResend;
                if (resend) _nextResend = now + ResendInterval;
                if (now >= _nextPoll || resend)
                {
                    _nextPoll = now + PollInterval;
                    List<int>? gone = null;
                    foreach (var kv in _tracked)
                    {
                        var t = kv.Value;
                        if (t.Owner == null || t.Animator == null) { (gone ??= new List<int>()).Add(kv.Key); continue; }
                        if (!t.Animator.isActiveAndEnabled) continue;
                        for (int i = 0; i < t.Hashes.Length; i++)
                        {
                            bool v = t.Animator.GetBool(t.Hashes[i]);
                            if (v == t.Last[i] && !resend) continue;
                            t.Last[i] = v;
                            var npc = NpcOf(t.Owner);
                            var evt = npc == null ? null : NewEvent(npc, NetHostEnemyVisualState.KindAnimatorBool, t.Hashes[i], v);
                            if (evt != null) Send(evt);
                        }
                    }
                    if (gone != null) foreach (int id in gone) _tracked.Remove(id);
                }
                if (resend) foreach (var evt in _revealed.Values) Send(evt);
            }
            catch (Exception ex) { NetLogger.Warn($"[EnemyVisual] host tick failed: {ex.Message}"); }
        }

        private static void Scan()
        {
            Track(_sentinelType, _sentinelAnimator, SentinelBools);
            Track(_wardenType, _wardenAnimator, WardenBools);
        }

        private static void Track(Type? type, FieldInfo? animatorField, string[] names)
        {
            if (type == null || animatorField == null) return;
            foreach (var o in UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None))
            {
                if (!(o is Component c) || c == null || _tracked.ContainsKey(c.GetInstanceID())) continue;
                if (!(animatorField.GetValue(c) is Animator a) || a == null) continue;
                var hashes = new int[names.Length];
                for (int i = 0; i < names.Length; i++) hashes[i] = Animator.StringToHash(names[i]);
                _tracked[c.GetInstanceID()] = new Tracked { Owner = c, Animator = a, Hashes = hashes, Last = new bool[names.Length] };
            }
        }

        private static object? NpcOf(Component c)
        {
            var npc = c.GetComponentInParent<PerfectRandom.Sulfur.Core.Units.Npc>();
            return npc != null ? npc : null;
        }

        private static NetHostEnemyVisualState? NewEvent(object npc, byte kind, int paramHash, bool value)
        {
            if (!NetGameplayProbeManager.TryGetHostRosterIdentity(npc, out int spawnIndex, out string unitId)) return null;
            if (!NetRunStateBridge.TryGetLocalRunState(out var state) || state == null) return null;
            return new NetHostEnemyVisualState
            {
                ChapterName    = state.ChapterName ?? "",
                LevelIndex     = state.LevelIndex,
                HasLevelSeed   = state.HasLevelSeed,
                LevelSeed      = state.HasLevelSeed ? state.LevelSeed : 0,
                HostSpawnIndex = spawnIndex,
                UnitIdentifier = unitId,
                Kind           = kind,
                ParamHash      = paramHash,
                Value          = value,
            };
        }

        private static void Send(NetHostEnemyVisualState evt)
        {
            NetGameplaySyncBridge.ReportHostEnemyVisualState(evt);
            HostSent++;
        }

        // ----------------------------------------------------------------- client

        // States for copies not bound yet: host spawn index → (kind, param) → event.
        private static readonly Dictionary<int, Dictionary<long, NetHostEnemyVisualState>> _held = new Dictionary<int, Dictionary<long, NetHostEnemyVisualState>>();

        /// <summary>CLIENT: a host state arrived.</summary>
        public static void Process(NetHostEnemyVisualState evt)
        {
            if (evt == null || NetConfig.GetMode() != NetMode.Client) return;
            try
            {
                if (!NetRunStateBridge.TryGetLocalRunState(out var local) || !evt.MatchesScene(local)) return;
                if (NetGameplayProbeManager.IsClientDeadOrDying(evt.HostSpawnIndex)) return;
                if (NetGameplayProbeManager.TryGetClientPuppetNpc(evt.HostSpawnIndex, out var npc)) { Apply(npc, evt, "on event"); return; }
                if (!_held.TryGetValue(evt.HostSpawnIndex, out var byKey)) _held[evt.HostSpawnIndex] = byKey = new Dictionary<long, NetHostEnemyVisualState>();
                byKey[((long)evt.Kind << 32) | (uint)evt.ParamHash] = evt;
                ClientHeld++;
            }
            catch (Exception ex) { NetLogger.Warn($"[EnemyVisual] client apply failed: {ex.Message}"); }
        }

        /// <summary>CLIENT: a puppet was (re)bound for <paramref name="hostIdx"/>.</summary>
        public static void OnClientPuppetBound(int hostIdx, object npc)
        {
            if (_held.Count == 0 || !_held.TryGetValue(hostIdx, out var byKey)) return;
            _held.Remove(hostIdx);
            if (NetGameplayProbeManager.IsClientDeadOrDying(hostIdx)) return;
            foreach (var evt in byKey.Values) Apply(npc, evt, "on bind");
        }

        private static void Apply(object npc, NetHostEnemyVisualState evt, string when)
        {
            try
            {
                ResolveTypes();
                if (!(npc is Component c) || c == null) return;
                if (evt.Kind == NetHostEnemyVisualState.KindReveal)
                {
                    if (_snipaType == null || _snipaSetHidden == null || _snipaDiscovered == null) return;
                    var snipa = c.GetComponentInChildren(_snipaType, true) ?? c.GetComponentInParent(_snipaType);
                    if (snipa == null) return;
                    if (_snipaDiscovered.GetValue(snipa) is bool d && d) return; // already out (a re-send)
                    _snipaSetHidden.Invoke(snipa, new object[] { false });
                    ClientApplied++;
                    NetLogger.Info($"[EnemyVisual] client revealed hostIdx={evt.HostSpawnIndex} ({when})");
                    return;
                }
                if (evt.Kind == NetHostEnemyVisualState.KindAnimatorBool)
                {
                    Animator? a = AnimatorOf(c);
                    if (a == null || !a.isActiveAndEnabled) return;
                    if (a.GetBool(evt.ParamHash) == evt.Value) return;
                    a.SetBool(evt.ParamHash, evt.Value);
                    ClientApplied++;
                }
            }
            catch (Exception ex) { NetLogger.Warn($"[EnemyVisual] hostIdx={evt.HostSpawnIndex} apply failed: {(ex.InnerException ?? ex).Message}"); }
        }

        private static Animator? AnimatorOf(Component npc)
        {
            if (_sentinelType != null && _sentinelAnimator != null && npc.GetComponentInChildren(_sentinelType, true) is Component s && s != null)
                return _sentinelAnimator.GetValue(s) as Animator;
            if (_wardenType != null && _wardenAnimator != null && npc.GetComponentInChildren(_wardenType, true) is Component w && w != null)
                return _wardenAnimator.GetValue(w) as Animator;
            return null;
        }

        // ----------------------------------------------------------------- lifecycle

        /// <summary>Level change / session teardown, both ends.</summary>
        public static void ClearLevel()
        {
            _tracked.Clear();
            _revealed.Clear();
            _held.Clear();
            _nextPoll = _nextScan = _nextResend = 0f;
        }

        public static string FormatSummary() => $"visualSent={HostSent} visualApplied={ClientApplied} visualHeld={ClientHeld}";
    }
}
