using System;
using UnityEngine;
using PerfectRandom.Sulfur.Core.Units;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// SM-1: a Black Guild Sapper's mines exist on every end.
    ///
    /// <para>The sapper lays a mine from its behaviour tree: <c>BlackGuildSapper.SpawnMine</c> → <c>InstantiateMine</c> →
    /// <c>mineSO.SpawnUnitAsync(GameManager, groundPoint)</c>. Only the host runs that tree (a client's sapper is a copy the
    /// host drives), and the spawn's owner is the GameManager, which no runtime-spawn source covered, so a client never had
    /// the mine at all.</para>
    ///
    /// <para>The host decides whether and where a mine is laid: <c>InstantiateMine</c> is bracketed on the host and
    /// the spawn it makes is broadcast through the runtime-spawn channel (source <see cref="Source"/>). A client spawns
    /// the same unit at the host's point. A mine is a <c>Breakable</c>, not an NPC: it gets no spawn index and no puppet
    /// binding. Its cross-end identity is the one every destructible has, its spawn position (5.7-BR,
    /// <see cref="BreakableBreakManager"/>), so whichever end's mine goes off first (its own sensor, a bullet, an
    /// explosion) breaks the other end's too.</para>
    ///
    /// <para>Each end's mine explodes on that end, at the same point, and each player is judged by its own end's explosion
    /// with the game's own rule. The host therefore neither relays a mine explosion to a client nor forwards its
    /// stand-in's hit by one (<see cref="EnemyExplosionRelay"/>); the client's own mine explosion on its player is not a
    /// copy's hit and is kept. A mine's owner is destroyed by the break that queues its explosion, so mine explosions are
    /// recognised by their explosion type, never by their owner.</para>
    ///
    /// <para>A client's copy of a sapper never lays its own mine: the host's mines reach it.</para>
    /// </summary>
    // Executes on: Host (spawn bracket) and Client (mirror, copy guard)
    internal static class SapperMineSync
    {
        /// <summary>The runtime-spawn source of a mine.</summary>
        public const string Source = "SapperMine";

        /// <summary>HOST: &gt;0 while <c>BlackGuildSapper.InstantiateMine</c> runs up to its spawn call. The method is async
        /// void and calls <c>SpawnUnitAsync</c> before its first await, so the spawn is noted inside the bracket.</summary>
        public static int HostSpawnDepth;

        // Diagnostics only (read with an inspector); never consulted.
        public static int HostLaid, ClientMirrored, ClientDiscarded, ClientCopyBlocked;

        private static bool LogOn { get { try { return Plugin.Cfg.LogRuntimeSpawnSync.Value; } catch { return false; } } }

        // CLIENT: host mine positions whose mirror is still loading. An entry leaves when its mirror spawns, or after
        // MirrorLoadingTtl if the spawn never completes.
        private const float MirrorLoadingTtl = 10f;
        private struct LoadingMirror { public Vector3 Position; public float At; }
        private static readonly System.Collections.Generic.List<LoadingMirror> _loading = new System.Collections.Generic.List<LoadingMirror>();

        /// <summary>CLIENT: the mirror of a host mine at this point starts loading.</summary>
        public static void NoteMirrorLoading(Vector3 hostPosition)
        {
            float now = Time.realtimeSinceStartup;
            _loading.RemoveAll(l => now - l.At > MirrorLoadingTtl);
            _loading.Add(new LoadingMirror { Position = hostPosition, At = now });
        }

        /// <summary>CLIENT: is a mine mirror still loading at this point?</summary>
        public static bool IsMirrorLoadingNear(Vector3 position)
        {
            float now = Time.realtimeSinceStartup;
            float maxSqr = BreakableBreakManager.MatchEpsilon * BreakableBreakManager.MatchEpsilon;
            for (int i = 0; i < _loading.Count; i++)
                if (now - _loading[i].At <= MirrorLoadingTtl && (_loading[i].Position - position).sqrMagnitude <= maxSqr) return true;
            return false;
        }

        private static void NoteMirrorLoaded(Vector3 hostPosition)
        {
            for (int i = 0; i < _loading.Count; i++)
                if (_loading[i].Position == hostPosition) { _loading.RemoveAt(i); return; }
        }

        /// <summary>Prefix of <c>BlackGuildSapper.InstantiateMine</c>. A linked client's copy of a sapper does not lay a
        /// mine of its own (returns false); on the host the bracket opens.</summary>
        public static bool OnInstantiateMinePre(Component sapper)
        {
            try
            {
                if (!NetGameplaySyncBridge.IsSessionActive) return true;
                NetMode mode = NetGameplaySyncBridge.BossMode;
                if (mode == NetMode.Host) { HostSpawnDepth++; return true; }
                if (mode != NetMode.Client || sapper == null) return true;
                var npc = sapper.GetComponent<Npc>();
                if (npc == null) return true;
                if (!NetGameplayProbeManager.IsClientEnemyPuppetNpc(npc) && !NetGameplayProbeManager.IsHostBoundEntity(npc)) return true;
                ClientCopyBlocked++;
                if (LogOn) Plugin.Log.Info($"[SapperMine] client copy of a sapper does not lay its own mine ({sapper.name})");
                return false;
            }
            catch { return true; }
        }

        /// <summary>Finalizer of <c>BlackGuildSapper.InstantiateMine</c>: the host's bracket closes.</summary>
        public static void OnInstantiateMineExit()
        {
            if (HostSpawnDepth > 0) HostSpawnDepth--;
        }

        /// <summary>HOST: a mine spawn was just broadcast.</summary>
        public static void NoteHostLaid(Vector3 position)
        {
            HostLaid++;
            if (LogOn) Plugin.Log.Info($"[SapperMine] host laid a mine at {position:F2}");
        }

        /// <summary>CLIENT: the mirror of a host mine has spawned. It is registered as a destructible at the host's point
        /// at once (its own <c>Start</c> would do so a frame later), unless the host's mine already went off while this one
        /// was loading: then it is discarded, as the host has no mine there any more.</summary>
        public static void OnClientMirrorSpawned(object unit, Vector3 hostPosition)
        {
            NoteMirrorLoaded(hostPosition);
            try
            {
                if (!(unit is Breakable mine) || mine == null)
                {
                    Plugin.Log.Warn($"[SapperMine] client mirror is not a destructible: {unit?.GetType().Name ?? "null"}");
                    return;
                }
                if (BreakableBreakManager.ConsumeUnmatchedBreakNear(hostPosition))
                {
                    ClientDiscarded++;
                    UnityEngine.Object.Destroy(mine.gameObject);
                    Plugin.Log.Info($"[SapperMine] client discarded mirror at {hostPosition:F2}: the host's mine already went off");
                    return;
                }
                BreakableBreakManager.Register(mine);
                ClientMirrored++;
                if (LogOn) Plugin.Log.Info($"[SapperMine] client mirrored a mine at {hostPosition:F2} ({mine.name})");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[SapperMine] client mirror failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
