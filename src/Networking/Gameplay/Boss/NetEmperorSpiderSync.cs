using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// EMP-6b: host-authoritative Emperor phase-2 SPIDER sync (see Docs/EmperorBossAudit.md §9). Validated by the
    /// EMP-6a probe (Log259): the spider is kinematic and walks a FIXED waypoint path at a speed proportional to the
    /// LOCAL player distance, so the two ends drift in path progress (no physics spiral, unlike the worm). The spider
    /// <c>npc</c> is a normal roster unit but the client runs its OWN local boss (not a puppet), so its hits are not
    /// forwarded and its health diverges. This makes the spider host-authoritative, reusing the worm's building blocks:
    ///
    ///  • <b>Position</b> (EMP-3a analog): the host streams the spider body transform + the two waypoint indices; a
    ///    linked client suppresses its local <c>MaintainDistance</c> and applies the interpolated stream, keeping the
    ///    legs/IK + cosmetic root motion running natively.
    ///  • <b>Fight-start</b> (EMP-4 analog): the spider's <c>Initialize()</c> is a phase-2 dialog action (NodeCanvas);
    ///    gate it host-authoritatively so ALL ends stand up together regardless of who does the dialog / who has jumped
    ///    down into the phase-2 pit yet.
    ///  • <b>Damage</b> (EMP-3d analog): a client hit on the spider npc is routed to the host's real <c>ReceiveDamage</c>.
    ///  • <b>Defend / rapid-fire / death</b>: host broadcasts the discrete mechanic; the client replays the native method.
    /// </summary>
    internal static class NetEmperorSpiderSync
    {
        // ---- streamed transform samples (client), interpolated like the worm head (EMP-3a) ----
        private static Vector3 _pos, _prevPos;
        private static float   _rotY, _prevRotY;
        private static float   _recvAt, _prevRecvAt;
        private static int     _wp, _tgt;     // waypoint indices (snap to latest; drives CheckDeathTriggerReached convergence)
        private static float   _hp = -1f;     // streamed absolute currentHealth (EMP-6c: P2 arena has no enemy-state mirror)
        private static int     _seq = -1;
        private static bool    _hasSample;

        // ---- host send throttle ----
        private const float SendIntervalSeconds = 1f / 20f; // 20 Hz
        private static int   _sendSeq;
        private static float _lastSendAt = -999f, _lastSendLogAt = -999f, _lastRecvLogAt = -999f;

        // ---- live refs ----
        private static Component _hostSpiderRef;
        private static Component _clientSpiderRef;

        // ---- reflection cache ----
        private static bool       _reflectTried;
        private static FieldInfo  _rbField, _npcField, _curWpField, _tgtWpField, _isDeadField, _launcherField, _playerTransformField, _isDefendingField;
        private static MethodInfo _initializeMi, _triggerDefendMi, _onNpcDeathMi;
        private static MethodInfo _activateRapidFireMi; // on the rocket launcher type
        private static MethodInfo _doWhiteFlashMi;      // on the Npc type (hit feedback)
        private static System.Type _npcType;
        private static PropertyInfo _npcDialogProp;

        private static void EnsureReflect(object spider)
        {
            if (_reflectTried) return;
            _reflectTried = true;
            var t = spider.GetType();
            _rbField        = AccessTools.Field(t, "rb");
            _npcField       = AccessTools.Field(t, "npc");
            _curWpField     = AccessTools.Field(t, "currentWaypointIndex");
            _tgtWpField     = AccessTools.Field(t, "targetWaypointIndex");
            _isDeadField    = AccessTools.Field(t, "isDead");
            _isDefendingField = AccessTools.Field(t, "isDefending");
            _launcherField  = AccessTools.Field(t, "emperorBossSpiderRocketLauncher");
            _playerTransformField = AccessTools.Field(t, "playerTransform");
            _initializeMi   = AccessTools.Method(t, "Initialize");
            _triggerDefendMi= AccessTools.Method(t, "TriggerDefendPhase");
            _onNpcDeathMi   = AccessTools.Method(t, "OnNpcDeath");
            var launcherT = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.EmperorBossSpiderRocketLauncher");
            if (launcherT != null) _activateRapidFireMi = AccessTools.Method(launcherT, "ActivateRapidFire");
            _npcType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Npc");
            if (_npcType != null)
            {
                _npcDialogProp = _npcType.GetProperty("dialog", BindingFlags.Public | BindingFlags.Instance);
                _doWhiteFlashMi = AccessTools.Method(_npcType, "DoWhiteFlash");
            }
        }

        /// <summary>True while a spider is live on this end (for scoping any diagnostics).</summary>
        public static bool IsSpiderActive => (_hostSpiderRef ?? _clientSpiderRef) != null;

        private static bool ReadIsDead(object spider)
            => _isDeadField?.GetValue(spider) is bool b && b;

        // ================================================================ HOST: transform stream
        /// <summary>Host: capture the spider body transform + waypoint indices and broadcast (throttled). Called from the
        /// spider's LateUpdate prefix on the host (before native movement — the ~1 frame lag is irrelevant at 20 Hz).</summary>
        public static void HostCapture(object spider)
        {
            if (!(spider is Component c) || c == null) return;
            // New host spider instance (repeat encounter / level reload — no ResetClient is called on scene change): re-arm
            // the per-encounter once-flags so defend/rapid-fire/death broadcast again for the fresh fight. The fight-start
            // commit self-rearms already (it is keyed by instanceID).
            if (_hostSpiderRef == null || _hostSpiderRef.GetInstanceID() != c.GetInstanceID())
            {
                _defendAnnounced = _rapidFireAnnounced = _deathAnnounced = false;
                _hostLocalPlayer = null; // re-capture the real player transform for the new fight (see UpdateHostTarget)
                Plugin.Log.Info("[EmperorSpider] host tracking a new spider instance — per-encounter broadcast flags re-armed.");
            }
            _hostSpiderRef = c;
            EnsureReflect(spider);

            // #2 fix: the vanilla spider chases only its LOCAL player (host's own) → when the host hasn't jumped into the
            // pit it races at max speed hunting an absent player (Log260). Retarget it to the NEAREST of all players
            // (host + remote ghosts) every frame so it engages whoever is actually down there.
            UpdateHostTarget(c);

            // Honour a client fight-start request that arrived before the host spider existed.
            if (_hostFightStartPending && _fightStartCommittedId != c.GetInstanceID())
            {
                _hostFightStartPending = false;
                HostCommitFightStart(c, "deferred client request", _pendingStarterPeerId);
            }

            float now = Time.realtimeSinceStartup;
            if (now - _lastSendAt < SendIntervalSeconds) return;
            _lastSendAt = now;

            Vector3 p = c.transform.position;
            float rotY = c.transform.eulerAngles.y;
            int wp = _curWpField?.GetValue(spider) is int a ? a : 0;
            int tgt = _tgtWpField?.GetValue(spider) is int b ? b : 0;
            // #4 fix: the P2 arena has no net-run-state, so the generic enemy-state health mirror is inactive (Log260
            // enemyStateTargets=0). Stream the spider's absolute currentHealth so the client's boss bar tracks the host.
            float hp = -1f;
            var npc = _npcField?.GetValue(spider);
            if (npc != null) BossReflect.TryCallFloat(npc, "GetCurrentHealth", out hp);
            _sendSeq++;
            NetGameplaySyncBridge.BroadcastEmperorSpiderTransform(p.x, p.y, p.z, rotY, wp, tgt, hp, _sendSeq);
            if (now - _lastSendLogAt > 1f)
            {
                _lastSendLogAt = now;
                Plugin.Log.Info($"[EmperorSpider] host sent transform seq={_sendSeq} pos={p:F1} wp={wp}->{tgt} hp={hp:F0}");
            }
        }

        // ================================================================ CLIENT: transform stream
        public static void OnTransformReceived(Vector3 pos, float rotY, int wp, int tgt, float hp, int seq)
        {
            if (_seq != -1 && seq <= _seq) return; // drop out-of-order / duplicate
            float now = Time.realtimeSinceStartup;
            _prevPos    = _hasSample ? _pos    : pos;
            _prevRotY   = _hasSample ? _rotY   : rotY;
            _prevRecvAt = _hasSample ? _recvAt : now;
            _seq = seq; _pos = pos; _rotY = rotY; _wp = wp; _tgt = tgt; _hp = hp; _recvAt = now; _hasSample = true;
            if (now - _lastRecvLogAt > 1f)
            {
                _lastRecvLogAt = now;
                Plugin.Log.Info($"[EmperorSpider] client recv transform seq={seq} pos={pos:F1} wp={wp}->{tgt}");
            }
        }

        /// <summary>Client (linked): apply the interpolated host transform + waypoint indices to the local spider, before
        /// native LateUpdate runs (MaintainDistance is separately suppressed, so the applied pose is not overwritten; the
        /// legs/IK + CheckDeathTriggerReached then read the streamed pose/index). Called from the LateUpdate prefix.</summary>
        public static void DriveClientSpider(object spider)
        {
            if (!(spider is Component c) || c == null) return;
            EnsureReflect(spider);

            // New spider instance (repeat encounter / client level-reload — Log259 showed a client double-spawn): reset
            // the stream + terminal state so a prior spider's state can't wedge this one.
            if (_clientSpiderRef == null || _clientSpiderRef.GetInstanceID() != c.GetInstanceID())
            {
                _hasSample = false; _seq = -1; _recvAt = 0f; _prevRecvAt = 0f; _hp = -1f; _lastWrittenHp = -1f;
                _clientDeathApplied = false;
                Plugin.Log.Info("[EmperorSpider] client tracking a new spider instance — prior stream/death state reset.");
            }
            _clientSpiderRef = c;

            // Keep the body kinematic (native LateUpdate also does this; belt-and-suspenders).
            if (_rbField?.GetValue(spider) is Rigidbody rb && rb != null && !rb.isKinematic) rb.isKinematic = true;

            // Once *actually* dead (native ExecuteActualDeath ran — it drops rb.position by 1.6 for the death pose), stop
            // applying. NOTE: do NOT stop on _clientDeathApplied — that only marks OnNpcDeath (walk-to-death BEGINS); the
            // spider still needs the stream to converge to the death waypoint where CheckDeathTriggerReached fires the
            // real ExecuteActualDeath locally.
            if (ReadIsDead(spider)) return;
            if (!_hasSample) return; // no sample yet — leave the spider at its spawn pose

            // Snapshot interpolation (same as the worm head): render between the previous and latest sample over the
            // measured interval, ~one interval in the past — continuous motion, no 20 Hz stair-step.
            float interval = Mathf.Max(0.0001f, _recvAt - _prevRecvAt);
            float t = Mathf.Clamp01((Time.realtimeSinceStartup - _recvAt) / interval);
            Vector3 pos = Vector3.Lerp(_prevPos, _pos, t);
            var rot = Quaternion.Euler(0f, Mathf.LerpAngle(_prevRotY, _rotY, t), 0f);
            c.transform.SetPositionAndRotation(pos, rot);

            // Snap the waypoint indices to the latest sample so the client's CheckDeathTriggerReached matches the SAME
            // death waypoint as the host (it is suppressed from advancing them locally via the MaintainDistance block).
            _curWpField?.SetValue(spider, _wp);
            _tgtWpField?.SetValue(spider, _tgt);

            // #4 fix: write the streamed absolute health to the client spider npc (raiseEvent=true → the attached boss
            // health bar updates). The generic enemy-state mirror does not cover this arena (no net-run-state). The host
            // floors health at maxHealth*0.03 during walk-to-death, so this never reaches 0 → no spurious client death.
            if (_hp >= 0f && Mathf.Abs(_hp - _lastWrittenHp) > 0.5f) // only on real change → no per-frame onHealthChange spam
            {
                var npc = _npcField?.GetValue(spider);
                if (npc != null && NetGameplayProbeManager.TryWriteUnitHealth(npc, _hp, true)) _lastWrittenHp = _hp;
            }
        }
        private static float _lastWrittenHp = -1f;

        // #2 fix: keep a small anchor Transform at the nearest player's position and point the spider at it. The rocket
        // launcher targets GameManager.PlayerUnit/camera separately (per-end), so this does not affect rockets.
        private static Transform _targetAnchor;
        private static Transform _hostLocalPlayer;
        private static void UpdateHostTarget(Component spider)
        {
            try
            {
                // Capture the real local (host) player transform once, from the spider's own playerTransform BEFORE we
                // ever overwrite it (Initialize sets it to GameManager.Instance.PlayerObject.transform).
                if (_hostLocalPlayer == null)
                {
                    _hostLocalPlayer = _playerTransformField?.GetValue(spider) as Transform;
                    if (_hostLocalPlayer == null) return; // not initialized yet → leave vanilla targeting
                }
                if (_targetAnchor == null)
                {
                    var go = new GameObject("EmperorSpiderTargetAnchor");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _targetAnchor = go.transform;
                }
                Vector3 sp = spider.transform.position;
                Vector3 best = _hostLocalPlayer.position;
                float bestSq = (best - sp).sqrMagnitude;
                NetGameplaySyncBridge.ForEachRemotePlayerPosition(p =>
                {
                    float d = (p - sp).sqrMagnitude;
                    if (d < bestSq) { bestSq = d; best = p; }
                });
                _targetAnchor.position = best;
                _playerTransformField?.SetValue(spider, _targetAnchor);
            }
            catch { }
        }

        // Close THIS end's currently-open phase-2 dialog when the fight commits but this end did NOT pick the option
        // (the picking end's dialog closes natively). Real Graph.Stop(true); no-op when nothing is open. Mirrors the
        // worm's EMP-4 FinalizeLocalDialog — its omission here left the client's spider dialog stuck open (Log261: the
        // host committed but the client's dialog was never closed). DisableSpiderDialog (below) only prevents re-opening,
        // it does NOT close an already-open dialog — both are needed.
        private static void FinalizeLocalDialog(string origin)
        {
            if (BossDialogReflect.TryFinalizeCurrentDialog(out string detail))
                Plugin.Log.Info($"[EmperorSpider] fight-start ({origin}) closed local phase-2 dialog — {detail}");
        }

        // #1 fix: after the fight commits, DISABLE the phase-2 spider dialog on THIS end so a late-arriving player can't
        // re-trigger it (which would re-invoke Initialize mid-fight — Log260 showed a 2nd host Initialize). Nulls the
        // spider npc's dialog (HasDialog => dialog != null → Npc.Interact skips it), same primitive as the worm (EMP-4).
        private static void DisableSpiderDialog(Component spider)
        {
            try
            {
                EnsureReflect(spider);
                var npc = _npcField?.GetValue(spider) as Component;
                if (npc == null || _npcDialogProp == null || !_npcDialogProp.CanWrite) return;
                if (_npcDialogProp.GetValue(npc) != null)
                {
                    _npcDialogProp.SetValue(npc, null);
                    Plugin.Log.Info("[EmperorSpider] fight-start disabled P2 spider dialog (can no longer be re-triggered / re-Initialized during combat)");
                }
            }
            catch (System.Exception ex) { Plugin.Log.Warn($"[EmperorSpider] DisableSpiderDialog failed: {ex.Message}"); }
        }

        // ================================================================ EMP-6g: late-arrival teleport to the fight-starter
        // P2 is a separate underground pit reached only by jumping down, and a late arrival necessarily hits the spider's
        // dialog trigger (Npc.Interact). Since the fight already started, that dialog is disabled (DisableSpiderDialog), so
        // the interaction is otherwise dead. Instead, pull that player to the fight-starter (the first phase-2 triggerer)
        // so they catch up to the roaming spider. Called from the Npc.Interact prefix; returns true to swallow the interact.
        // Mode-agnostic: the late player can be the host (a client started) or a client (the host started).
        public static bool TryTeleportLateP2Player(object npc)
        {
            try
            {
                if (NetGameplaySyncBridge.BossMode == NetMode.Off || !NetGameplaySyncBridge.IsSessionActive) return false;
                if (_fightStartCommittedId == 0 || string.IsNullOrEmpty(_p2StarterPeerId)) return false; // fight not started
                if (_p2LatePulled) return false;                                        // already pulled this fight
                if (_p2StarterPeerId == NetGameplaySyncBridge.LocalPeerId) return false; // this end IS the starter — already in

                var spider = _hostSpiderRef ?? _clientSpiderRef ?? FindLocalSpider();
                if (spider == null) return false;
                EnsureReflect(spider);
                var spiderNpc = _npcField?.GetValue(spider) as Component;
                if (spiderNpc == null || !(npc is Component nc) || nc == null) return false;
                if (nc.gameObject.GetInstanceID() != spiderNpc.gameObject.GetInstanceID()) return false; // not the spider dialog

                Vector3 dest = ResolveStarterPos() ?? spider.transform.position; // live starter pos; fallback the spider itself
                TeleportLocalPlayerTo(dest);
                _p2LatePulled = true;
                Plugin.Log.Info($"[EmperorSpider] P2 late arrival pulled to fight-starter '{_p2StarterPeerId}' at ({dest.x:0.0},{dest.y:0.0},{dest.z:0.0})");
                return true; // swallow the (disabled) dialog interact
            }
            catch { return false; }
        }

        /// <summary>The fight-starter's live position, resolved by peerId from the synced remote-player positions. Null if
        /// the starter isn't a tracked remote (e.g. left / not yet streamed) → the caller falls back to the spider itself.</summary>
        private static Vector3? ResolveStarterPos()
        {
            Vector3? found = null;
            try
            {
                NetGameplaySyncBridge.ForEachRemotePlayerPositionWithPeer((peerId, pos) =>
                {
                    if (found == null && peerId == _p2StarterPeerId) found = pos;
                });
            }
            catch { }
            return found;
        }

        private static void TeleportLocalPlayerTo(Vector3 dest)
        {
            try
            {
                var gmType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
                var gm = gmType == null ? null : AccessTools.Property(gmType, "Instance")?.GetValue(null, null);
                var pu = gm == null ? null : AccessTools.Property(gmType, "PlayerUnit")?.GetValue(gm, null);
                if (pu is UnityEngine.Object uo && uo == null) pu = null;
                if (pu == null) { Plugin.Log.Warn("[EmperorSpider] P2 late teleport: local player unit missing"); return; }
                Vector3 d = dest + Vector3.up * 0.5f;
                var tp = AccessTools.Method(pu.GetType(), "TeleportTo", new[] { typeof(Vector3) })
                      ?? AccessTools.Method(pu.GetType(), "TeleportTo");
                if (tp != null) tp.Invoke(pu, new object[] { d });
                else if (pu is Component c && c != null) c.transform.position = d;
            }
            catch (System.Exception ex) { Plugin.Log.Warn($"[EmperorSpider] P2 late teleport failed: {ex.Message}"); }
        }

        /// <summary>Prefix on <c>MaintainDistance</c>: a linked client never advances the spider along the path itself
        /// (that is local-player-speed driven → divergence); the host stream drives it. Returns false to block on a
        /// linked client, true otherwise.</summary>
        public static bool SuppressClientMaintainDistance()
        {
            if (_inFightStartCommit) return true;
            return !(NetGameplaySyncBridge.BossMode == NetMode.Client
                     && SULFURTogether.Networking.NetLinkState.ClientLinked);
        }

        // ================================================================ EMP-4 analog: fight-start (Initialize) gate
        private static int  _fightStartCommittedId;
        private static int  _fightStartRequestedId;
        private static bool _hostFightStartPending;
        private static bool _inFightStartCommit;

        // EMP-6g: the P2 fight-starter (first phase-2 triggerer) + late-arrival pull state. P2 is a separate underground
        // pit reached only by jumping down; a player who drops in AFTER the fight started is teleported to the starter.
        private static string _p2StarterPeerId;      // peerId of whoever committed the fight (teleport target for late arrivals)
        private static bool   _p2LatePulled;         // this end's local player has already been pulled to the starter this fight
        private static string _pendingStarterPeerId; // requester peerId held when a client request arrives before the host spider is live

        /// <summary>Prefix on <c>EmperorBossSpider.Initialize</c>. Host commits inline + broadcasts and runs it; a linked
        /// client blocks its own local Initialize (its own phase-2 dialog) and requests the host commit, then runs it via
        /// the reentry invoke. Returns false to block, true to run.</summary>
        public static bool TryGateFightStart(object spider)
        {
            try
            {
                if (_inFightStartCommit) return true; // our own authoritative invoke
                if (!(spider is Component c) || c == null) return true;
                var mode = NetGameplaySyncBridge.BossMode;
                bool linked = mode == NetMode.Client && SULFURTogether.Networking.NetLinkState.ClientLinked;

                // #1 fix: once committed for this spider, BLOCK any further Initialize on every end. A late-arriving
                // player stepping the phase-2 dialog trigger re-invokes Initialize mid-fight (Log260: 2nd host
                // Initialize → re-subscribed damage, reset startup animation). The dialog is also nulled on commit, but
                // this is the hard guard.
                if ((mode == NetMode.Host || linked) && _fightStartCommittedId == c.GetInstanceID())
                    return false;

                if (mode == NetMode.Host)
                {
                    _fightStartCommittedId = c.GetInstanceID();
                    _hostSpiderRef = c;
                    _p2StarterPeerId = NetGameplaySyncBridge.LocalPeerId; // EMP-6g: host picked → host is the fight-starter
                    _p2LatePulled = false;
                    NetGameplaySyncBridge.BroadcastEmperorSpiderFightStart(_p2StarterPeerId);
                    DisableSpiderDialog(c); // picking host: prevent any re-trigger during combat
                    Plugin.Log.Info($"[EmperorSpider] host fight-start (local dialog) spider={c.GetInstanceID()} -> broadcast commit");
                    return true;
                }

                if (linked)
                {
                    if (_fightStartRequestedId != c.GetInstanceID())
                    {
                        _fightStartRequestedId = c.GetInstanceID();
                        NetGameplaySyncBridge.SendClientEmperorSpiderFightStart();
                        Plugin.Log.Info($"[EmperorSpider] client fight-start (local dialog) spider={c.GetInstanceID()} -> requested host commit (local start blocked)");
                    }
                    return false;
                }

                return true; // Off / unlinked-solo → vanilla
            }
            catch { return true; }
        }

        public static void HostOnClientFightStartRequest(string requesterPeerId)
        {
            _pendingStarterPeerId = requesterPeerId; // EMP-6g: the requesting client is the fight-starter
            var spider = _hostSpiderRef;
            if (!(spider is Component c) || c == null)
            {
                _hostFightStartPending = true;
                Plugin.Log.Info("[EmperorSpider] client fight-start request arrived before host spider is live — deferred.");
                return;
            }
            if (_fightStartCommittedId == c.GetInstanceID()) return; // already committed; its broadcast already went out
            HostCommitFightStart(c, "client request", requesterPeerId);
        }

        private static void HostCommitFightStart(Component spider, string origin, string starterPeerId)
        {
            _fightStartCommittedId = spider.GetInstanceID();
            _p2StarterPeerId = starterPeerId; // EMP-6g
            _p2LatePulled = false;
            NetGameplaySyncBridge.BroadcastEmperorSpiderFightStart(starterPeerId);
            InvokeInitialize(spider, origin);
            FinalizeLocalDialog(origin);   // host did NOT pick (a client did) → CLOSE its own open phase-2 dialog
            DisableSpiderDialog(spider);   // and prevent it re-opening during combat
            Plugin.Log.Info($"[EmperorSpider] host fight-start ({origin}) spider={spider.GetInstanceID()} starter={starterPeerId} -> committed + broadcast");
        }

        public static void OnFightStartCommitReceived(string starterPeerId)
        {
            // EMP-6g: record the starter up front so a late arrival can be pulled to it even if the spider ref isn't cached yet.
            if (!string.IsNullOrEmpty(starterPeerId)) { _p2StarterPeerId = starterPeerId; _p2LatePulled = false; }
            var spider = _clientSpiderRef ?? FindLocalSpider();
            if (!(spider is Component c) || c == null)
            {
                Plugin.Log.Warn("[EmperorSpider] fight-start commit received before any local spider exists — dropped.");
                return;
            }
            if (_fightStartCommittedId == c.GetInstanceID())
            {
                Plugin.Log.Info($"[EmperorSpider] fight-start commit ignored — spider={c.GetInstanceID()} already started.");
                return;
            }
            _fightStartCommittedId = c.GetInstanceID();
            InvokeInitialize(c, "host commit");
            FinalizeLocalDialog("host commit"); // this client did not pick → CLOSE its own open phase-2 dialog (Graph.Stop)
            DisableSpiderDialog(c);             // and prevent it re-opening during combat
            Plugin.Log.Info($"[EmperorSpider] client mirrored fight-start (host commit) spider={c.GetInstanceID()}");
        }

        private static void InvokeInitialize(Component spider, string origin)
        {
            EnsureReflect(spider);
            if (_initializeMi == null) { Plugin.Log.Warn($"[EmperorSpider] fight-start ({origin}): Initialize not resolved."); return; }
            _inFightStartCommit = true;
            try { _initializeMi.Invoke(spider, null); }
            catch (System.Exception ex) { Plugin.Log.Warn($"[EmperorSpider] fight-start ({origin}) Initialize invoke failed: {ex.Message}"); }
            finally { _inFightStartCommit = false; }
        }

        // The client may receive the commit before its LateUpdate has cached a spider ref (Initialize is blocked, so
        // DriveClientSpider hasn't run yet). Find the singleton via the static Instance property as a fallback.
        private static Component FindLocalSpider()
        {
            try
            {
                var t = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.EmperorBossSpider");
                var inst = t?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                return inst as Component;
            }
            catch { return null; }
        }

        // ================================================================ EMP-3d analog: damage authority (CLIENT -> HOST)
        private static int   _hitSendSeq;
        private static float _lastHitLogAt = -999f;

        /// <summary>Client: if <paramref name="npc"/> is the local spider's npc, forward the hit to the host and suppress
        /// the local damage. Returns true when the caller should swallow the damage. Called from Npc_ReceiveDamage_Pre.</summary>
        public static bool TryClientSpiderHit(object npc, float damage, int damageTypeInt)
        {
            try
            {
                if (npc == null) return false;
                if (NetGameplaySyncBridge.BossMode != NetMode.Client || !SULFURTogether.Networking.NetLinkState.ClientLinked) return false;
                var spider = _clientSpiderRef; if (spider == null) return false;
                if (_clientDeathApplied) return true; // dying — swallow strays
                EnsureReflect(spider);
                var spiderNpc = _npcField?.GetValue(spider) as Component;
                if (spiderNpc == null) return false;
                if (!(npc is Component nc) || nc == null) return false;
                if (nc.gameObject.GetInstanceID() != spiderNpc.gameObject.GetInstanceID()) return false;

                _hitSendSeq++;
                NetGameplaySyncBridge.SendClientEmperorSpiderHit(damage, damageTypeInt, _hitSendSeq);

                // Hit feedback (Log261: "P2 client health synced but no white-flash on hit"). The client's local damage is
                // suppressed so the vanilla DoWhiteFlash never fires — play it optimistically here. Skip while defending
                // (boss is invulnerable → the host rejects the hit, so no flash is correct) or dead.
                bool defending = _isDefendingField?.GetValue(spider) is bool d && d;
                if (!defending && !ReadIsDead(spider) && _doWhiteFlashMi != null)
                {
                    try { _doWhiteFlashMi.Invoke(spiderNpc, null); } catch { }
                }

                float now = Time.realtimeSinceStartup;
                if (now - _lastHitLogAt > 1f)
                {
                    _lastHitLogAt = now;
                    Plugin.Log.Info($"[EmperorSpider] client -> host spider hit dmg={damage:F1} dtype={damageTypeInt} seq={_hitSendSeq} (local suppressed)");
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Host: apply a client-forwarded hit to the real spider npc via vanilla ReceiveDamage, firing
        /// onDamageRecieved → OnDamageTaken so the real mechanic advances (defend / particle stages / rapid-fire / death).</summary>
        public static void HostApplyClientSpiderHit(float damage, int damageTypeInt, int seq)
        {
            try
            {
                var spider = _hostSpiderRef;
                if (spider == null) { Plugin.Log.Warn($"[EmperorSpider] client hit seq={seq} but no host spider is active — dropped."); return; }
                EnsureReflect(spider);
                var npc = _npcField?.GetValue(spider);
                if (npc == null) { Plugin.Log.Warn($"[EmperorSpider] client hit seq={seq}: host spider npc is null — dropped."); return; }

                object source = BossDamageReflect.ResolveHostPlayerUnit();
                bool ok = BossDamageReflect.TryApplyRealDamage(npc, damage, damageTypeInt, source, out bool vanillaResult, out string detail);
                float now = Time.realtimeSinceStartup;
                if (now - _lastHitLogAt > 1f)
                {
                    _lastHitLogAt = now;
                    Plugin.Log.Info($"[EmperorSpider] host applied client spider hit seq={seq} dmg={damage:F1} ok={ok} vanilla={vanillaResult} ({detail})");
                }
            }
            catch (System.Exception ex) { Plugin.Log.Warn($"[EmperorSpider] HostApplyClientSpiderHit seq={seq} failed: {ex.Message}"); }
        }

        // ================================================================ discrete events (HOST -> CLIENT)
        public const int EventDefend = 1, EventRapidFire = 2, EventDeath = 3;
        private static int  _eventSendSeq;
        private static bool _clientDeathApplied;
        private static bool _defendAnnounced, _rapidFireAnnounced, _deathAnnounced; // host: once each per encounter

        /// <summary>Host: the real TriggerDefendPhase just ran (from OnDamageTaken crossing 50 %). Tell clients to replay it.</summary>
        public static void HostAnnounceDefend(object spider)
        {
            if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;
            _hostSpiderRef = spider as Component;
            if (_defendAnnounced) return; _defendAnnounced = true;
            _eventSendSeq++;
            NetGameplaySyncBridge.BroadcastEmperorSpiderEvent(EventDefend, _eventSendSeq);
            Plugin.Log.Info($"[EmperorSpider] host defend-phase -> broadcast seq={_eventSendSeq}");
        }

        /// <summary>Host: the rocket launcher just went to rapid-fire (≤10 % health). Tell clients to replay it.</summary>
        public static void HostAnnounceRapidFire()
        {
            if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;
            if (_rapidFireAnnounced) return; _rapidFireAnnounced = true;
            _eventSendSeq++;
            NetGameplaySyncBridge.BroadcastEmperorSpiderEvent(EventRapidFire, _eventSendSeq);
            Plugin.Log.Info($"[EmperorSpider] host rapid-fire -> broadcast seq={_eventSendSeq}");
        }

        /// <summary>Host: OnNpcDeath just ran (walk-to-death begins). Tell clients to replay it in step.</summary>
        public static void HostAnnounceDeath(object spider)
        {
            if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;
            _hostSpiderRef = spider as Component;
            if (_deathAnnounced) return; _deathAnnounced = true;
            _eventSendSeq++;
            NetGameplaySyncBridge.BroadcastEmperorSpiderEvent(EventDeath, _eventSendSeq);
            Plugin.Log.Info($"[EmperorSpider] host death (OnNpcDeath) -> broadcast seq={_eventSendSeq}");
        }

        public static void OnEventReceived(int eventCode, int seq)
        {
            try
            {
                var spider = _clientSpiderRef ?? FindLocalSpider();
                if (!(spider is Component c) || c == null) { Plugin.Log.Warn($"[EmperorSpider] event code={eventCode} seq={seq} but no local spider — dropped."); return; }
                EnsureReflect(spider);
                switch (eventCode)
                {
                    case EventDefend:
                        _triggerDefendMi?.Invoke(spider, null);
                        Plugin.Log.Info($"[EmperorSpider] client mirrored defend-phase seq={seq}");
                        break;
                    case EventRapidFire:
                        var launcher = _launcherField?.GetValue(spider);
                        if (launcher != null && _activateRapidFireMi != null) _activateRapidFireMi.Invoke(launcher, null);
                        Plugin.Log.Info($"[EmperorSpider] client mirrored rapid-fire seq={seq}");
                        break;
                    case EventDeath:
                        if (_clientDeathApplied) { Plugin.Log.Info($"[EmperorSpider] death seq={seq} ignored — already applied."); return; }
                        _clientDeathApplied = true; // stop the transform stream from fighting the death sequence
                        // OnNpcDeath(Unit): sets isWalkingToDeath, destroys legs, floors health; the walk then converges to
                        // the same death waypoint via the transform stream, where the native CheckDeathTriggerReached fires
                        // ExecuteActualDeath locally (blower resolved correctly). Loot/checkpoint stay host-authoritative.
                        _onNpcDeathMi?.Invoke(spider, new object[] { null });
                        Plugin.Log.Info($"[EmperorSpider] client mirrored death (OnNpcDeath) seq={seq}");
                        break;
                    default:
                        Plugin.Log.Warn($"[EmperorSpider] unknown event code={eventCode} seq={seq}");
                        break;
                }
            }
            catch (System.Exception ex) { Plugin.Log.Warn($"[EmperorSpider] OnEventReceived code={eventCode} seq={seq} failed: {ex.Message}"); }
        }

        public static void ResetClient()
        {
            _hasSample = false; _seq = -1; _recvAt = 0f; _prevRecvAt = 0f; _hp = -1f; _lastWrittenHp = -1f;
            _clientSpiderRef = null; _hostSpiderRef = null; _hostLocalPlayer = null;
            _clientDeathApplied = false;
            _sendSeq = 0; _eventSendSeq = 0; _hitSendSeq = 0;
            _fightStartCommittedId = 0; _fightStartRequestedId = 0;
            _hostFightStartPending = false; _inFightStartCommit = false;
            _p2StarterPeerId = null; _p2LatePulled = false; _pendingStarterPeerId = null; // EMP-6g
            _defendAnnounced = _rapidFireAnnounced = _deathAnnounced = false;
        }
    }
}
