using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Phase 5.3-G: LevelGeneration process trace. Discovery-first, never guesses namespaces.
    ///   - LevelGenGraphUtilities.FinalizeConnection(Connector, Connector) — patched; the Connector
    ///     type is DERIVED from this method's parameter (it lives in
    ///     PerfectRandom.Sulfur.Core.LevelGeneration, not LevelGeneration).
    ///   - Connector.FinalizeSpawn() — patched via the derived type; traces door/blocker objects'
    ///     active/collider/renderer state before & after (the "rock blocks path / open door" cause).
    ///   - AddExtraRoomsNode.Execute coroutine — the real logic is in the compiler-generated
    ///     <Execute>d__XX.MoveNext(); we resolve that nested state machine and patch MoveNext.
    /// </summary>
    internal static class LevelGenTracePatches
    {
        private static Type? _connectorType;
        private static bool _connectorMembersDumped;
        private static readonly HashSet<string> _stateMachineFieldsDumped = new HashSet<string>();
        private static readonly HashSet<int> _extraRoomInstancesLogged = new HashSet<int>();

        // Phase 5.3-I D/E/F: discovery-dump-once + first-N log limiting per trace category.
        private static readonly HashSet<string> _memberDumpDone = new HashSet<string>();
        private static readonly Dictionary<string, int> _firstN = new Dictionary<string, int>();
        private const int FirstNLimit = 40;

        private static bool AllowFirstN(string category)
        {
            _firstN.TryGetValue(category, out int n);
            if (n >= FirstNLimit) return false;
            _firstN[category] = n + 1;
            return true;
        }

        // Candidate door/blocker field names (real names confirmed via DLL reverse engineering).
        private static readonly string[] BlockerObjectFields =
        {
            "disableObjectIfPlugged", "enableObjectIfPlugged",
            "disableObjectIfActive",  "enableObjectIfActive",
            "connectorNavBlockerOverride", "connectorDoorBlockerOverride",
        };

        public static void Apply(Harmony harmony)
        {
            if (!Plugin.Cfg.EnableLevelGenTrace.Value)
            {
                Plugin.Log.Info("[LevelGenTrace] Disabled by config.");
                return;
            }

            PatchLevelStepTrace(harmony);       // D: MakerGraphContext.ResetContext (the 17-step trace)
            PatchFinalizeConnection(harmony);   // also derives the Connector type
            PatchConnectorFinalizeSpawn(harmony);

            // Coroutine MoveNext traces — node Execute() bodies are iterators; real logic is in
            // the compiler-generated <Execute>d__XX.MoveNext (resolved at runtime, never hardcoded).
            PatchNodeCoroutine(harmony, "AddExtraRoomsNode",          nameof(ExtraRoomMoveNext_Post));
            PatchNodeCoroutine(harmony, "SpawnEventsNode",            nameof(GenericNodeMoveNext_Post));
            PatchNodeCoroutine(harmony, "SetupLootNode",             nameof(GenericNodeMoveNext_Post));
            PatchNodeCoroutine(harmony, "FinalizeAndMutateUnitsNode", nameof(GenericNodeMoveNext_Post));
            PatchNodeCoroutine(harmony, "SpawnEnemiesNode",           nameof(GenericNodeMoveNext_Post));

            // D: room-internal randomization (explains "same room shell, different visible layout").
            //    Process self-destructs the component at its end, so capture in a PREFIX while alive.
            PatchProcessMethods(harmony, "RandomChildSubset",  nameof(RandomComponent_Process_Pre));
            PatchProcessMethods(harmony, "RandomChanceSelect", nameof(RandomComponent_Process_Pre));
            PatchProcessMethods(harmony, "RandomlyRemove",     nameof(RandomComponent_Process_Pre));
            // Room is not destroyed — safe to read baked-list sizes before & after filtering.
            PatchRoomFilterBakedLists(harmony);

            // E: event / special-NPC selection divergence.
            PatchNamedMethods(harmony, "SpawnEventsNode", "PickEventFromList",
                nameof(PickEventFromList_Pre), nameof(PickEventFromList_Post));

            // F: mutation/modifier divergence.
            PatchNamedMethods(harmony, "FinalizeAndMutateUnitsNode", "GetMutation",
                null, nameof(GetMutation_Post));
            PatchToggleMutation(harmony);

            Plugin.Log.Info("[LevelGenTrace] LevelGeneration trace hooks registered.");
        }

        // ---- generic short-name type discovery (C): try several namespaces + assembly scan ----

        private static Type? FindTypeByShortName(string shortName)
        {
            // 1-3: exact full names in the confirmed namespaces (LevelGeneration first — nodes live there).
            string[] candidates =
            {
                "LevelGeneration." + shortName,
                "PerfectRandom.Sulfur.Core.LevelGeneration." + shortName,
                shortName,
            };
            foreach (var full in candidates)
            {
                var t = AccessTools.TypeByName(full);
                if (t != null) return t;
            }

            // 4: scan all loaded assemblies for an exact Name match.
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t != null).ToArray()!; }
                    foreach (var t in types)
                        if (t != null && t.Name == shortName) return t;
                }
            }
            catch { }

            // 5: dump candidates that contain the short name for the next iteration.
            try
            {
                var partial = new List<string>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t != null).ToArray()!; }
                    foreach (var t in types)
                        if (t != null && t.Name.IndexOf(shortName, StringComparison.OrdinalIgnoreCase) >= 0)
                            partial.Add(t.FullName ?? t.Name);
                    if (partial.Count >= 10) break;
                }
                Plugin.Log.Info($"[LevelGenTrace] type NOT FOUND: {shortName} — partial candidates: {(partial.Count == 0 ? "none" : string.Join(", ", partial.Take(10)))}");
            }
            catch { Plugin.Log.Info($"[LevelGenTrace] type NOT FOUND: {shortName}"); }
            return null;
        }

        // ---- LevelGenGraphUtilities.FinalizeConnection ----

        private static void PatchFinalizeConnection(Harmony harmony)
        {
            var t = FindTypeByShortName("LevelGenGraphUtilities");
            if (t == null) return;

            var mi = AccessTools.GetDeclaredMethods(t).FirstOrDefault(m => m.Name == "FinalizeConnection");
            if (mi == null) { Plugin.Log.Info("[LevelGenTrace] FinalizeConnection method not found"); return; }

            // Derive the real Connector type from the parameter — avoids namespace guessing entirely.
            var ps = mi.GetParameters();
            if (ps.Length >= 1)
            {
                _connectorType = ps[0].ParameterType;
                Plugin.Log.Info($"[LevelGenTrace] Connector type derived from FinalizeConnection param = {_connectorType.FullName}");
            }

            try
            {
                harmony.Patch(mi,
                    prefix:  new HarmonyMethod(typeof(LevelGenTracePatches).GetMethod(nameof(FinalizeConnection_Pre),  BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(LevelGenTracePatches).GetMethod(nameof(FinalizeConnection_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info($"[LevelGenTrace] patched {t.Name}.FinalizeConnection({string.Join(",", ps.Select(p => p.ParameterType.Name))})");
            }
            catch (Exception ex) { Plugin.Log.Error($"[LevelGenTrace] FinalizeConnection patch failed: {ex.Message}"); }
        }

        private static void FinalizeConnection_Pre(object[] __args, out int __state)
        {
            __state = 0;
            try
            {
                if (__args == null || __args.Length < 2) return;
                __state = ChildCount(__args[0]) + ChildCount(__args[1]);
            }
            catch { __state = 0; }
        }

        private static void FinalizeConnection_Post(object[] __args, int __state)
        {
            try
            {
                NetGameplayProbeManager.NoteLevelGenTrace("FinalizeConnection");
                if (!Plugin.Cfg.LogLevelGenTrace.Value) return;
                if (__args == null || __args.Length < 2) return;
                int after = ChildCount(__args[0]) + ChildCount(__args[1]);
                int destroyed = Mathf.Max(0, __state - after);
                Plugin.Log.Info($"[LevelGenTrace] FinalizeConnection out={ConnectorTag(__args[0])} in={ConnectorTag(__args[1])} destroyedChildren={destroyed}");
            }
            catch (Exception ex) { if (Plugin.Cfg.EnableDebugLog.Value) Plugin.Log.Debug($"[LevelGenTrace] FinalizeConnection_Post: {ex.Message}"); }
        }

        // ---- Connector.FinalizeSpawn ----

        private struct BlockerState
        {
            public string Field;
            public GameObject? Obj;
            public string Name;
            public bool ExistedActive;
            public bool ExistedCollider;
            public bool ExistedRenderer;
        }

        private static void PatchConnectorFinalizeSpawn(Harmony harmony)
        {
            var t = _connectorType ?? FindTypeByShortName("Connector");
            if (t == null) { Plugin.Log.Info("[LevelGenTrace] Connector type unresolved — cannot patch FinalizeSpawn"); return; }

            var mi = AccessTools.GetDeclaredMethods(t).FirstOrDefault(m => m.Name == "FinalizeSpawn" && m.GetParameters().Length == 0);
            if (mi == null) { Plugin.Log.Info($"[LevelGenTrace] {t.FullName}.FinalizeSpawn() not found"); return; }

            try
            {
                harmony.Patch(mi,
                    prefix:  new HarmonyMethod(typeof(LevelGenTracePatches).GetMethod(nameof(ConnectorFinalizeSpawn_Pre),  BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(LevelGenTracePatches).GetMethod(nameof(ConnectorFinalizeSpawn_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info($"[LevelGenTrace] patched {t.FullName}.FinalizeSpawn()");
            }
            catch (Exception ex) { Plugin.Log.Error($"[LevelGenTrace] Connector.FinalizeSpawn patch failed: {ex.Message}"); }
        }

        private static void ConnectorFinalizeSpawn_Pre(object __instance, out BlockerState[] __state)
        {
            __state = Array.Empty<BlockerState>();
            try
            {
                if (!_connectorMembersDumped) { _connectorMembersDumped = true; DumpConnectorFields(__instance); }

                var list = new List<BlockerState>(BlockerObjectFields.Length);
                foreach (var field in BlockerObjectFields)
                {
                    var go = ReadGameObject(__instance, field);
                    list.Add(new BlockerState
                    {
                        Field = field,
                        Obj = go,
                        Name = go != null ? go.name : "<null>",
                        ExistedActive = go != null && go.activeSelf,
                        ExistedCollider = go != null && AnyColliderEnabled(go),
                        ExistedRenderer = go != null && AnyRendererEnabled(go),
                    });
                }
                __state = list.ToArray();

                if (Plugin.Cfg.LogLevelGenTrace.Value)
                {
                    bool isConnected = ReadBool(__instance, "isConnected") ?? false;
                    Plugin.Log.Info($"[ConnectorTrace] FinalizeSpawn PRE conn={ConnectorTag(__instance)} isConnected={isConnected} connectionAllowed={ReadBool(__instance, "connectionAllowed")} hasSpawnedDoor={ReadBool(__instance, "hasSpawnedDoor")} doNotCreateNavBlocker={ReadBool(__instance, "doNotCreateNavBlockerHere")} doNotCreateDoorBlocker={ReadBool(__instance, "doNotCreateDoorBlockerHere")}");
                    foreach (var b in __state)
                    {
                        if (b.Obj == null) continue;
                        Plugin.Log.Info($"[DoorBlockerTrace] PRE field={b.Field} obj={b.Name} active={b.ExistedActive} collider={b.ExistedCollider} renderer={b.ExistedRenderer}");
                    }
                }
            }
            catch (Exception ex) { if (Plugin.Cfg.EnableDebugLog.Value) Plugin.Log.Debug($"[LevelGenTrace] ConnectorFinalizeSpawn_Pre: {ex.Message}"); }
        }

        private static void ConnectorFinalizeSpawn_Post(object __instance, BlockerState[] __state)
        {
            try
            {
                NetGameplayProbeManager.NoteLevelGenTrace("ConnectorFinalizeSpawn");
                int recorded = 0;
                bool isConnected = ReadBool(__instance, "isConnected") ?? false;

                foreach (var b in __state)
                {
                    // Re-read the field — FinalizeSpawn may have nulled it and/or destroyed the object.
                    var goNow = ReadGameObject(__instance, b.Field);
                    bool existedBefore = b.Obj != null;
                    bool existsNow = goNow != null;
                    if (!existedBefore && !existsNow) continue;
                    recorded++;

                    if (!Plugin.Cfg.LogLevelGenTrace.Value) continue;
                    if (!existsNow)
                    {
                        Plugin.Log.Info($"[DoorBlockerTrace] POST field={b.Field} obj={b.Name} -> <destroyed/null>");
                    }
                    else
                    {
                        Plugin.Log.Info($"[DoorBlockerTrace] POST field={b.Field} obj={goNow!.name} active={goNow.activeSelf} collider={AnyColliderEnabled(goNow)} renderer={AnyRendererEnabled(goNow)}");
                    }
                }

                if (recorded > 0) NetGameplayProbeManager.NoteLevelGenTrace("DoorBlocker", recorded);
                if (Plugin.Cfg.LogLevelGenTrace.Value)
                    Plugin.Log.Info($"[ConnectorTrace] FinalizeSpawn POST conn={ConnectorTag(__instance)} isConnected={isConnected} blockersRecorded={recorded}");
            }
            catch (Exception ex) { if (Plugin.Cfg.EnableDebugLog.Value) Plugin.Log.Debug($"[LevelGenTrace] ConnectorFinalizeSpawn_Post: {ex.Message}"); }
        }

        private static void DumpConnectorFields(object connector)
        {
            try
            {
                var t = connector.GetType();
                var sb = new StringBuilder();
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Take(80))
                    sb.Append(f.Name).Append('(').Append(f.FieldType.Name).Append(") ");
                Plugin.Log.Info($"[LevelGenTrace] {t.FullName} fields: {sb}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[LevelGenTrace] DumpConnectorFields failed: {ex.Message}"); }
        }

        // ---- D: LevelStep trace via MakerGraphContext.ResetContext ----

        private static void PatchLevelStepTrace(Harmony harmony)
        {
            // MakerGraphContext lives in the MakerGraphTool namespace; resolve by short name.
            var t = FindTypeByShortName("MakerGraphContext");
            if (t == null) { Plugin.Log.Info("[LevelStepTrace] MakerGraphContext type not found"); return; }

            var mi = AccessTools.GetDeclaredMethods(t).FirstOrDefault(m => m.Name == "ResetContext");
            if (mi == null)
            {
                // Dump methods for discovery if ResetContext is named differently.
                var methods = AccessTools.GetDeclaredMethods(t).Where(m => !m.IsSpecialName)
                    .Select(m => $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})").Take(40);
                Plugin.Log.Info($"[LevelStepTrace] ResetContext not found on {t.FullName}; methods: {string.Join(" | ", methods)}");
                return;
            }

            try
            {
                harmony.Patch(mi,
                    postfix: new HarmonyMethod(typeof(LevelGenTracePatches).GetMethod(nameof(ResetContext_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info($"[LevelStepTrace] patched {t.FullName}.ResetContext({string.Join(",", mi.GetParameters().Select(p => p.ParameterType.Name))})");
            }
            catch (Exception ex) { Plugin.Log.Error($"[LevelStepTrace] ResetContext patch failed: {ex.Message}"); }
        }

        private static bool _makerContextFieldsDumped;

        // __instance = MakerGraphContext, __args = (MakerNodeBase node, int index)
        private static void ResetContext_Post(object __instance, object[] __args)
        {
            try
            {
                NetGameplayProbeManager.NoteLevelGenTrace("LevelStep");
                if (!_makerContextFieldsDumped) { _makerContextFieldsDumped = true; DumpFields(__instance, "MakerGraphContext"); }

                object? node = (__args != null && __args.Length > 0) ? __args[0] : null;
                int index = (__args != null && __args.Length > 1 && __args[1] is int i) ? i : -1;

                // B: read confirmed members by exact name (property OR field) — NOT a mangled-substring guess.
                //   graphSeed = MakerGraphContext.Seed ; total = MakerGraphContext.Set.TotalExecutingNodes
                //   nodeSeed  = Seed + index (matches ResetContext: nodeSeed = Seed + index)
                string graphSeed = MemberNumber(__instance, "Seed");
                object? set = ReadMember(__instance, "Set");
                string total = set != null ? MemberNumber(set, "TotalExecutingNodes") : "?";
                string execIndex = MemberNumber(__instance, "ExecutingNodeIndex");
                string graphName = set is UnityEngine.Object suo && suo != null ? suo.name : (set != null ? NodeName(set) : "?");

                // L P0-A: finalize the generation-input snapshot from the REAL MakerGraph context — this is
                // where graphName (e.g. Caves2) and the final graphSeed are correct (the StartLevelRoutineGraph
                // prefix seed is the previous level's). Done regardless of the trace-log toggle.
                if (long.TryParse(graphSeed, out long gsl))
                    NetGenerationInputCapture.FinalizeFromMakerContext(graphName, gsl);

                if (!Plugin.Cfg.LogLevelGenTrace.Value) return;

                string nodeName = NodeName(node);
                string nodeType = node?.GetType().FullName ?? "?";
                string nodeSeed = "?";
                if (long.TryParse(graphSeed, out long gs) && index >= 0) nodeSeed = (gs + index).ToString();

                string runKey = $"scene={NetGameplayProbeManager.GetLocalRunKey()}";
                Plugin.Log.Info($"[LevelStepTrace] START idx={index}/{total} node={nodeName} type={nodeType} graph={graphName} {runKey} graphSeed={graphSeed} nodeSeed={nodeSeed} execIdx={execIndex}");
            }
            catch (Exception ex) { if (Plugin.Cfg.EnableDebugLog.Value) Plugin.Log.Debug($"[LevelStepTrace] ResetContext_Post: {ex.Message}"); }
        }

        private static string NodeName(object? node)
        {
            if (node == null) return "<null>";
            try
            {
                var nm = ReadField(node, "name") ?? ReadField(node, "NodeName") ?? ReadField(node, "nodeName");
                if (nm is string s && !string.IsNullOrEmpty(s)) return s;
            }
            catch { }
            return node.GetType().Name;
        }

        // ---- E/F/G/H: generic node coroutine MoveNext patch ----

        private static void PatchNodeCoroutine(Harmony harmony, string nodeShortName, string postfixName)
        {
            var nodeType = FindTypeByShortName(nodeShortName);
            if (nodeType == null) return;

            var sm = ResolveCoroutineStateMachine(nodeType, "Execute");
            if (sm == null)
            {
                // Not an iterator? Patch Execute directly instead.
                var execMi = nodeType.GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (execMi != null)
                {
                    try
                    {
                        harmony.Patch(execMi, postfix: new HarmonyMethod(typeof(LevelGenTracePatches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Log.Info($"[LevelGenTrace] patched {nodeType.Name}.Execute (non-iterator)");
                    }
                    catch (Exception ex) { Plugin.Log.Error($"[LevelGenTrace] {nodeShortName}.Execute patch failed: {ex.Message}"); }
                }
                else
                    Plugin.Log.Info($"[LevelGenTrace] {nodeShortName}: no Execute state machine and no Execute method");
                return;
            }
            var moveNext = sm.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (moveNext == null) { Plugin.Log.Info($"[LevelGenTrace] {sm.FullName}.MoveNext not found"); return; }

            try
            {
                harmony.Patch(moveNext, postfix: new HarmonyMethod(typeof(LevelGenTracePatches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info($"[LevelGenTrace] patched {sm.FullName}.MoveNext ({nodeShortName} coroutine)");
            }
            catch (Exception ex) { Plugin.Log.Error($"[LevelGenTrace] {nodeShortName} MoveNext patch failed: {ex.Message}"); }
        }

        // E: AddExtraRoomsNode — enhanced field read (uniqueName, AssetGUID, chance, indices...).
        private static void ExtraRoomMoveNext_Post(object __instance)
        {
            try
            {
                NetGameplayProbeManager.NoteLevelGenTrace("ExtraRoom");

                string typeKey = __instance.GetType().FullName ?? "extraroom";
                if (_stateMachineFieldsDumped.Add(typeKey))
                    DumpFields(__instance, "AddExtraRooms.MoveNext");

                if (!Plugin.Cfg.LogLevelGenTrace.Value) return;

                object? roomRefObj = ReadFieldLike(__instance, "roomRef");
                string? uniqueName = ReadStrLike(__instance, "uniqueName");
                if (roomRefObj == null && string.IsNullOrEmpty(uniqueName)) return;

                int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(__instance);
                if (!_extraRoomInstancesLogged.Add(id)) return;

                string prefab = "", assetGuid = "", chance = "";
                if (roomRefObj != null)
                {
                    prefab = ReadStrLike(roomRefObj, "prefabAsset") ?? "";
                    assetGuid = InvokeStringMethod(roomRefObj, "get_AssetGUID") ?? ReadStrLike(roomRefObj, "AssetGUID") ?? "";
                    chance = ReadAnyNumber(roomRefObj, "chance");
                }

                Plugin.Log.Info($"[ExtraRoomTrace] selected uniqueName={uniqueName} prefab={prefab} assetGUID={assetGuid} chance={chance} sideRoomsToSpawn={ReadAnyNumber(__instance, "sideRoomsToSpawn")} spawnedSideRooms={ReadCountLike(__instance, "spawnedSideRooms")} outConnIdx={ReadAnyNumber(__instance, "outConnIndex")} inConnIdx={ReadAnyNumber(__instance, "inConnIndex")} roomIndex={ReadAnyNumber(__instance, "roomIndex")} flipped={ReadBoolLike(__instance, "flipped")} connectors={ReadCountLike(__instance, "connectors")} uniquePerLevel={ReadBoolLike(__instance, "uniquePerLevel")}");
            }
            catch (Exception ex) { if (Plugin.Cfg.EnableDebugLog.Value) Plugin.Log.Debug($"[LevelGenTrace] ExtraRoomMoveNext_Post: {ex.Message}"); }
        }

        // F/G/H: generic node coroutine — dumps fields once per state machine type for discovery,
        // and emits a compact one-line snapshot of interesting tokens. Read-only.
        private static void GenericNodeMoveNext_Post(object __instance)
        {
            try
            {
                string typeName = __instance.GetType().FullName ?? "node";
                NetGameplayProbeManager.NoteLevelGenTrace("NodeCoroutine");
                if (_stateMachineFieldsDumped.Add(typeName))
                {
                    DumpFields(__instance, "NodeCoroutine " + ShortNodeLabel(typeName));
                    return; // first call: discovery only
                }
                if (!Plugin.Cfg.LogLevelGenTrace.Value) return;

                // Emit at most once per coroutine instance.
                int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(__instance);
                if (!_extraRoomInstancesLogged.Add(id)) return;

                string label = ShortNodeLabel(typeName);
                // Best-effort token reads common across event/loot/mutation nodes.
                string selected = ReadStrLike(__instance, "selected") ?? ReadStrLike(__instance, "eventEntry") ?? ReadStrLike(__instance, "mutation") ?? ReadStrLike(__instance, "lootTable") ?? "";
                string room = ReadStrLike(__instance, "room") ?? "";
                Plugin.Log.Info($"[NodeTrace] {label} selected={selected} room={room} vendor={ReadBoolLike(__instance, "vendor")} usedRun={ReadCountLike(__instance, "usedUniqueEventThisRun")} usedEnv={ReadCountLike(__instance, "usedUniqueEventThisEnvironment")} usedLevel={ReadCountLike(__instance, "usedEventsThisLevel")} containers={ReadCountLike(__instance, "containers")}");
            }
            catch (Exception ex) { if (Plugin.Cfg.EnableDebugLog.Value) Plugin.Log.Debug($"[LevelGenTrace] GenericNodeMoveNext_Post: {ex.Message}"); }
        }

        private static string ShortNodeLabel(string typeFullName)
        {
            // "LevelGeneration.SpawnEventsNode+<Execute>d__1" -> "SpawnEventsNode"
            int plus = typeFullName.IndexOf('+');
            string outer = plus >= 0 ? typeFullName.Substring(0, plus) : typeFullName;
            int dot = outer.LastIndexOf('.');
            return dot >= 0 ? outer.Substring(dot + 1) : outer;
        }

        // Resolve the compiler-generated IEnumerator state machine for an iterator method.
        private static Type? ResolveCoroutineStateMachine(Type nodeType, string methodName)
        {
            try
            {
                var execMi = nodeType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (execMi != null)
                {
                    var attr = execMi.GetCustomAttributes()
                        .FirstOrDefault(a => a.GetType().Name.Contains("StateMachine"));
                    if (attr != null)
                    {
                        var prop = attr.GetType().GetProperty("StateMachineType");
                        if (prop?.GetValue(attr) is Type smt) return smt;
                    }
                }
                return nodeType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(nt => typeof(IEnumerator).IsAssignableFrom(nt) && nt.Name.Contains(methodName));
            }
            catch { return null; }
        }

        private static void DumpFields(object obj, string label)
        {
            try
            {
                var t = obj.GetType();
                var sb = new StringBuilder();
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Take(70))
                    sb.Append(f.Name).Append('(').Append(f.FieldType.Name).Append(") ");
                Plugin.Log.Info($"[LevelGenTrace] {label} {t.Name} fields: {sb}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[LevelGenTrace] DumpFields failed: {ex.Message}"); }
        }

        private static string? InvokeStringMethod(object obj, string method)
        {
            try
            {
                var mi = obj.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (mi != null) return mi.Invoke(obj, null)?.ToString();
            }
            catch { }
            return null;
        }

        // ================= D/E/F: room randomization / event / mutation traces =================

        // Patch every method named "Process" on a randomization component type with a PREFIX.
        private static void PatchProcessMethods(Harmony harmony, string typeShortName, string prefixName)
        {
            var t = FindTypeByShortName(typeShortName);
            if (t == null) return;
            var methods = AccessTools.GetDeclaredMethods(t).Where(m => m.Name == "Process").ToList();
            if (methods.Count == 0) { Plugin.Log.Info($"[LevelGenTrace] {t.FullName}.Process not found"); return; }
            foreach (var mi in methods)
            {
                try
                {
                    harmony.Patch(mi, prefix: new HarmonyMethod(typeof(LevelGenTracePatches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic)));
                    Plugin.Log.Info($"[LevelGenTrace] patched {t.Name}.Process({string.Join(",", mi.GetParameters().Select(p => p.ParameterType.Name))})");
                }
                catch (Exception ex) { Plugin.Log.Error($"[LevelGenTrace] {typeShortName}.Process patch failed: {ex.Message}"); }
            }
        }

        private static void PatchNamedMethods(Harmony harmony, string typeShortName, string methodName, string? prefixName, string? postfixName)
        {
            var t = FindTypeByShortName(typeShortName);
            if (t == null) return;
            var methods = AccessTools.GetDeclaredMethods(t).Where(m => m.Name == methodName).ToList();
            if (methods.Count == 0) { Plugin.Log.Info($"[LevelGenTrace] {t.FullName}.{methodName} not found"); return; }
            foreach (var mi in methods)
            {
                try
                {
                    harmony.Patch(mi,
                        prefix:  prefixName  != null ? new HarmonyMethod(typeof(LevelGenTracePatches).GetMethod(prefixName,  BindingFlags.Static | BindingFlags.NonPublic)) : null,
                        postfix: postfixName != null ? new HarmonyMethod(typeof(LevelGenTracePatches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic)) : null);
                    Plugin.Log.Info($"[LevelGenTrace] patched {t.Name}.{methodName}({string.Join(",", mi.GetParameters().Select(p => p.ParameterType.Name))})");
                }
                catch (Exception ex) { Plugin.Log.Error($"[LevelGenTrace] {typeShortName}.{methodName} patch failed: {ex.Message}"); }
            }
        }

        private static void PatchRoomFilterBakedLists(Harmony harmony)
        {
            var t = FindTypeByShortName("Room");
            if (t == null) return;
            var mi = AccessTools.GetDeclaredMethods(t).FirstOrDefault(m => m.Name == "FilterAndFinalizeBakedLists");
            if (mi == null) { Plugin.Log.Info($"[LevelGenTrace] {t.FullName}.FilterAndFinalizeBakedLists not found"); return; }
            try
            {
                harmony.Patch(mi,
                    prefix:  new HarmonyMethod(typeof(LevelGenTracePatches).GetMethod(nameof(RoomFilterBaked_Pre),  BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(LevelGenTracePatches).GetMethod(nameof(RoomFilterBaked_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info($"[LevelGenTrace] patched {t.Name}.FilterAndFinalizeBakedLists");
            }
            catch (Exception ex) { Plugin.Log.Error($"[LevelGenTrace] FilterAndFinalizeBakedLists patch failed: {ex.Message}"); }
        }

        private static void PatchToggleMutation(Harmony harmony)
        {
            var t = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Unit");
            if (t == null) { Plugin.Log.Info("[LevelGenTrace] Unit type not found for ToggleMutation"); return; }
            var methods = AccessTools.GetDeclaredMethods(t).Where(m => m.Name == "ToggleMutation").ToList();
            if (methods.Count == 0) { Plugin.Log.Info($"[LevelGenTrace] {t.FullName}.ToggleMutation not found"); return; }
            foreach (var mi in methods)
            {
                try
                {
                    harmony.Patch(mi, prefix: new HarmonyMethod(typeof(LevelGenTracePatches).GetMethod(nameof(ToggleMutation_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                    Plugin.Log.Info($"[LevelGenTrace] patched Unit.ToggleMutation({string.Join(",", mi.GetParameters().Select(p => p.ParameterType.Name))})");
                }
                catch (Exception ex) { Plugin.Log.Error($"[LevelGenTrace] ToggleMutation patch failed: {ex.Message}"); }
            }
        }

        // D: randomization component PREFIX (component is alive here; it self-destructs at Process end).
        private static void RandomComponent_Process_Pre(object __instance)
        {
            try
            {
                NetGameplayProbeManager.NoteLevelGenTrace("RoomRandomize");
                string typeName = __instance.GetType().Name;
                if (_memberDumpDone.Add("proc:" + typeName)) DumpFields(__instance, "RandomComponent." + typeName);
                if (!Plugin.Cfg.LogLevelGenTrace.Value) return;
                if (!AllowFirstN("proc:" + typeName)) return;

                string runKey = NetGameplayProbeManager.GetLocalRunKey();
                int childCount = ChildCount(__instance);
                Plugin.Log.Info($"[RoomRandomizeTrace] {typeName} scene={runKey} path={GameObjectPath(__instance)} childCount={childCount} " +
                    $"minKeep={ReadAnyNumber(__instance, "minChildrenToKeep")} maxKeep={ReadAnyNumber(__instance, "maxChildrenToKeep")} " +
                    $"chanceToRemove={ReadAnyNumber(__instance, "chanceToRemove")} chanceToBeRemove={ReadAnyNumber(__instance, "chanceToBeRemove")} " +
                    $"rootIndex={ReadAnyNumber(__instance, "RandomizedLevelRootIndex")}");
            }
            catch (Exception ex) { if (Plugin.Cfg.EnableDebugLog.Value) Plugin.Log.Debug($"[LevelGenTrace] RandomComponent_Process_Pre: {ex.Message}"); }
        }

        private struct BakedCounts { public int Npc, Event, Nav, DestroyedIdx; public string Room; }

        private static void RoomFilterBaked_Pre(object __instance, object[] __args, out BakedCounts __state)
        {
            __state = default;
            try
            {
                __state.Room = (__instance is Component c && c != null && c.gameObject != null) ? c.gameObject.name : "?";
                __state.Npc = ReadCollectionCount(__instance, "bakedNPCSpawns");
                __state.Event = ReadCollectionCount(__instance, "bakedEventSpawners");
                __state.Nav = ReadCollectionCount(__instance, "bakedNavMeshLinks");
                __state.DestroyedIdx = (__args != null && __args.Length > 0 && __args[0] is ICollection col) ? col.Count : -1;
            }
            catch { }
        }

        private static void RoomFilterBaked_Post(object __instance, BakedCounts __state)
        {
            try
            {
                NetGameplayProbeManager.NoteLevelGenTrace("FilterBaked");
                if (!Plugin.Cfg.LogLevelGenTrace.Value) return;
                if (!AllowFirstN("filterBaked")) return;
                int npcAfter = ReadCollectionCount(__instance, "bakedNPCSpawns");
                int eventAfter = ReadCollectionCount(__instance, "bakedEventSpawners");
                int navAfter = ReadCollectionCount(__instance, "bakedNavMeshLinks");
                Plugin.Log.Info($"[FilterBakedTrace] scene={NetGameplayProbeManager.GetLocalRunKey()} room={__state.Room} destroyedIndexes={__state.DestroyedIdx} " +
                    $"npcSpawns={__state.Npc}->{npcAfter} eventSpawners={__state.Event}->{eventAfter} navLinks={__state.Nav}->{navAfter}");
            }
            catch (Exception ex) { if (Plugin.Cfg.EnableDebugLog.Value) Plugin.Log.Debug($"[LevelGenTrace] RoomFilterBaked_Post: {ex.Message}"); }
        }

        // E: SpawnEventsNode.PickEventFromList
        private static void PickEventFromList_Pre(object __instance, object[] __args)
        {
            try
            {
                if (_memberDumpDone.Add("pickEvent")) DumpFields(__instance, "SpawnEventsNode.PickEventFromList(instance)");
                if (!Plugin.Cfg.LogLevelGenTrace.Value) return;
                if (!AllowFirstN("pickEvent")) return;
                string args = __args == null ? "" : string.Join(",", __args.Select(a => DescribeArg(a)));
                Plugin.Log.Info($"[EventPickTrace] PRE scene={NetGameplayProbeManager.GetLocalRunKey()} args=[{args}]");
            }
            catch (Exception ex) { if (Plugin.Cfg.EnableDebugLog.Value) Plugin.Log.Debug($"[LevelGenTrace] PickEventFromList_Pre: {ex.Message}"); }
        }

        private static void PickEventFromList_Post(object __result)
        {
            try
            {
                NetGameplayProbeManager.NoteLevelGenTrace("EventPick");
                if (!Plugin.Cfg.LogLevelGenTrace.Value) return;
                if (!AllowFirstN("pickEventResult")) return;
                Plugin.Log.Info($"[EventPickTrace] selected={DescribeArg(__result)}");
            }
            catch (Exception ex) { if (Plugin.Cfg.EnableDebugLog.Value) Plugin.Log.Debug($"[LevelGenTrace] PickEventFromList_Post: {ex.Message}"); }
        }

        // F: FinalizeAndMutateUnitsNode.GetMutation result + Unit.ToggleMutation
        private static void GetMutation_Post(object __instance, object[] __args, object __result)
        {
            try
            {
                NetGameplayProbeManager.NoteLevelGenTrace("Mutation");
                if (_memberDumpDone.Add("getMutation") && __args != null && __args.Length > 0 && __args[0] != null)
                    DumpFields(__args[0], "GetMutation.arg0");
                if (!Plugin.Cfg.LogLevelGenTrace.Value) return;
                if (!AllowFirstN("getMutation")) return;
                string unit = (__args != null && __args.Length > 0) ? DescribeArg(__args[0]) : "?";
                Plugin.Log.Info($"[MutationTrace] GetMutation scene={NetGameplayProbeManager.GetLocalRunKey()} unit={unit} selected={DescribeArg(__result)}");
            }
            catch (Exception ex) { if (Plugin.Cfg.EnableDebugLog.Value) Plugin.Log.Debug($"[LevelGenTrace] GetMutation_Post: {ex.Message}"); }
        }

        private static void ToggleMutation_Pre(object __instance, object[] __args)
        {
            try
            {
                NetGameplayProbeManager.NoteLevelGenTrace("ToggleMutation");
                if (!Plugin.Cfg.LogLevelGenTrace.Value) return;
                if (!AllowFirstN("toggleMutation")) return;
                string mutation = (__args != null && __args.Length > 0) ? DescribeArg(__args[0]) : "?";
                string on = (__args != null && __args.Length > 1) ? __args[1]?.ToString() ?? "?" : "?";
                string unitName = (__instance is Component c && c != null && c.gameObject != null) ? c.gameObject.name : __instance.GetType().Name;
                Plugin.Log.Info($"[MutationTrace] ToggleMutation scene={NetGameplayProbeManager.GetLocalRunKey()} unit={unitName} mutation={mutation} on={on}");
            }
            catch (Exception ex) { if (Plugin.Cfg.EnableDebugLog.Value) Plugin.Log.Debug($"[LevelGenTrace] ToggleMutation_Pre: {ex.Message}"); }
        }

        private static string DescribeArg(object? a)
        {
            if (a == null) return "<null>";
            if (a is string s) return s;
            if (a is UnityEngine.Object uo) return uo != null ? uo.name : "<destroyed>";
            if (a is ICollection col) return $"{a.GetType().Name}(count={col.Count})";
            return a.GetType().Name;
        }

        private static string GameObjectPath(object obj)
        {
            try
            {
                Transform? tr = (obj is Component c && c != null) ? c.transform : (obj is GameObject go && go != null ? go.transform : null);
                if (tr == null) return "<no-transform>";
                var parts = new List<string>();
                for (Transform? t = tr; t != null && parts.Count < 8; t = t.parent) parts.Add(t.name);
                parts.Reverse();
                return string.Join("/", parts);
            }
            catch { return "<err>"; }
        }

        private static int ReadCollectionCount(object obj, string fieldToken)
        {
            var v = ReadFieldLike(obj, fieldToken);
            if (v is ICollection col) return col.Count;
            return -1;
        }

        // ---- reflection helpers (all defensive; tolerate mangled <name>5__N fields) ----

        private static int ChildCount(object connector)
        {
            try { if (connector is Component c && c != null) return c.transform.childCount; } catch { }
            return 0;
        }

        private static string ConnectorTag(object connector)
        {
            try
            {
                if (!(connector is Component c) || c == null) return "<null>";
                string connName = c.gameObject != null ? c.gameObject.name : "?";
                string roomName = "";
                var roomObj = ReadField(connector, "room");
                if (roomObj is Component rc && rc != null) roomName = rc.gameObject != null ? rc.gameObject.name : "";
                else if (roomObj is GameObject rgo && rgo != null) roomName = rgo.name;
                return string.IsNullOrEmpty(roomName) ? connName : $"{roomName}.{connName}";
            }
            catch { return "<err>"; }
        }

        private static object? ReadField(object obj, string name)
        {
            try
            {
                for (Type? tt = obj.GetType(); tt != null; tt = tt.BaseType)
                {
                    var f = tt.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null) return f.GetValue(obj);
                }
            }
            catch { }
            return null;
        }

        // Exact-name member read: tries an instance property (parameterless getter) first, then a field,
        // walking the type hierarchy. Used for confirmed MakerGraphContext members (Seed, Set, ...).
        private static object? ReadMember(object obj, string name)
        {
            if (obj == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                for (Type? tt = obj.GetType(); tt != null; tt = tt.BaseType)
                {
                    var p = tt.GetProperty(name, flags);
                    if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
                        return p.GetValue(obj, null);
                }
            }
            catch { }
            return ReadField(obj, name);
        }

        private static string MemberNumber(object obj, string name)
        {
            var v = ReadMember(obj, name);
            return v?.ToString() ?? "?";
        }

        // Read a field whose mangled name contains the given token (for coroutine locals <token>5__N).
        private static object? ReadFieldLike(object obj, string token)
        {
            try
            {
                foreach (var f in obj.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (f.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                        return f.GetValue(obj);
            }
            catch { }
            return null;
        }

        private static bool? ReadBool(object obj, string name) { var v = ReadField(obj, name); return v is bool b ? b : (bool?)null; }
        private static bool? ReadBoolLike(object obj, string token) { var v = ReadFieldLike(obj, token); return v is bool b ? b : (bool?)null; }
        private static string? ReadStrLike(object obj, string token)
        {
            var v = ReadFieldLike(obj, token);
            if (v == null) return null;
            if (v is string s) return s;
            if (v is UnityEngine.Object uo) return uo != null ? uo.name : null;
            return v.ToString();
        }
        private static string ReadAnyNumber(object obj, string token)
        {
            var v = ReadFieldLike(obj, token);
            return v?.ToString() ?? "?";
        }
        private static string ReadCountLike(object obj, string token)
        {
            var v = ReadFieldLike(obj, token);
            if (v is ICollection col) return col.Count.ToString();
            return v != null ? "?" : "0";
        }

        private static GameObject? ReadGameObject(object obj, string name)
        {
            var v = ReadField(obj, name);
            if (v is GameObject go) return go != null ? go : null;
            if (v is Component c) return c != null ? c.gameObject : null;
            return null;
        }

        private static bool AnyColliderEnabled(GameObject go)
        {
            try { foreach (var col in go.GetComponentsInChildren<Collider>(true)) if (col != null && col.enabled) return true; } catch { }
            return false;
        }

        private static bool AnyRendererEnabled(GameObject go)
        {
            try { foreach (var r in go.GetComponentsInChildren<Renderer>(true)) if (r != null && r.enabled) return true; } catch { }
            return false;
        }
    }
}
