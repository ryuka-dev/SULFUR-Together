using System;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core.Units;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// HR-5 (HOST): enemy reactions that look for a player's hit. A remote peer's hit is settled with
    /// <c>DamageSourceData.isPlayer = false</c> and the peer's stand-in unit as attacker (whose <c>Unit.isPlayer</c> is
    /// false too), so that the host's own kill counter, explosion achievement, weapon-XP attribution and damage-feedback
    /// mods do not take it for the host's. A handful of enemy handlers key on the same flags to react to "a player hit
    /// me"; for those, and only while they run, the remote peer's hit counts as a player's:
    /// <list type="bullet">
    /// <item><c>CryoSnakes.OnFrostDamageReceived</c> — frosts and damages a melee attacker that is NOT a player. With the
    /// flag off, a client meleeing one was frozen and hurt, which a player never is.</item>
    /// <item><c>HellShrewSnipa.OnGetHurtAndDiscovered</c> — a hidden sniper shrew comes out when a player hits it hard
    /// from close by.</item>
    /// <item><c>CrawSentinel.RegisterDamageInstance</c> / <c>RegisterHitFromInstance</c> and the four-argument
    /// <c>ShavWaWarden.RegisterHitInstance</c> — count a player's hits to start or hold their block. These read the
    /// attacker unit's <c>isPlayer</c>, so the stand-in's flag is raised for the duration of the call and put back.</item>
    /// </list>
    /// Each prefix is a no-op outside a remote resolution, and for damage that is not the remote peer's own hit.
    /// Deliberately not covered: <c>DamageReverser</c> (it applies its status to the local player unit, i.e. the host's;
    /// there is no path yet to apply a status to a remote player), Terrorbaum's projectile absorb (synced by
    /// <c>TerrorbaumMechanicSync</c>). The AI alert at the end of <c>ReceiveDamage</c> is reproduced by
    /// <see cref="RemoteHitResolver"/>; organ drops are rolled on the client (<see cref="ClientHitEffects"/>).
    /// </summary>
    internal static class RemoteHitReactionPatches
    {
        public static void Apply(Harmony harmony)
        {
            int ok = 0, missing = 0;
            Patch(harmony, "PerfectRandom.Sulfur.Core.CryoSnakes", "OnFrostDamageReceived", nameof(SourceAsPlayer_Pre), null, ref ok, ref missing);
            Patch(harmony, "PerfectRandom.Sulfur.Gameplay.HellShrewSnipa", "OnGetHurtAndDiscovered", nameof(SourceAsPlayer_Pre), null, ref ok, ref missing);
            Patch(harmony, "PerfectRandom.Sulfur.Gameplay.CrawSentinel", "RegisterDamageInstance", nameof(SourceUnitAsPlayer_Pre), nameof(Restore_Finalizer), ref ok, ref missing);
            Patch(harmony, "PerfectRandom.Sulfur.Gameplay.CrawSentinel", "RegisterHitFromInstance", nameof(FromUnitAsPlayer_Pre), nameof(Restore_Finalizer), ref ok, ref missing);
            Patch(harmony, "PerfectRandom.Sulfur.Gameplay.ShavWaWarden", "RegisterHitInstance", nameof(FromUnitAsPlayer_Pre), nameof(Restore_Finalizer), ref ok, ref missing);
            if (missing > 0)
                Plugin.Log.Warn($"[RemoteHit] reaction hooks: {ok} patched, {missing} not found — those enemies do not react to a client's hit as to a player's");
            else
                Plugin.Log.Info($"[RemoteHit] Patched {ok} enemy reaction handlers (a client's hit counts as a player's).");
        }

        private static void Patch(Harmony harmony, string typeName, string method, string prefix, string? finalizer, ref int ok, ref int missing)
        {
            try
            {
                var t = AccessTools.TypeByName(typeName);
                // Every handler here has the (Unit, x, DamageSourceData, Vector3?) shape; ShavWaWarden also has a
                // two-argument RegisterHitInstance, which is not this one.
                MethodInfo? m = null;
                if (t != null)
                    foreach (var c in AccessTools.GetDeclaredMethods(t))
                        if (c.Name == method && c.GetParameters().Length == 4 && c.GetParameters()[2].ParameterType == typeof(DamageSourceData)) { m = c; break; }
                if (m == null) { missing++; Plugin.Log.Warn($"[RemoteHit] {typeName}.{method} not found"); return; }
                harmony.Patch(m, prefix: new HarmonyMethod(M(prefix)), finalizer: finalizer == null ? null : new HarmonyMethod(M(finalizer)));
                ok++;
            }
            catch (Exception ex) { missing++; Plugin.Log.Warn($"[RemoteHit] {typeName}.{method} hook failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static MethodInfo M(string name) => typeof(RemoteHitReactionPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

        /// <summary>The handler's own copy of the source says "player" when it is the remote peer's hit.</summary>
        private static void SourceAsPlayer_Pre(ref DamageSourceData __2)
        {
            if (RemoteHitResolver.IsResolving && RemoteHitResolver.IsRemoteSource(__2)) __2.isPlayer = true;
        }

        /// <summary>CrawSentinel.RegisterDamageInstance: the attacker is <c>sourceData.sourceUnit</c>.</summary>
        private static void SourceUnitAsPlayer_Pre(DamageSourceData __2, out Unit? __state)
        {
            __state = RemoteHitResolver.IsResolving ? RemoteHitResolver.RaiseStandInPlayerFlag(__2.sourceUnit) : null;
        }

        /// <summary>RegisterHitFromInstance / RegisterHitInstance: the attacker is the <c>fromUnit</c> argument.</summary>
        private static void FromUnitAsPlayer_Pre(Unit __1, out Unit? __state)
        {
            __state = RemoteHitResolver.IsResolving ? RemoteHitResolver.RaiseStandInPlayerFlag(__1) : null;
        }

        private static Exception? Restore_Finalizer(Exception? __exception, Unit? __state)
        {
            if (__state != null) RemoteHitResolver.LowerStandInPlayerFlag(__state);
            return __exception;
        }
    }
}
