using System;
using System.Collections.Generic;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// ST-3-DIFF: <b>diagnostic only</b>. Dump the RENDERED state of a frozen-solid corpse on both ends, in one
    /// identical line per unit, so host and client can be put side by side.
    ///
    /// <para><b>Why this exists.</b> Every counter ST-3, ST-3c and ST-3d added measures an INPUT — a message sent, a
    /// status written, a vanilla body executed. All of them read green in Log544 (94 of 96 host deaths frozen-solid,
    /// 70 of 70 shatters replayed) while the maintainer was watching the client show far fewer ice statues than the
    /// host. Three separate mechanisms have now been proposed for that gap and none was confirmed, which is the signal
    /// to stop proposing a fourth: nothing so far reads what the player is actually looking at. This does.</para>
    ///
    /// <para>Keyed by host spawn index and emitted a few frames after the death has fully settled, so the two ends'
    /// lines pair up and every field can be compared directly: the status, the animator (which carries the frozen
    /// POSE), the material (which carries the frost coverage and the solid-ice shader time) and the rigidbody. Whatever
    /// differs is the answer; nothing here changes behaviour.</para>
    /// </summary>
    internal static class FrozenSolidDiffProbe
    {
        /// <summary>Report a few frames after the death so anything the death itself kicks off — the mirrored
        /// <c>Die()</c>, the puppet release, vanilla's own death coroutines — has already landed.</summary>
        private const int ReportDelayFrames = 4;

        /// <summary>A second, later sample. Log19 showed one client corpse already at <c>frozen=98.0</c> while the host
        /// sat at 100: the decay coroutines are still running on a non-shattered corpse, so "de-ices over time on one
        /// end only" is a live possibility that a single early sample cannot see.</summary>
        private const int LateReportDelayFrames = 60;

        /// <summary>Bounded so a pathological session cannot grow this without limit; a frozen-solid death is rare
        /// enough (94 in the busiest round so far) that this is never reached in practice.</summary>
        private const int MaxPending = 64;

        private sealed class Pending
        {
            public Npc?   Unit;
            public int    HostSpawnIndex;
            public int    ReportFrame;
            public bool   Shattered;
            public string Sample = "";
        }

        private static readonly List<Pending> _pending = new List<Pending>();

        // ---- effect transition trail -------------------------------------------------------------------------
        // Log19 localised the divergence to ONE field: the frozen animator bool, absent on the client for exactly the
        // deaths that did NOT shatter (11 of 12) and present on the host for all of them. Only two vanilla methods
        // touch that bool — ApplyEffect sets it, RemoveEffect clears it — so recording which of them ran, in order,
        // with their arguments and the resulting bool, names the culprit instead of guessing at a fourth mechanism.
        // Bounded in both directions and cleared with the rest of the client state.

        private const int MaxTrailUnits = 128;
        private const int MaxTrailChars = 200;
        private static readonly Dictionary<int, string> _trail = new Dictionary<int, string>();

        /// <summary>Diagnostic: append one Frozen effect transition for this unit.</summary>
        public static void NoteTransition(Unit unit, string entry)
        {
            try
            {
                if (unit == null) return;
                int id = unit.GetInstanceID();
                if (!_trail.TryGetValue(id, out string? s))
                {
                    if (_trail.Count >= MaxTrailUnits) return;
                    s = "";
                }
                s = s!.Length == 0 ? entry : s + ">" + entry;
                if (s.Length > MaxTrailChars) s = "…" + s.Substring(s.Length - MaxTrailChars);
                _trail[id] = s;
            }
            catch { }
        }

        private static string Trail(Unit unit)
        {
            try { return _trail.TryGetValue(unit.GetInstanceID(), out string? s) ? s! : "-"; }
            catch { return "?"; }
        }

        /// <summary>Spawn indices whose frozen-solid came from a real shatter (vanilla's roll on the host, the replay
        /// on a client) rather than merely dying with the status at the cap. The two look different in vanilla and
        /// must not be pooled when comparing ends.</summary>
        private static readonly HashSet<int> _shattered = new HashSet<int>();

        public static void NoteShatter(int hostSpawnIndex)
        {
            if (hostSpawnIndex >= 0) _shattered.Add(hostSpawnIndex);
        }

        public static bool DidShatter(int hostSpawnIndex) => hostSpawnIndex >= 0 && _shattered.Contains(hostSpawnIndex);

        public static void Reset()
        {
            _pending.Clear();
            _shattered.Clear();
            _trail.Clear();
        }

        public static void Schedule(object? runtimeObject, int hostSpawnIndex)
        {
            try
            {
                if (runtimeObject is not Npc npc || npc == null) return;
                if (_pending.Count + 2 > MaxPending) return;

                // One death reaches this from two places on a client — the death-mirror apply and the local death
                // report that the mirrored Die() itself raises — which is why Log19 carried two identical client lines
                // per unit. Same unit already queued means the same death.
                for (int i = 0; i < _pending.Count; i++)
                    if (ReferenceEquals(_pending[i].Unit, npc)) return;

                int now = Time.frameCount;
                _pending.Add(new Pending { Unit = npc, HostSpawnIndex = hostSpawnIndex, Shattered = DidShatter(hostSpawnIndex),
                                           ReportFrame = now + ReportDelayFrames,     Sample = "settle" });
                _pending.Add(new Pending { Unit = npc, HostSpawnIndex = hostSpawnIndex, Shattered = DidShatter(hostSpawnIndex),
                                           ReportFrame = now + LateReportDelayFrames, Sample = "late" });
            }
            catch { }
        }

        public static void Tick()
        {
            if (_pending.Count == 0) return;
            int frame = Time.frameCount;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var p = _pending[i];
                if (frame < p.ReportFrame) continue;
                _pending.RemoveAt(i);
                try { Emit(p); }
                catch (Exception ex) { Plugin.Log.Warn($"[FrozenDiff] emit failed: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        private static void Emit(Pending p)
        {
            Npc? npc = p.Unit;
            string role = NetConfig.GetMode() == NetMode.Host ? "Host" : "Client";

            if (npc == null)
            {
                NetLogger.Info($"[FrozenDiff] role={role} sample={p.Sample} idx={p.HostSpawnIndex} shattered={p.Shattered} unit=DESTROYED");
                return;
            }

            // Status
            float frozen = 0f; bool isSolid = false;
            try { frozen = npc.Stats.GetStatus(EntityAttributes.NegativeEffect_Frozen); isSolid = npc.IsFrozenSolid; } catch { }

            // Animator — the frozen POSE lives here, and only DelayedFrozenSolid ever sets it.
            string anim = "animator=null";
            try
            {
                var a = npc.animator;
                if (a != null)
                {
                    string stateTxt = "?";
                    try
                    {
                        var st = a.GetCurrentAnimatorStateInfo(0);
                        stateTxt = $"{st.fullPathHash}@{st.normalizedTime:F2}";
                    }
                    catch { }
                    anim = $"animEnabled={a.enabled} animFrozen={a.GetBool("Frozen")} animDead={SafeGetBool(a, "Dead")} animState={stateTxt}";
                }
            }
            catch { }

            // Material — frost coverage (the effect's own shaderParameter) and the solid-ice switch.
            string shaderParam = SULFURTogether.Patches.UnitStatusPatches.GetFrozenShaderParameter(npc) ?? "";
            string mat = "renderer=null";
            try
            {
                var r = npc.mainRenderer;
                if (r != null && r.material != null)
                {
                    string coverage = string.IsNullOrEmpty(shaderParam)
                        ? "?"
                        : SafeGetFloat(r.material, shaderParam);
                    mat = $"shader[{(string.IsNullOrEmpty(shaderParam) ? "?" : shaderParam)}]={coverage} solidTime={SafeGetFloat(r.material, "_FrozenSolidTime")}";
                }
            }
            catch { }

            // Physics — IsFrozenSolid is what makes vanilla keep a corpse rigid instead of settling it.
            string phys = "rb=null";
            try
            {
                var rb = npc.Rigidbody;
                if (rb != null)
                    phys = $"kinematic={rb.isKinematic} gravity={rb.useGravity} sleeping={rb.IsSleeping()}";
            }
            catch { }

            NetLogger.Info(
                $"[FrozenDiff] role={role} sample={p.Sample} idx={p.HostSpawnIndex} shattered={p.Shattered} unit={npc.name} " +
                $"frozen={frozen:F1} isSolid={isSolid} state={npc.unitState} active={npc.gameObject.activeInHierarchy} " +
                $"{anim} {mat} {phys} trail={Trail(npc)}");
        }

        private static string SafeGetBool(Animator a, string name)
        {
            try { return a.GetBool(name).ToString(); } catch { return "?"; }
        }

        private static string SafeGetFloat(Material m, string name)
        {
            try { return m.HasProperty(name) ? m.GetFloat(name).ToString("F2") : "n/a"; } catch { return "?"; }
        }
    }
}
