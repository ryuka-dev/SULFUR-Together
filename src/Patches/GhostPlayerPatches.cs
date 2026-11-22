using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using SULFURTogether.Networking.Gameplay;
using UnityEngine;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Keeps the host's headless remote-player stand-ins (Phase 5.7-B ghosts, registered into
    /// <c>GameManager.Players</c> so vanilla enemy detection and targeting see remote players) out of vanilla
    /// per-player logic that assumes a fully-built local Player.
    /// <para>Phase 5.7-B relied on <c>enabled = false</c> for this: a ghost carried only the fields the detection
    /// paths read (unit, cameraRoot, visuals), and Unity simply never delivered <c>Update</c> to a disabled
    /// component. The game's manual-update refactor removed <c>Player.Update</c> entirely and replaced it with
    /// <c>Player.ManualUpdate</c>, which <c>ProjectileSystem.Update</c> calls directly while its Burst job runs:</para>
    /// <code>
    /// StaticInstance&lt;BatchedNPCRaycasts&gt;.Instance.ManualUpdate();
    /// List&lt;Player&gt; players = StaticInstance&lt;GameManager&gt;.Instance.Players;
    /// for (int i = 0; i &lt; players.Count; i++) players[i].ManualUpdate();
    /// if (RunWhileWritingInstanceData != null) RunWhileWritingInstanceData();
    /// </code>
    /// <para>A direct call ignores <c>enabled</c>, so every frame a ghost reached <c>ManualUpdate</c> and threw an
    /// NRE on the fields it does not carry (inputReader / cameraRootAnimator). That aborted
    /// <c>ProjectileSystem.Update</c> mid-loop, so <c>RunWhileWritingInstanceData</c> — where
    /// <c>BehaviourTreeCode_Gameplay.ManualUpdate</c> (the enemy behaviour-tree driver), <c>PlayerHUD</c> and
    /// <c>GooEyeManager</c> register — never ran, and <c>_lastJobHandle.Complete()</c> never completed: enemy AI
    /// froze host-side, and clients saw it through their puppets. Host Player.log carried the NRE once per frame
    /// (12485 in one session) while the BepInEx log stayed clean, because BepInEx does not capture Unity's own
    /// exceptions.</para>
    /// <para>So the skip is now explicit rather than a side effect of <c>enabled</c>. Suppressing the ghost while
    /// the host loads (RemotePlayerRegistryManager.IsHostLoading, the ShowLevelNode camera NRE) does not help here:
    /// this fires during ordinary gameplay, which is exactly when the ghost has to exist.</para>
    /// <para><b>DonutCollision (game 1.0.2):</b> the ring damage of Terrorbaum's root phase and the Witch's rings
    /// collects <c>aliveNpcs</c> plus every <c>Players[i].playerUnit</c> each frame and reads
    /// <c>unit.capsuleCollider.radius</c>. A ghost's Unit has no <c>mainCollider</c>, so <c>Unit.Awake</c> never
    /// set <c>capsuleCollider</c>, and the loop threw on the ghost every frame the ring was enabled (6260 and 25873
    /// times in two Terrorbaum rounds). Ghosts are appended after the host's own player, so everything before them
    /// had already been checked: the ring has never damaged a ghost. The read of <c>Players</c> inside that method
    /// now returns the list without ghosts, which keeps exactly that outcome without the exception. Whether a remote
    /// player should take ring damage through its ghost depends on whether its own end runs the ring (Terrorbaum's
    /// root phase is mirrored to the client, whose ring then checks the client's local player) and is not decided
    /// here.</para>
    /// </summary>
    internal static class GhostPlayerPatches
    {
        public static void Apply(Harmony harmony)
        {
            ApplyDonutCollision(harmony);
            ApplyWaterTriggers(harmony);
            ApplyPunishTrigger(harmony);
            try
            {
                var playerType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Player");
                if (playerType == null) { Plugin.Log.Warn("[GhostPlayer] Player type not found — ghosts will run vanilla per-player logic"); return; }

                var manualUpdate = AccessTools.Method(playerType, "ManualUpdate");
                if (manualUpdate == null)
                {
                    // Not a probe: without this skip a ghost throws inside ProjectileSystem.Update every frame and
                    // takes the enemy behaviour-tree driver down with it. Loud on purpose — a rename must not pass
                    // as a silent no-op the way AccessTools' null return otherwise would.
                    Plugin.Log.Error("[GhostPlayer] Player.ManualUpdate not found — if the game drives players directly, ghosts will throw every frame and freeze enemy AI");
                    return;
                }

                harmony.Patch(manualUpdate, prefix: new HarmonyMethod(
                    typeof(GhostPlayerPatches).GetMethod(nameof(Player_ManualUpdate_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info("[GhostPlayer] patched Player.ManualUpdate (ghost skip)");
            }
            catch (Exception ex) { Plugin.Log.Error($"[GhostPlayer] apply failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static int _skipLogCount;

        /// <summary>Skips vanilla per-player update for a ghost. Real players are untouched, so a lookup that ever
        /// misidentified one would only cost that player their update — never silently swallow a real exception.</summary>
        private static bool Player_ManualUpdate_Pre(object __instance)
        {
            try
            {
                if (!RemotePlayerRegistryManager.IsGhostPlayer(__instance)) return true;
                if (_skipLogCount++ < 3)
                    Plugin.Log.Info("[GhostPlayer] skipping Player.ManualUpdate for a ghost (headless stand-in carries no inputReader/animators)");
                return false;
            }
            catch { return true; } // never block a real player's update because our own check threw
        }

        // ---------------------------------------------------------------- DonutCollision.Update (game 1.0.2+)

        private static int _donutPlayerReads;

        private static void ApplyDonutCollision(Harmony harmony)
        {
            try
            {
                var donutType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.DonutCollision");
                var update = donutType != null ? AccessTools.Method(donutType, "Update") : null;
                if (update == null)
                {
                    Plugin.Log.Info("[GhostPlayer] DonutCollision.Update not found on this game build — no ring ghost skip needed");
                    return;
                }
                _donutPlayerReads = 0;
                harmony.Patch(update, transpiler: new HarmonyMethod(
                    typeof(GhostPlayerPatches).GetMethod(nameof(DonutCollision_Update_Transpiler), BindingFlags.Static | BindingFlags.NonPublic)));
                if (_donutPlayerReads > 0)
                    Plugin.Log.Info($"[GhostPlayer] patched DonutCollision.Update (ghost skip, {_donutPlayerReads} Players read(s))");
                else
                    Plugin.Log.Error("[GhostPlayer] DonutCollision.Update no longer reads GameManager.Players — ghost skip not supported on this game build; ring damage may throw on ghosts every frame");
            }
            catch (Exception ex) { Plugin.Log.Error($"[GhostPlayer] DonutCollision patch failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Replaces each <c>GameManager.Players</c> read in <c>DonutCollision.Update</c> with
        /// <see cref="PlayersWithoutGhosts"/>, which takes the same instance off the stack.</summary>
        private static IEnumerable<CodeInstruction> DonutCollision_Update_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getter = AccessTools.PropertyGetter(typeof(GameManager), nameof(GameManager.Players));
            var hook = typeof(GhostPlayerPatches).GetMethod(nameof(PlayersWithoutGhosts), BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var ins in instructions)
            {
                if (getter != null && hook != null && (ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt)
                    && ins.operand is MethodInfo m && m == getter)
                {
                    _donutPlayerReads++;
                    yield return new CodeInstruction(OpCodes.Call, hook).WithLabels(ins.labels).WithBlocks(ins.blocks);
                    continue;
                }
                yield return ins;
            }
        }

        // ---------------------------------------------------------------- Water trigger enter/exit

        /// <summary>
        /// <c>Water.OnTriggerEnter</c> / <c>OnTriggerExit</c> look for a <c>Player</c> on the entering collider and call
        /// <c>swimScript.BodyAddWater</c> / <c>BodyRemoveWater</c>. A ghost carries a (disabled) <c>Player</c> with no
        /// swim script, so every time a remote player walked into water the host threw (Player.log, rounds 587/588);
        /// <c>TitanicWater</c>'s overrides would also add that null swim script to its own swimmer list. Whether a
        /// remote player is in water is decided on their own end, so the ghost is kept out of water volumes entirely —
        /// the same rule the NextLevelTrigger guard applies. Every <c>Water</c> type that declares the two methods is
        /// patched, so an override that does not call the base is covered too.
        /// </summary>
        private static void ApplyWaterTriggers(Harmony harmony)
        {
            try
            {
                var waterType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.World.Water");
                if (waterType == null)
                {
                    Plugin.Log.Info("[GhostPlayer] Water type not found on this game build — no water ghost skip needed");
                    return;
                }
                var prefix = new HarmonyMethod(typeof(GhostPlayerPatches).GetMethod(nameof(Water_Trigger_Pre), BindingFlags.Static | BindingFlags.NonPublic));
                int patched = 0;
                foreach (var t in AccessTools.GetTypesFromAssembly(waterType.Assembly))
                {
                    if (t == null || !waterType.IsAssignableFrom(t)) continue;
                    foreach (var name in new[] { "OnTriggerEnter", "OnTriggerExit" })
                    {
                        var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                            null, new[] { typeof(Collider) }, null);
                        if (m == null || m.IsAbstract) continue;
                        harmony.Patch(m, prefix: prefix);
                        patched++;
                    }
                }
                if (patched > 0)
                    Plugin.Log.Info($"[GhostPlayer] patched {patched} Water trigger method(s) (ghost skip)");
                else
                    Plugin.Log.Error("[GhostPlayer] Water declares no OnTriggerEnter/OnTriggerExit(Collider) — ghost skip not supported on this game build; water may throw on ghosts");
            }
            catch (Exception ex) { Plugin.Log.Error($"[GhostPlayer] Water patch failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Skips the water volume's handling of a ghost's collider; everything else runs as vanilla.</summary>
        private static bool Water_Trigger_Pre(Collider __0)
        {
            try { return !RemotePlayerRegistryManager.HasGhosts || !RemotePlayerRegistryManager.IsGhostCollider(__0); }
            catch { return true; }
        }

        // ---------------------------------------------------------------- PunishPlayerTrigger (PT-1)

        /// <summary>
        /// <c>PunishPlayerTrigger</c> is the game's fall damage: the volume under a drop or outside the map. A player
        /// entering it loses half their current health plus 5 (<c>DamageTypes.Punish</c>), the game slows to 0.25
        /// and fades out, and the player is teleported back into the room. Any other unit entering it is killed with
        /// its full maximum health.
        /// <para>A remote player's stand-in (the host's ghost, a target proxy, a client's friendly-fire hit proxy) has
        /// a solid capsule on a kinematic body, so it follows its player into the volume and sets it off again on
        /// this machine. Its <c>isPlayer</c> is false, so it takes the non-player branch, and the host forwarded that
        /// full-health hit to the client on top of the fall the client had already taken on its own end: a client
        /// lost far more to a fall than a host did.</para>
        /// <para>Whether a player fell is decided on that player's own end, like water (above). The trigger skips
        /// every remote-player stand-in; the local player and every enemy are untouched.</para>
        /// </summary>
        private static void ApplyPunishTrigger(Harmony harmony)
        {
            try
            {
                var triggerType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.World.PunishPlayerTrigger");
                var trigger = triggerType != null
                    ? triggerType.GetMethod("Trigger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new[] { typeof(GameObject) }, null)
                    : null;
                if (trigger == null)
                {
                    Plugin.Log.Error("[GhostPlayer] PunishPlayerTrigger.Trigger(GameObject) not found — remote players' stand-ins will set off fall damage on this machine");
                    return;
                }
                harmony.Patch(trigger, prefix: new HarmonyMethod(
                    typeof(GhostPlayerPatches).GetMethod(nameof(PunishTrigger_Trigger_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info("[GhostPlayer] patched PunishPlayerTrigger.Trigger (remote-player stand-in skip)");
            }
            catch (Exception ex) { Plugin.Log.Error($"[GhostPlayer] PunishPlayerTrigger patch failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static int _punishSkipLogCount;

        /// <summary>Skips the trigger for a remote player's stand-in. The unit is the one on the collider's object or a
        /// parent; the game's own lookup (<c>UnitManager.GetUnitFromObject</c>) can return null for an ignored collider,
        /// but only a unit in the proxy registry is skipped, so that difference never skips anything else.</summary>
        private static bool PunishTrigger_Trigger_Pre(GameObject __0)
        {
            try
            {
                if (__0 == null) return true;
                var unit = __0.GetComponentInParent<Unit>();
                if (unit == null || !SULFURTogether.Networking.RemotePlayerTargetProxyManager.TryGetProxyPeer(unit, out string peerId))
                    return true;
                if (_punishSkipLogCount++ < 5)
                    Plugin.Log.Info($"[GhostPlayer] PunishPlayerTrigger ignored a remote player's stand-in peer={peerId} (the fall is that player's own)");
                return false;
            }
            catch { return true; }
        }

        private static readonly List<Player> _playersWithoutGhosts = new List<Player>();

        /// <summary><c>gm.Players</c>, minus our ghosts. Returns the game's own list when there are none (always on a
        /// client, and on a host without remote players), so single-player behaviour is untouched.</summary>
        private static List<Player> PlayersWithoutGhosts(GameManager gm)
        {
            List<Player> players = gm.Players;
            if (!RemotePlayerRegistryManager.HasGhosts || players == null) return players!;
            _playersWithoutGhosts.Clear();
            for (int i = 0; i < players.Count; i++)
                if (!RemotePlayerRegistryManager.IsGhostPlayer(players[i])) _playersWithoutGhosts.Add(players[i]);
            return _playersWithoutGhosts;
        }
    }
}
