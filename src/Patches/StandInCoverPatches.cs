using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Units.AI;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// HR-7 (HOST): enemies behave towards a remote player who slipped out of sight as they do towards the host. The
    /// host's AI sees a remote player as that peer's stand-in unit, whose <c>Unit.isPlayer</c> is false, and the
    /// "go to the target's last known position" behaviour graph splits on <c>AiAgent.IsHostileTargetPlayer</c>: against
    /// a player an Offensive enemy pushes to the last known position and a Defensive one looks for cover facing it
    /// (<c>TryFindDefensivePosition</c>); against anything else it just walks to the position. So enemies never took
    /// cover from a client. Patched, each by a transpiler that replaces one read and changes nothing else:
    /// <list type="bullet">
    /// <item>the graph's two gates (<c>BehaviourTreeCode_Gameplay.G_GoToKnownPositionOfLastTarget_Sequencer_3</c> and
    /// <c>_Sequencer_14</c>, the only readers of <c>IsHostileTargetPlayer</c>): a live stand-in target counts as a player;</item>
    /// <item><c>AiAgent.TryFindDefensivePosition</c> and <c>ResetDefensive</c> (which counts failed attempts, three and the
    /// enemy stops trying, only against a player): the same, through their one read of <c>Unit.isPlayer</c>.
    /// <c>TryFindDefensivePosition</c> indexes the agent's position reports by the target without checking the lookup,
    /// so a stand-in counts there only once the agent has a report for it (it always has one for a real player);</item>
    /// <item><c>AiAgent.OnDefensiveAnotherTestPathComplete</c>, the cover search: it rates spots against the target's
    /// <c>EyesPosition</c>, which for a plain unit is its feet; for a stand-in it is its camera root, as for a player's
    /// camera.</item>
    /// </list>
    /// The flag itself is never written: <c>Unit.EyesPosition</c> reads the player camera when <c>isPlayer</c> is true,
    /// which a stand-in does not have. Enemy aiming, which also reads <c>EyesPosition</c>, is not touched.
    /// </summary>
    internal static class StandInCoverPatches
    {
        public static void Apply(Harmony harmony)
        {
            // The two gates and TryFindDefensivePosition only work together: with a gate opened but the cover search
            // still refusing a stand-in, a Defensive enemy would neither take cover nor walk to the last known
            // position. If any of the three fails (a game update), all three are removed again.
            var tree = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.BehaviourTreeCode_Gameplay");
            var core = new List<MethodBase>();
            int coreOk = 0;
            coreOk += Patch(harmony, tree, "G_GoToKnownPositionOfLastTarget_Sequencer_3", nameof(Gate_Transpiler), core);
            coreOk += Patch(harmony, tree, "G_GoToKnownPositionOfLastTarget_Sequencer_14", nameof(Gate_Transpiler), core);
            coreOk += Patch(harmony, typeof(AiAgent), "TryFindDefensivePosition", nameof(TryFind_Transpiler), core);
            if (coreOk != 3)
            {
                foreach (var m in core)
                    try { harmony.Unpatch(m, HarmonyPatchType.Transpiler, harmony.Id); } catch { }
                Plugin.Log.Warn($"[RemoteHit] AI target checks: {coreOk}/3 core patches applied — all removed; enemies treat remote players as before");
                return;
            }
            int ok = coreOk;
            ok += Patch(harmony, typeof(AiAgent), "ResetDefensive", nameof(Reset_Transpiler), null);
            ok += Patch(harmony, typeof(AiAgent), "OnDefensiveAnotherTestPathComplete", nameof(CoverEyes_Transpiler), null);
            if (ok == 5) Plugin.Log.Info("[RemoteHit] Patched 5 AI checks (enemies treat a remote player who left their sight like the host).");
            else Plugin.Log.Warn($"[RemoteHit] AI target checks: {ok}/5 patched — cover against remote players is degraded");
        }

        private static int Patch(Harmony harmony, Type? type, string method, string transpiler, List<MethodBase>? patched)
        {
            try
            {
                var m = type == null ? null : AccessTools.DeclaredMethod(type, method);
                if (m == null) { Plugin.Log.Warn($"[RemoteHit] {type?.Name ?? "?"}.{method} not found"); return 0; }
                harmony.Patch(m, transpiler: new HarmonyMethod(typeof(StandInCoverPatches).GetMethod(transpiler, BindingFlags.Static | BindingFlags.NonPublic)));
                patched?.Add(m);
                return _lastPatched ? 1 : 0;
            }
            catch (Exception ex) { Plugin.Log.Warn($"[RemoteHit] {type?.Name ?? "?"}.{method} hook failed: {ex.GetType().Name}: {ex.Message}"); return 0; }
        }

        private static bool _lastPatched;
        private static readonly FieldInfo IsPlayerField = AccessTools.Field(typeof(Unit), nameof(Unit.isPlayer));
        private static readonly MethodInfo HostileTargetPlayerGetter = AccessTools.PropertyGetter(typeof(AiAgent), nameof(AiAgent.IsHostileTargetPlayer));
        private static readonly MethodInfo EyesPositionGetter = AccessTools.PropertyGetter(typeof(Unit), nameof(Unit.EyesPosition));

        /// <summary>[.., AiAgent] callvirt get_IsHostileTargetPlayer → [.., AiAgent] call IsHostileTargetPlayerOrStandIn.</summary>
        private static IEnumerable<CodeInstruction> Gate_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
            => ReplaceOne(instructions, original.Name, c => c.Calls(HostileTargetPlayerGetter),
                new[] { new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(StandInCoverPatches), nameof(IsHostileTargetPlayerOrStandIn))) });

        /// <summary>[.., Unit] callvirt get_EyesPosition → [.., Unit] call CoverEyesPosition.</summary>
        private static IEnumerable<CodeInstruction> CoverEyes_Transpiler(IEnumerable<CodeInstruction> instructions)
            => ReplaceOne(instructions, "OnDefensiveAnotherTestPathComplete", c => c.Calls(EyesPositionGetter),
                new[] { new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(StandInCoverPatches), nameof(CoverEyesPosition))) });

        /// <summary>[.., Unit] ldfld isPlayer → [.., Unit] ldarg.0 call TreatAsPlayerForCover(Unit, AiAgent).</summary>
        private static IEnumerable<CodeInstruction> TryFind_Transpiler(IEnumerable<CodeInstruction> instructions)
            => Replace(instructions, "TryFindDefensivePosition",
                new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(StandInCoverPatches), nameof(TreatAsPlayerForCover))) });

        /// <summary>[.., Unit] ldfld isPlayer → [.., Unit] call IsPlayerOrStandIn(Unit).</summary>
        private static IEnumerable<CodeInstruction> Reset_Transpiler(IEnumerable<CodeInstruction> instructions)
            => Replace(instructions, "ResetDefensive",
                new[] { new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(StandInCoverPatches), nameof(IsPlayerOrStandIn))) });

        private static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, string method, CodeInstruction[] with)
            => ReplaceOne(instructions, method, c => c.opcode == OpCodes.Ldfld && Equals(c.operand, IsPlayerField), with);

        /// <summary>Replace the single instruction matching <paramref name="match"/>; leave the method alone (and say so)
        /// when there is not exactly one.</summary>
        private static IEnumerable<CodeInstruction> ReplaceOne(IEnumerable<CodeInstruction> instructions, string method,
            Func<CodeInstruction, bool> match, CodeInstruction[] with)
        {
            var list = instructions.ToList();
            int at = -1, count = 0;
            for (int i = 0; i < list.Count; i++)
                if (match(list[i])) { count++; at = i; }
            _lastPatched = false;
            if (count != 1)
            {
                Plugin.Log.Warn($"[RemoteHit] {method}: expected one match for the stand-in check, found {count} — left unchanged");
                return list;
            }
            with[0].labels.AddRange(list[at].labels);
            with[0].blocks.AddRange(list[at].blocks);
            list.RemoveAt(at);
            list.InsertRange(at, with);
            _lastPatched = true;
            return list;
        }

        private static bool IsStandIn(Unit unit)
            => NetConfig.GetMode() == NetMode.Host && RemotePlayerRegistryManager.IsGhostPlayer(unit);

        public static bool IsPlayerOrStandIn(Unit unit)
            => unit.isPlayer || IsStandIn(unit);

        // Diagnostics only (the periodic hit summary prints them).
        public static int GateStandIn, CoverSearchStandIn;

        public static bool IsHostileTargetPlayerOrStandIn(AiAgent agent)
        {
            if (agent.IsHostileTargetPlayer) return true;
            var target = agent.hostileTarget;
            if (target == null || !IsStandIn(target)) return false;
            GateStandIn++;
            return true;
        }

        public static UnityEngine.Vector3 CoverEyesPosition(Unit unit)
        {
            // A stand-in removed while the path request was in flight (its peer left): no position to rate cover
            // against. Vanilla would throw here; the search then finds no cover and the agent resets as usual.
            if (unit == null) return UnityEngine.Vector3.zero;
            if (unit != null && !unit.isPlayer && IsStandIn(unit))
                return unit.transform.position + UnityEngine.Vector3.up * RemotePlayerRegistryManager.CameraRootHeight;
            return unit!.EyesPosition;
        }

        public static bool TreatAsPlayerForCover(Unit unit, AiAgent agent)
        {
            if (unit.isPlayer) return true;
            try
            {
                bool ok = unit != null && agent != null && IsStandIn(unit) && agent.positionReportSet.TryGetUnitIndex(unit, out _);
                if (ok) CoverSearchStandIn++;
                return ok;
            }
            catch { return false; }
        }
    }
}
