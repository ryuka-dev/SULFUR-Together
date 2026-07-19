using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// EM-Boss probe (observe-only). Endless-mode bosses are NOT plain wave enemies: each is an
    /// <c>Endless</c> variant controller sharing the story bosses' <c>BossFightHelper</c> / <c>BossPhase</c>
    /// machinery (verified against 0.18.5 <c>PerfectRandom.Sulfur.Gameplay.dll</c>):
    /// <list type="bullet">
    ///   <item><c>BossEndlessHelper : BossFightHelper</c> — base for
    ///     <c>CousinEndlessHelper</c>, <c>LuciaBossEndlessHelper</c>, <c>WitchBossEndlessHelper</c>,
    ///     <c>TerrorbaumBossEndlessHelper</c>, <c>DesertClauseBossEndlessHelper</c>.</item>
    ///   <item>Emperor uses bespoke <c>EmperorBossWormEndless</c> / <c>EmperorBossSpiderEndless</c> (not covered here).</item>
    /// </list>
    ///
    /// <para>The boss NPC itself spawns as a normal wave burst (<c>EndlessModeManager.SetEnemy →
    /// UnitSO.SpawnUnitAsync</c>) so its transform/HP/death already ride the EM-2 RuntimeSpawn puppet
    /// mirror. What is NOT mirrored is everything the controller drives: the initial invulnerability gate
    /// (<c>Awake → SetInvulnerable(true)</c>, cleared only in <c>TriggerFight</c>), the animation-event fight
    /// start (<c>Anim_OnStartBossFight → TriggerFight → bossPhases.StartBossPhases</c>), and the multi-phase
    /// state machine (<c>BossPhase.currentPhaseIndex</c> / <c>isTransitioning</c>).</para>
    ///
    /// <para>This probe answers the one question that decides the whole sync strategy: on the CLIENT, does the
    /// boss's <c>BossEndlessHelper</c> run and diverge, or is it inert (enabled=false, driven only as a
    /// puppet)? It snapshots, on both ends at ~2 Hz (log-on-change + a 5 s heartbeat), the controller's
    /// <c>enabled</c>/<c>fightStarted</c>/phase index/transitioning flags plus the boss unit's
    /// invulnerability/health, and whether THIS end classifies the unit as a client puppet. Comparing the two
    /// logs tells us whether to BLOCK the client controller and mirror phases, or REPLAY phase/invuln edges
    /// onto an inert client puppet. Pure diagnostic — no gameplay effect; gated behind
    /// <c>CoopConfig.LogEndlessSync</c> and only while an Endless run is active.</para>
    /// </summary>
    internal static class EndlessBossDiagnostics
    {
        private const float SampleInterval = 0.5f;   // throttle the scan/log cadence
        private const float Heartbeat      = 5.0f;   // re-log an unchanged boss at least this often

        private static bool _resolved;
        private static bool _available;
        private static Type? _bossEndlessHelperType;

        // BossFightHelper (base) fields — declared on the base, inherited by every endless helper.
        private static FieldInfo? _fFightStarted;   // public bool fightStarted
        private static FieldInfo? _fBossPhases;      // protected BossPhase bossPhases
        private static FieldInfo? _fBossUnit;        // protected Npc bossUnit

        // BossPhase members.
        private static FieldInfo? _fCurrentPhaseIndex; // public int currentPhaseIndex
        private static FieldInfo? _fIsTransitioning;   // public bool isTransitioning
        private static FieldInfo? _fCurrentBossPhase;  // public BossCondition currentBossPhase
        private static FieldInfo? _fBossPhaseIndex;    // BossCondition.bossPhaseIndex
        private static FieldInfo? _fPhaseName;         // BossCondition.phaseName

        // Unit members (property-or-field, resolved lazily per boss unit type).
        private static Func<object, string>? _readUnitState;

        // Boss-UI roster (EndlessModeManager.spawnedBosses = the crypt bar's segments).
        private static PropertyInfo? _emInstanceProp;   // static EndlessModeManager.Instance
        private static FieldInfo? _fSpawnedBosses;       // public List<Npc> spawnedBosses
        private static FieldInfo? _fBossProgressBar;     // EndlessModeManager.bossProgressBar : CryptProgressBar (its GameObject.activeSelf = shown)
        private static PropertyInfo? _uiInstanceProp;    // UIManager.Instance (StaticInstance)
        private static FieldInfo? _uiBossUIField;        // UIManager.bossUI : BossHealth (the SEPARATE standard boss bar)
        private static FieldInfo? _bhUnitTrackedField;   // BossHealth.unitTracked : Unit (who the standard bar currently tracks)
        private static string _lastRosterSig = "";

        private static float _nextSample;
        // instanceID -> (last signature, last log time)
        private static readonly Dictionary<int, (string sig, float at)> _seen = new Dictionary<int, (string, float)>();

        public static void Tick()
        {
            try
            {
                if (!LogOn || !EndlessActive) return;

                float now = Time.realtimeSinceStartup;
                if (now < _nextSample) return;
                _nextSample = now + SampleInterval;

                Resolve();
                if (!_available || _bossEndlessHelperType == null) return;

                string role = SafeRole();

                // Log the health-bar roster every tick (even with no active boss helper) so a segment that lingers
                // between/after fights is caught.
                LogBossUiRoster(role);

                var found = Resources.FindObjectsOfTypeAll(_bossEndlessHelperType);
                if (found == null || found.Length == 0) { if (_seen.Count > 0) _seen.Clear(); return; }

                var live = new HashSet<int>();

                foreach (var obj in found)
                {
                    if (obj is not Behaviour helper) continue;
                    var go = helper.gameObject;
                    if (go == null) continue;
                    // Skip prefab assets / non-scene objects — only report instantiated scene bosses.
                    if (!go.scene.IsValid()) continue;

                    int id = helper.GetInstanceID();
                    live.Add(id);

                    bool enabled = helper.enabled;
                    bool fightStarted = ReadBool(_fFightStarted, helper);

                    object? phases = _fBossPhases?.GetValue(helper);
                    int phaseIdx = phases != null ? ReadInt(_fCurrentPhaseIndex, phases) : -1;
                    bool transitioning = phases != null && ReadBool(_fIsTransitioning, phases);
                    string phaseName = ReadPhaseName(phases);

                    object? bossUnit = _fBossUnit?.GetValue(helper);
                    bool invuln = bossUnit != null && ReadUnitBool(bossUnit, "isInvulnerable");
                    bool alive = bossUnit == null || ReadUnitBool(bossUnit, "IsAlive");
                    float hp = bossUnit != null ? ReadUnitFloat(bossUnit, "normalizedHealth") : -1f;
                    bool puppet = bossUnit != null && NetGameplayProbeManager.IsClientEnemyPuppetNpc(bossUnit);

                    string leaf = helper.GetType().Name;
                    string sig = $"{enabled}|{fightStarted}|{phaseIdx}|{transitioning}|{invuln}|{alive}|{puppet}|{phaseName}";

                    bool changed = !_seen.TryGetValue(id, out var prev) || prev.sig != sig;
                    if (!changed && now - prev.at < Heartbeat) continue;
                    _seen[id] = (sig, now);

                    Vector3 p = helper.transform.position;
                    Plugin.Log.Info(
                        $"[Endless] EM-Boss probe: role={role} type={leaf} go='{go.name}' " +
                        $"puppetHere={puppet} enabled={enabled} fightStarted={fightStarted} " +
                        $"phaseIdx={phaseIdx}('{phaseName}') transitioning={transitioning} " +
                        $"invuln={invuln} hp={hp:0.00} alive={alive} pos=({p.x:0.0},{p.y:0.0},{p.z:0.0}) " +
                        $"{(changed ? "[change]" : "[hb]")}");
                }

                // Prune bosses that vanished (dead/despawned) so a re-fight logs fresh.
                if (_seen.Count > 0)
                {
                    var stale = new List<int>();
                    foreach (var kv in _seen) if (!live.Contains(kv.Key)) stale.Add(kv.Key);
                    foreach (var k in stale) _seen.Remove(k);
                }
            }
            catch (Exception ex) { Plugin.Log.Error($"[Endless.EM-Boss] {ex.Message}"); }
        }

        // The actual health-bar composition: EndlessModeManager.spawnedBosses is the list the crypt boss bar splits
        // into segments (one per entry). Logged on change so a lingering/duplicate bar is visible directly (who is
        // attached, and whether that entry is still alive).
        private static void LogBossUiRoster(string role)
        {
            try
            {
                if (_emInstanceProp == null || _fSpawnedBosses == null) return;
                object? mgr = _emInstanceProp.GetValue(null);
                if (mgr == null) return;
                if (_fSpawnedBosses.GetValue(mgr) is not IEnumerable list) return;

                int count = 0;
                var parts = new List<string>();
                foreach (var entry in list)
                {
                    count++;
                    if (entry == null) { parts.Add("<null>"); continue; }
                    // Npc derives from UnityEngine.Object → catch the fake-null (destroyed) case too.
                    if (entry is UnityEngine.Object uo && uo == null) { parts.Add("<destroyed>"); continue; }
                    string nm = entry is Component c && c.gameObject != null ? c.gameObject.name : entry.GetType().Name;
                    bool alive = ReadUnitBool(entry, "IsAlive");
                    parts.Add($"{nm}(alive={alive})");
                }

                // Crypt bar visibility (CryptProgressBar.Show/Hide toggle its GameObject.activeSelf).
                string cryptVis = "?";
                try { if (_fBossProgressBar?.GetValue(mgr) is Component cb && cb != null) cryptVis = cb.gameObject.activeSelf ? "shown" : "hidden"; }
                catch { }

                // The SEPARATE standard boss bar — who (if anyone) it currently tracks.
                string stdBar = "?";
                try
                {
                    object? uiMgr = _uiInstanceProp?.GetValue(null);
                    object? bossUI = uiMgr != null ? _uiBossUIField?.GetValue(uiMgr) : null;
                    object? tracked = bossUI != null ? _bhUnitTrackedField?.GetValue(bossUI) : null;
                    if (tracked == null) stdBar = "<none>";
                    else if (tracked is UnityEngine.Object to && to == null) stdBar = "<destroyed>";
                    else stdBar = tracked is Component tc && tc.gameObject != null ? tc.gameObject.name : tracked.GetType().Name;
                }
                catch { }

                string sig = count + ":" + string.Join(",", parts) + "|crypt=" + cryptVis + "|std=" + stdBar;
                if (sig == _lastRosterSig) return;
                _lastRosterSig = sig;
                Plugin.Log.Info($"[Endless] EM-Boss UI roster: role={role} segments={count} [{string.Join(" | ", parts)}] " +
                                $"cryptBar={cryptVis} stdBossBar={stdBar}");
            }
            catch (Exception ex) { Plugin.Log.Error($"[Endless.EM-Boss roster] {ex.Message}"); }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _bossEndlessHelperType = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.BossEndlessHelper")
                                      ?? AccessTools.TypeByName("BossEndlessHelper");
                var bfh = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.BossFightHelper")
                       ?? AccessTools.TypeByName("BossFightHelper");
                var phase = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.BossPhase")
                         ?? AccessTools.TypeByName("BossPhase");

                if (_bossEndlessHelperType == null || bfh == null)
                {
                    Plugin.Log.Info("[Endless] EM-Boss probe: BossEndlessHelper/BossFightHelper type not found — probe disabled.");
                    return;
                }

                _fFightStarted = AccessTools.Field(bfh, "fightStarted");
                _fBossPhases   = AccessTools.Field(bfh, "bossPhases");
                _fBossUnit     = AccessTools.Field(bfh, "bossUnit");

                if (phase != null)
                {
                    _fCurrentPhaseIndex = AccessTools.Field(phase, "currentPhaseIndex");
                    _fIsTransitioning   = AccessTools.Field(phase, "isTransitioning");
                    _fCurrentBossPhase  = AccessTools.Field(phase, "currentBossPhase");
                    var cond = _fCurrentBossPhase?.FieldType;
                    if (cond != null)
                    {
                        _fBossPhaseIndex = AccessTools.Field(cond, "bossPhaseIndex");
                        _fPhaseName      = AccessTools.Field(cond, "phaseName");
                    }
                }

                // Boss-UI roster source: EndlessModeManager is not under the PerfectRandom.Sulfur.Core namespace (bare
                // name resolves it — same as EndlessModeProbePatches).
                var em = AccessTools.TypeByName("EndlessModeManager")
                      ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.EndlessModeManager");
                if (em != null)
                {
                    _emInstanceProp = AccessTools.Property(em, "Instance");
                    _fSpawnedBosses = AccessTools.Field(em, "spawnedBosses");
                    _fBossProgressBar = AccessTools.Field(em, "bossProgressBar");
                }

                // The SEPARATE standard boss bar (UIManager.bossUI : BossHealth), distinct from the crypt segment bar.
                var ui = AccessTools.TypeByName("UIManager")
                      ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.UIManager");
                if (ui != null)
                {
                    _uiInstanceProp = AccessTools.Property(ui, "Instance"); // StaticInstance<UIManager>.Instance (inherited)
                    _uiBossUIField  = AccessTools.Field(ui, "bossUI");
                }
                var bh = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.UI.BossHealth")
                      ?? AccessTools.TypeByName("BossHealth");
                if (bh != null) _bhUnitTrackedField = AccessTools.Field(bh, "unitTracked");

                _available = _fBossPhases != null && _fBossUnit != null;
                Plugin.Log.Info($"[Endless] EM-Boss probe armed (fields: fightStarted={_fFightStarted != null} " +
                                $"bossPhases={_fBossPhases != null} bossUnit={_fBossUnit != null} " +
                                $"phaseIdx={_fCurrentPhaseIndex != null} transitioning={_fIsTransitioning != null} " +
                                $"emInstance={_emInstanceProp != null} spawnedBosses={_fSpawnedBosses != null}).");
            }
            catch (Exception ex) { Plugin.Log.Error($"[Endless.EM-Boss] resolve failed: {ex.Message}"); }
        }

        private static bool LogOn { get { try { return Plugin.Cfg.LogEndlessSync.Value; } catch { return false; } } }

        private static bool EndlessActive { get { try { return EndlessSyncManager.EndlessActive; } catch { return false; } } }

        private static string SafeRole()
        {
            try { return NetGameplaySyncBridge.BossMode.ToString(); } catch { return "?"; }
        }

        private static bool ReadBool(FieldInfo? f, object owner)
        {
            try { return f?.GetValue(owner) is bool b && b; } catch { return false; }
        }

        private static int ReadInt(FieldInfo? f, object owner)
        {
            try { return f?.GetValue(owner) is int i ? i : -1; } catch { return -1; }
        }

        private static string ReadPhaseName(object? phases)
        {
            try
            {
                if (phases == null || _fCurrentBossPhase == null) return "?";
                object? cond = _fCurrentBossPhase.GetValue(phases);
                if (cond == null) return "<none>";
                if (_fPhaseName?.GetValue(cond) is string s && !string.IsNullOrEmpty(s)) return s;
                if (_fBossPhaseIndex?.GetValue(cond) is int bi) return "#" + bi;
                return "?";
            }
            catch { return "?"; }
        }

        // Unit props (isInvulnerable / IsAlive / normalizedHealth) are property-or-field on the game's
        // Unit type; resolve each lazily and cache the accessor on the concrete unit type.
        private static readonly Dictionary<string, MemberInfo?> _unitMembers = new Dictionary<string, MemberInfo?>();

        private static MemberInfo? UnitMember(object unit, string name)
        {
            Type t = unit.GetType();
            string key = t.FullName + "::" + name;
            if (_unitMembers.TryGetValue(key, out var m)) return m;
            MemberInfo? found = (MemberInfo?)AccessTools.Property(t, name) ?? AccessTools.Field(t, name);
            _unitMembers[key] = found;
            return found;
        }

        private static object? ReadUnitRaw(object unit, string name)
        {
            try
            {
                var m = UnitMember(unit, name);
                if (m is PropertyInfo p) return p.GetValue(unit);
                if (m is FieldInfo f) return f.GetValue(unit);
                return null;
            }
            catch { return null; }
        }

        private static bool ReadUnitBool(object unit, string name)
        {
            return ReadUnitRaw(unit, name) is bool b && b;
        }

        private static float ReadUnitFloat(object unit, string name)
        {
            try { object? v = ReadUnitRaw(unit, name); return v is float f ? f : (v != null ? Convert.ToSingle(v) : -1f); }
            catch { return -1f; }
        }
    }
}
