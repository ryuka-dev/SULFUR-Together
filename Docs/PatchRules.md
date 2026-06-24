# SULFUR Together — Patch Rules

## Rules

1. **No guessing.**  
   Never patch a class, method, or field that has not been verified via ILSpy decompilation and/or a live runtime probe log.

2. **ReverseMapping.md first.**  
   Every patch must have a row in `ReverseMapping.md` with `Verified In Game = Yes` before it leaves probe-only status.

3. **Prefix / Postfix only.**  
   Default to `[HarmonyPrefix]` or `[HarmonyPostfix]`.  
   Transpilers are forbidden unless explicitly approved — they break on game updates.

4. **Every patch is disableable.**  
   Each patch checks a `CoopConfig` entry at the top and returns early if disabled.  
   At minimum, `EnableReverseProbe = false` must silence the whole probe system.

5. **High-frequency methods must be throttled.**  
   Methods called every frame (Update, FixedUpdate, AI loop) must use `ReverseProbeState` to limit log output.  
   FixedUpdate is not patched by default — requires explicit approval.

6. **try/catch around every patch body.**  
   A logging failure must never crash or hang the game.  
   Catch the exception and call `Plugin.Log.Error(...)`.

7. **No game-state mutation in probes.**  
   Probe patches are read-only. They must not change `__result`, call game state-modifying methods, or write to game fields.

8. **Host-only vs. all-clients annotation.**  
   When gameplay patches are added (Phase 3+), each must declare in a comment:  
   `// Executes on: Host | Client | All`

---

## Patch Class Template (for Phase 3+ gameplay patches)

```csharp
// ReverseMapping.md: <System> / <Class> / <Method>
// Executes on: Host | Client | All
// Purpose: <one sentence>
internal static class SomePatch
{
    [HarmonyPatch(typeof(VerifiedClass), nameof(VerifiedClass.VerifiedMethod))]
    [HarmonyPostfix]
    private static void Postfix(object __instance)
    {
        if (!Plugin.Cfg.SomeToggle.Value) return;
        try { /* implementation */ }
        catch (Exception ex) { Plugin.Log.Error($"SomePatch: {ex.Message}"); }
    }
}
```

---

## Forbidden Patterns

| Pattern | Reason |
|---------|--------|
| Patch a guessed class/method name | Will throw on load; violates Phase Gate |
| Transpiler without approval | Fragile across updates |
| Patch with no config disable | Cannot be turned off for debugging |
| Silent exception swallow | Masks bugs |
| Client modifies game state without Host validation | Desync |
| Patch points not in `ReverseMapping.md` | Untracked risk |
