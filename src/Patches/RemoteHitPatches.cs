using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using System.Reflection.Emit;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using SULFURTogether.Networking.Gameplay;
using UnityEngine;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// HR-1: the three hooks <see cref="RemoteHitResolver"/> reads while it settles a remote peer's hit. Each is a no-op
    /// outside a resolution (one int compare), so they are applied unconditionally — they are functional, not diagnostic.
    ///
    /// <list type="bullet">
    /// <item><c>Unit.ReceiveDamage(float, DamageSourceData, Hitmesh.Data, Vector3?)</c> prefix / postfix / finalizer — one
    /// frame per call, so the Crusader damage the game deals from inside the outer call is told apart from it. The base
    /// method is patched because Npc and Breakable override it and call it.</item>
    /// <item><c>Unit.CreateDamageLog</c> postfix — the final amount the game logs for that call (its only caller is
    /// ReceiveDamage).</item>
    /// <item><c>Npc.Die</c> prefix — runs before <c>GiveExperience</c>, which reads <c>damagedByPlayer</c>.</item>
    /// <item>HR-3: <c>Unit.OnGaveDamage</c> prefix — the lifesteal amount the game hands the remote peer's stand-in, which
    /// the client applies to its own player.</item>
    /// <item>HR-3: <c>AttributeEffect.DistributeVoodooDamage</c> transpiler — its share count starts from
    /// <see cref="RemoteHitResolver.VoodooShareCount"/> instead of the constant 1, so a remote shotgun hit spreads one
    /// share per pellet as it does for the host.</item>
    /// </list>
    /// </summary>
    internal static class RemoteHitPatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var receiveDamage = AccessTools.GetDeclaredMethods(typeof(Unit)).FirstOrDefault(m =>
                {
                    if (m.Name != "ReceiveDamage" || m.ReturnType != typeof(bool)) return false;
                    var p = m.GetParameters();
                    return p.Length == 4 && p[0].ParameterType == typeof(float) && p[1].ParameterType == typeof(DamageSourceData)
                        && p[2].ParameterType == typeof(Hitmesh.Data) && p[3].ParameterType == typeof(Vector3?);
                });
                var createDamageLog = AccessTools.GetDeclaredMethods(typeof(Unit)).FirstOrDefault(m =>
                {
                    if (m.Name != "CreateDamageLog") return false;
                    var p = m.GetParameters();
                    return p.Length == 5 && p[0].ParameterType == typeof(float) && p[1].ParameterType == typeof(float)
                        && p[2].ParameterType == typeof(DamageTypes) && p[3].ParameterType == typeof(Hitmesh.Data)
                        && p[4].ParameterType == typeof(DamageSourceData);
                });
                var npcDie = AccessTools.DeclaredMethod(typeof(Npc), "Die", Type.EmptyTypes);

                if (receiveDamage != null)
                    harmony.Patch(receiveDamage,
                        prefix:    new HarmonyMethod(M(nameof(ReceiveDamage_Pre))),
                        postfix:   new HarmonyMethod(M(nameof(ReceiveDamage_Post))),
                        finalizer: new HarmonyMethod(M(nameof(ReceiveDamage_Finalizer))));
                if (createDamageLog != null)
                    harmony.Patch(createDamageLog, postfix: new HarmonyMethod(M(nameof(CreateDamageLog_Post))));
                if (npcDie != null)
                    harmony.Patch(npcDie, prefix: new HarmonyMethod(M(nameof(NpcDie_Pre))));

                ApplyNullSourceGuards(harmony);
                ApplyLifestealAndVoodoo(harmony);

                if (receiveDamage == null || createDamageLog == null || npcDie == null)
                    Plugin.Log.Error($"[RemoteHit] missing hook(s): ReceiveDamage={receiveDamage != null} CreateDamageLog={createDamageLog != null} Npc.Die={npcDie != null} — client hit results / host XP guard are degraded");
                else
                    Plugin.Log.Info("[RemoteHit] Patched Unit.ReceiveDamage/CreateDamageLog and Npc.Die (remote hit settlement).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[RemoteHit] Apply failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>A remote hit's source unit is null while the peer has no ghost on the host (downed, between scenes).
        /// <c>TerrorbaumBossEndlessHelper.OnDamageReceived</c> — an <c>onDamageRecieved</c> handler — reads
        /// <c>attacker.isPlayer</c> without a null check, and an exception there aborts ReceiveDamage after the health
        /// deduction and before the death check. Vanilla never passes it a sourceless hit; with no attacker it can only
        /// mean "not a player", which is the handler's own early return, so the guard takes exactly that branch.</summary>
        private static void ApplyNullSourceGuards(Harmony harmony)
        {
            try
            {
                var t = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.TerrorbaumBossEndlessHelper");
                var m = t == null ? null : AccessTools.DeclaredMethod(t, "OnDamageReceived");
                if (m == null)
                {
                    Plugin.Log.Warn("[RemoteHit] TerrorbaumBossEndlessHelper.OnDamageReceived not found — its null-source guard is inactive");
                    return;
                }
                harmony.Patch(m, prefix: new HarmonyMethod(M(nameof(TerrorbaumEndless_OnDamageReceived_Pre))));
            }
            catch (Exception ex) { Plugin.Log.Warn($"[RemoteHit] null-source guard failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void ApplyLifestealAndVoodoo(Harmony harmony)
        {
            try
            {
                var gave = AccessTools.DeclaredMethod(typeof(Unit), "OnGaveDamage", new[] { typeof(Unit), typeof(float) });
                if (gave != null) harmony.Patch(gave, prefix: new HarmonyMethod(M(nameof(OnGaveDamage_Pre))));
                else Plugin.Log.Warn("[RemoteHit] Unit.OnGaveDamage not found — a client's lifesteal on its own hits is inactive");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[RemoteHit] OnGaveDamage hook failed: {ex.GetType().Name}: {ex.Message}"); }

            try
            {
                var voodoo = AccessTools.DeclaredMethod(typeof(AttributeEffect), "DistributeVoodooDamage");
                if (voodoo != null) harmony.Patch(voodoo, transpiler: new HarmonyMethod(M(nameof(DistributeVoodooDamage_Transpiler))));
                else Plugin.Log.Warn("[RemoteHit] AttributeEffect.DistributeVoodooDamage not found — a client's Voodoo spreads one share per hit");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[RemoteHit] Voodoo hook failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void OnGaveDamage_Pre(Unit __instance, Unit target, float amount)
        {
            if (RemoteHitResolver.IsResolving) RemoteHitResolver.OnGaveDamage(__instance, target, amount);
        }

        /// <summary>Vanilla opens with <c>int num = 1; float num2 = 0.5f;</c> (IL <c>ldc.i4.1; stloc; ldc.r4 0.5</c>) and
        /// only overrides <c>num</c> from <c>sourceData.sourceWeapon</c>. The <c>ldc.i4.1</c> of that sequence becomes a
        /// call to <see cref="RemoteHitResolver.VoodooShareCount"/>(sourceData), which is 1 unless the damage being
        /// distributed is a remote peer's own hit. If the
        /// sequence is not found the method is left as it is.</summary>
        private static IEnumerable<CodeInstruction> DistributeVoodooDamage_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            for (int i = 0; i + 2 < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Ldc_I4_1) continue;
                if (!list[i + 1].IsStloc()) continue;
                if (list[i + 2].opcode != OpCodes.Ldc_R4 || !(list[i + 2].operand is float f) || f != 0.5f) continue;
                // ldarg.3 = sourceData (arg 0 is this): the helper applies the client's count only to its own hit.
                var load = new CodeInstruction(OpCodes.Ldarg_3);
                load.labels.AddRange(list[i].labels);
                load.blocks.AddRange(list[i].blocks);
                list[i] = load;
                list.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RemoteHitResolver), nameof(RemoteHitResolver.VoodooShareCount))));
                Plugin.Log.Info("[RemoteHit] Patched AttributeEffect.DistributeVoodooDamage (share count per projectile for remote hits).");
                return list;
            }
            Plugin.Log.Warn("[RemoteHit] DistributeVoodooDamage: 'int num = 1' not found — a client's Voodoo spreads one share per hit");
            return list;
        }

        private static bool TerrorbaumEndless_OnDamageReceived_Pre(object[] __args)
        {
            if (__args == null || __args.Length < 3 || __args[2] is not DamageSourceData sd) return true;
            if (sd.sourceUnit != null) return true;
            if (sd.sourceWeapon != null && sd.sourceWeapon.owner != null) return true;
            return false; // no attacker → not a player → the handler's own early return
        }

        private static MethodInfo M(string name) => typeof(RemoteHitPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

        // __state is frame index + 1, so its default 0 means "no frame": if this prefix were ever skipped (another prefix
        // on the method returning false), the postfix must not mistake the default for frame 0, the root.
        private static void ReceiveDamage_Pre(Unit __instance, out int __state)
        {
            __state = RemoteHitResolver.IsResolving ? RemoteHitResolver.OnReceiveDamageEnter(__instance) + 1 : 0;
        }

        private static void ReceiveDamage_Post(bool __result, int __state)
        {
            if (__state > 0) RemoteHitResolver.OnReceiveDamageExit(__state - 1, __result);
        }

        private static Exception? ReceiveDamage_Finalizer(Exception? __exception, int __state)
        {
            if (__exception != null && __state > 0) RemoteHitResolver.OnReceiveDamageFinalize(__state - 1);
            return __exception;
        }

        private static void CreateDamageLog_Post(Unit __instance, float __0, float __1, DamageTypes __2, Hitmesh.Data __3, DamageSourceData __4)
        {
            if (!RemoteHitResolver.IsResolving) return;
            RemoteHitResolver.OnCreateDamageLog(__instance, __0, __1, __2, __3.shapeId.partAndIndex, __4.isCritical);
        }

        private static void NpcDie_Pre(Npc __instance)
        {
            if (RemoteHitResolver.IsResolving) RemoteHitResolver.OnNpcDie(__instance);
        }
    }
}
