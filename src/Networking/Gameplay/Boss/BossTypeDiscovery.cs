using System;
using System.Linq;
using System.Reflection;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase 5.4-E2: safe, namespace-agnostic discovery for the Emperor multi-entity boss. The previous adapter
    /// guessed PerfectRandom.Sulfur.Gameplay/.Core.EmperorBossFightHelper and the log showed "Could not find type".
    /// The real type is PerfectRandom.EmperorBossFightHelper (in PerfectRandom.Sulfur.Gameplay.dll). This prints
    /// every loaded type whose name contains "Emperor" with its assembly / base type / key members so the next
    /// phase can wire the worm/section pipeline against real signatures instead of guesses. Diagnostic only.
    /// </summary>
    internal static class BossTypeDiscovery
    {
        private static bool _done;

        public static void LogEmperorTypes()
        {
            if (_done) return;
            _done = true;
            try
            {
                int found = 0;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t != null).ToArray()!; }
                    catch { continue; }

                    foreach (var t in types)
                    {
                        if (t == null || t.Name.IndexOf("Emperor", StringComparison.Ordinal) < 0) continue;
                        if (t.IsNested) continue; // skip compiler-generated coroutine state machines
                        found++;
                        Plugin.Log.Info($"[BossTypeDiscovery] Emperor type: {t.FullName} | asm={asm.GetName().Name} | base={t.BaseType?.Name ?? "?"}");
                    }
                }

                // Detailed member dump for the core Emperor types if present.
                foreach (var name in new[] { "EmperorBossFightHelper", "EmperorBossWorm", "EmperorWormSectionController", "EmperorBossSpider" })
                {
                    var t = BossReflect.FindType(name);
                    if (t == null) { Plugin.Log.Info($"[BossTypeDiscovery] {name}: NOT FOUND"); continue; }
                    LogMembers(t);
                }

                Plugin.Log.Info($"[BossTypeDiscovery] done — {found} Emperor-named type(s) discovered.");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[BossTypeDiscovery] failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void LogMembers(Type t)
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            try
            {
                var fields = t.GetFields(F).Select(f => $"{f.FieldType.Name} {f.Name}").Take(40);
                var methods = t.GetMethods(F)
                    .Where(m => !m.IsSpecialName)
                    .Select(m => $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})")
                    .Take(40);
                Plugin.Log.Info($"[BossTypeDiscovery] {t.FullName} (asm={t.Assembly.GetName().Name}, base={t.BaseType?.Name})");
                Plugin.Log.Info($"[BossTypeDiscovery]   fields: {string.Join(" | ", fields)}");
                Plugin.Log.Info($"[BossTypeDiscovery]   methods: {string.Join(" | ", methods)}");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[BossTypeDiscovery] member dump failed for {t.FullName}: {ex.Message}");
            }
        }
    }
}
