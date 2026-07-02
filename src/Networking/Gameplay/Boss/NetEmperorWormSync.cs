using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// EMP-3a: host-authoritative Emperor phase-1 worm HEAD streaming.
    ///
    /// The client must NOT run the worm's autonomous ballistic <c>FixedUpdate</c> — that native rigidbody physics is
    /// the client-only ~1 fps (see <c>Docs/EmperorBossAudit.md</c> §8.5). Instead the host streams its worm head
    /// transform (~20 Hz, unreliable); a linked client keeps its local worm kinematic, skips the native
    /// <c>FixedUpdate</c>, drives the head from the stream, and runs only the cheap <c>UpdateWormSections</c>
    /// section-follow locally. Result: a visible, moving, synced worm with no physics spiral.
    ///
    /// This supersedes the EMP-2b stopgap (which blocked <c>StartMovement</c> entirely and left the worm invisible).
    /// Section destruction (EMP-3b) and death / phase-2 handoff (EMP-3c) are separate follow-ups.
    /// </summary>
    internal static class NetEmperorWormSync
    {
        // ---- streamed head samples (client) ----
        // EMP-3a interpolation: keep the last two samples and render between them ~one interval in the past, so the
        // worm moves continuously instead of stair-stepping to each 20 Hz sample. Fixed small delay (not a
        // velocity-proportional lag), so it still tracks fast jumps closely.
        private static Vector3 _headPos;      // latest sample
        private static float   _headRotY;
        private static float   _headRecvAt;
        private static Vector3 _prevHeadPos;  // previous sample (interpolation start)
        private static float   _prevHeadRotY;
        private static float   _prevRecvAt;
        private static int     _headSeq = -1;
        private static bool    _hasHead;
        private static float   _headTailHp = -1f;       // EMP-6d: streamed tail currentHealth (client boss bar)
        private static float   _lastWrittenTailHp = -1f;

        // ---- host send throttle ----
        private const float SendIntervalSeconds = 1f / 20f; // 20 Hz
        private static int   _sendSeq;
        private static float _lastSendAt = -999f;

        // ---- reflection cache (fields/method are private on EmperorBossWorm) ----
        private static bool       _reflectTried;
        private static FieldInfo  _rootField;
        private static FieldInfo  _rbField;
        private static MethodInfo _updateSectionsMi;
        // EMP-3b/3c: section-destroy + death mirror.
        private static MethodInfo _destroySectionMi;
        private static MethodInfo _moveVulnerableUpMi;
        private static MethodInfo _deathAnimationMi;
        private static FieldInfo  _lastSectionNpcField;
        // EMP-4: fight-start (dialog) commit.
        private static MethodInfo _startMovementMi;

        private static void EnsureReflect(object worm)
        {
            if (_reflectTried) return;
            _reflectTried = true;
            var t = worm.GetType();
            _rootField           = AccessTools.Field(t, "root");
            _rbField             = AccessTools.Field(t, "rb");
            _updateSectionsMi    = AccessTools.Method(t, "UpdateWormSections");
            _destroySectionMi    = AccessTools.Method(t, "DestroySection");
            _moveVulnerableUpMi  = AccessTools.Method(t, "MoveVulnerableSectionUp");
            _deathAnimationMi    = AccessTools.Method(t, "DeathAnimation");
            _lastSectionNpcField = AccessTools.Field(t, "lastSectionNpc");
            _startMovementMi     = AccessTools.Method(t, "StartMovement");
        }

        // Cached so a message-received callback (which has no worm reference of its own) can find the live worm.
        // Only one Emperor worm is ever active at a time, so a single static ref is sufficient.
        private static Component _clientWormRef;
        private static Component _hostWormRef; // EMP-3d: host's live worm, for applying inbound client hits.

        /// <summary>True while an Emperor worm is live on this end (its FixedUpdate has set our worm ref). Used to scope
        /// the Emperor dialog probe so it fires without needing the LogBossPreFight config toggle.</summary>
        public static bool IsWormActive
        {
            get { var w = _hostWormRef ?? _clientWormRef; return w != null; }
        }

        private static float _lastSendLogAt = -999f;

        // ================================================================ HOST
        /// <summary>Host: capture the worm head transform and broadcast it (throttled). Called from the worm's
        /// FixedUpdate prefix on the host (before the native movement runs — the ~1 frame lag is irrelevant at 20 Hz).</summary>
        public static void HostCapture(object worm)
        {
            if (!(worm is Component c) || c == null) return;
            _hostWormRef = c; // EMP-3d: so an inbound client worm-hit can reach the host's live worm + its lastSectionNpc.
            EnsureReflect(worm);
            // EMP-4: a client's fight-start request may have arrived before the host worm was live (host still loading
            // into the Emperor level). Now that the host worm exists, honour the deferred request.
            if (_hostFightStartPending && _fightStartCommittedWormId != c.GetInstanceID())
            {
                _hostFightStartPending = false;
                HostCommitFightStart(c, "deferred client request");
            }
            float now = Time.realtimeSinceStartup;
            if (now - _lastSendAt < SendIntervalSeconds) return;
            _lastSendAt = now;

            Vector3 p = c.transform.position;
            float rotY = c.transform.eulerAngles.y;
            // EMP-6d (P1 health fix): the worm arena, like the P2 spider arena, does not reliably drive the generic
            // enemy-state health mirror (the tail is a quarantined runtime add), so the client's boss bar was
            // intermittently stale (Log261). Stream the tail's absolute currentHealth alongside the head.
            float tailHp = -1f;
            var tail = _lastSectionNpcField?.GetValue(worm);
            if (tail != null) BossReflect.TryCallFloat(tail, "GetCurrentHealth", out tailHp);
            _sendSeq++;
            NetGameplaySyncBridge.BroadcastEmperorWormHead(p.x, p.y, p.z, rotY, tailHp, _sendSeq);
            if (now - _lastSendLogAt > 1f)
            {
                _lastSendLogAt = now;
                Plugin.Log.Info($"[EmperorWormHead] host sent seq={_sendSeq} pos={p:F1} tailHp={tailHp:F0}");
            }
        }

        private static float _lastRecvLogAt = -999f;

        // ================================================================ CLIENT
        /// <summary>Client: store a received head sample (drop out-of-order / duplicate).</summary>
        public static void OnHeadReceived(Vector3 pos, float rotY, float tailHp, int seq)
        {
            if (_headSeq != -1 && seq <= _headSeq) return;
            float now = Time.realtimeSinceStartup;
            // Shift latest → previous (on the first sample, previous == latest so we render a static pose, not a lerp
            // from the origin). The measured interval between recv times drives the interpolation so it self-adapts to
            // the real rate and to dropped Unreliable packets.
            _prevHeadPos  = _hasHead ? _headPos  : pos;
            _prevHeadRotY = _hasHead ? _headRotY : rotY;
            _prevRecvAt   = _hasHead ? _headRecvAt : now;
            _headSeq    = seq;
            _headPos    = pos;
            _headRotY   = rotY;
            _headTailHp = tailHp; // EMP-6d: streamed tail health for the client boss bar
            _headRecvAt = now;
            _hasHead    = true;
            if (now - _lastRecvLogAt > 1f)
            {
                _lastRecvLogAt = now;
                Plugin.Log.Info($"[EmperorWormHead] client recv seq={seq} pos={pos:F1}");
            }
        }

        /// <summary>Client: drive the local worm from the latest streamed head, keep it kinematic, and run the cheap
        /// section-follow. Called from the worm's FixedUpdate prefix on a linked client (which then skips native).</summary>
        public static void DriveClientWorm(object worm)
        {
            if (!(worm is Component c) || c == null) return;
            EnsureReflect(worm);

            // EMP-3c: nothing calls ResetClient() on a scene/session boundary today, so detect a fresh worm
            // instance by identity instead (a repeat encounter, retry, or reload spawns a NEW EmperorBossWorm).
            // Without this, _clientDeathApplied from a PRIOR worm would permanently stop this one from ever being
            // head-driven, and a stale _hasHead/_headSeq would render one stray frame at the old worm's last pose.
            if (_clientWormRef == null || _clientWormRef.GetInstanceID() != c.GetInstanceID())
            {
                _hasHead = false; _headSeq = -1; _headRecvAt = 0f; _prevRecvAt = 0f;
                _headTailHp = -1f; _lastWrittenTailHp = -1f;
                _clientDeathApplied = false;
                Plugin.Log.Info("[EmperorWorm] client tracking a new worm instance — prior worm's stream/death state reset.");
            }
            _clientWormRef = c; // EMP-3b/3c: so a network-thread-free message callback can reach the live worm.

            // EMP-3c: once the host's terminal death has been mirrored, the DeathAnimation coroutine on THIS worm
            // owns transform/rb.position (lerp to roomCenter, slam, etc.) — stop overwriting it with the head stream.
            if (_clientDeathApplied) return;

            // Keep the body kinematic so PhysX never integrates it (we also skip native FixedUpdate; belt-and-suspenders).
            if (_rbField?.GetValue(worm) is Rigidbody rb && rb != null && !rb.isKinematic)
                rb.isKinematic = true;

            // EMP-3a fix (Log247): the residual client-only lag tracks head-jump distance exactly — the client is
            // smooth while the head sits still and drops to 1-8 fps the instant it begins its 20 Hz long-range
            // teleports, worse on wide burrow spacing than on stairs (the user's observation). The worm logic is
            // already inert (native FixedUpdate bails on kinematic; the sections spawn kinematic), so the only cost
            // left is PhysX rebuilding the broadphase for ~11 kinematic colliders teleported across the static arena
            // every substep. The client worm is purely visual, so disable those colliders — keeping only the current
            // tail weakpoint hittable so the player can still shoot it.
            EnsureWormVisualOnly(worm, c);

            if (!_hasHead) return; // no sample yet — leave the worm at its spawn pose until the stream starts

            // Snapshot interpolation: render between the previous and latest sample over the measured send interval,
            // ~one interval behind real time. This removes the 20 Hz stair-step (the "worm teleport-flicker") and keeps
            // the head moving continuously so the sections trail in a stable formation instead of snapping+stretching.
            float interval = Mathf.Max(0.0001f, _headRecvAt - _prevRecvAt);
            float t = Mathf.Clamp01((Time.realtimeSinceStartup - _headRecvAt) / interval);
            Vector3 pos = Vector3.Lerp(_prevHeadPos, _headPos, t);
            var rot = Quaternion.Euler(0f, Mathf.LerpAngle(_prevHeadRotY, _headRotY, t), 0f);

            // Move the worm root GameObject (the head visual) and the section-follow anchor field `root` to the
            // interpolated pose, then run UpdateWormSections so the 10 body sections trail the head (game's own smoothing).
            c.transform.SetPositionAndRotation(pos, rot);
            if (_rootField?.GetValue(worm) is Transform root && root != null)
                root.SetPositionAndRotation(pos, rot);
            _updateSectionsMi?.Invoke(worm, null);

            // EMP-6d: write the streamed tail health to the client's boss-bar unit (raiseEvent=true) so the P1 bar tracks
            // the host. Only on real change → no per-frame onHealthChange spam. DestroySection events already sync the
            // section count; this fills in the continuous health between destroys (the generic enemy-state mirror is
            // unreliable for the quarantined worm tail — Log261 intermittent P1 bar).
            if (_headTailHp >= 0f && Mathf.Abs(_headTailHp - _lastWrittenTailHp) > 0.5f)
            {
                var tail = _lastSectionNpcField?.GetValue(worm);
                if (tail != null && NetGameplayProbeManager.TryWriteUnitHealth(tail, _headTailHp, true))
                    _lastWrittenTailHp = _headTailHp;
            }
        }

        // Head assembly: colliders disabled once (worm root's own colliders; sections are separate scene objects).
        private static readonly System.Collections.Generic.HashSet<int> _headCollidersDisabled
            = new System.Collections.Generic.HashSet<int>();
        // Section Npc instanceID → its cached colliders (GetComponentsInChildren is done once per section, no per-frame alloc).
        private static readonly System.Collections.Generic.Dictionary<int, Collider[]> _sectionColliders
            = new System.Collections.Generic.Dictionary<int, Collider[]>();
        private static FieldInfo _wormNpcsField;

        /// <summary>Client visual-only worm: disable the colliders that PhysX would otherwise re-broadphase every substep
        /// as the head streams around. The worm root's own colliders are disabled once; each section's colliders are
        /// disabled except the current weakpoint. EMP-3b note: the weakpoint is identified by the <c>lastSectionNpc</c>
        /// FIELD (a stable entity reference), NOT by indexing <c>wormNpcs[lastActiveIndex]</c> — <c>wormNpcs</c> keeps
        /// every section ever spawned in its original order and never shrinks, while <c>lastActiveIndex</c> is an index
        /// into the SHRINKING <c>wormSections</c>/<c>sectionControllers</c> lists (it decrements every
        /// <c>DestroySection</c>). Indexing wormNpcs by lastActiveIndex only happens to work before the first section
        /// dies; after that it points at an already-destroyed section and leaves the real (still-alive) tail's collider
        /// disabled — unhittable. Per-frame work is a cheap enabled-flag toggle (only written on change).</summary>
        private static void EnsureWormVisualOnly(object worm, Component c)
        {
            try
            {
                // 1) Head assembly colliders — once (sections are not children of the worm root, so this is head-only).
                int headId = c.GetInstanceID();
                if (_headCollidersDisabled.Add(headId))
                {
                    var headCols = c.GetComponentsInChildren<Collider>(true);
                    foreach (var col in headCols) if (col != null) col.enabled = false;
                    Plugin.Log.Info($"[EmperorWormHead] client disabled {headCols.Length} head collider(s) (visual-only worm).");
                }

                // 2) Sections — keep only the persistent vulnerable tail hittable, disable the rest.
                if (_wormNpcsField == null) _wormNpcsField = AccessTools.Field(worm.GetType(), "wormNpcs");
                if (!(_wormNpcsField?.GetValue(worm) is System.Collections.IList npcs) || npcs.Count == 0) return; // not spawned yet
                var tail = _lastSectionNpcField?.GetValue(worm) as Component;

                for (int i = 0; i < npcs.Count; i++)
                {
                    if (!(npcs[i] is Component uc) || uc == null) continue;
                    int sid = uc.GetInstanceID();
                    if (!_sectionColliders.TryGetValue(sid, out var cols))
                    {
                        cols = uc.GetComponentsInChildren<Collider>(true);
                        _sectionColliders[sid] = cols;
                    }
                    bool keep = ReferenceEquals(uc, tail);
                    foreach (var col in cols)
                        if (col != null && col.enabled != keep) col.enabled = keep;
                }
            }
            catch (System.Exception ex) { Plugin.Log.Warn($"[EmperorWormHead] EnsureWormVisualOnly failed: {ex.Message}"); }
        }

        // ================================================================ EMP-3d: damage authority (CLIENT -> HOST)

        private static int _wormHitSendSeq;
        private static float _lastWormHitLogAt = -999f;

        /// <summary>Client: if <paramref name="npc"/> is the local worm's vulnerable tail section, forward the hit to the
        /// host (which applies it to its real worm) and suppress the local damage. Returns true when the caller should
        /// swallow the damage (routed to host). Called from Npc_ReceiveDamage_Pre BEFORE the ordinary roster
        /// ClientHitRequest path — the worm's runtime-spawned tail is quarantined as "client-only" by that path, so
        /// hits would otherwise never reach the host (Log250-252). Single target, so no role/target resolution needed.</summary>
        public static bool TryClientWormHit(object npc, float damage, int damageTypeInt)
        {
            try
            {
                if (npc == null) return false;
                if (NetGameplaySyncBridge.BossMode != NetMode.Client || !SULFURTogether.Networking.NetLinkState.ClientLinked) return false;
                var worm = _clientWormRef;
                if (worm == null) return false; // no worm being driven → not an Emperor worm hit
                if (_clientDeathApplied) return true; // worm already dying — swallow stray hits, don't spam the host

                var tail = _lastSectionNpcField?.GetValue(worm) as Component;
                if (tail == null) return false;
                // The damaged Npc must be THE vulnerable tail. Only the tail's collider is left enabled
                // (EnsureWormVisualOnly), so in practice this is the only worm part the player can hit anyway.
                if (!(npc is Component nc) || nc == null) return false;
                if (nc.gameObject.GetInstanceID() != tail.gameObject.GetInstanceID()) return false;

                _wormHitSendSeq++;
                NetGameplaySyncBridge.SendClientEmperorWormHit(damage, damageTypeInt, _wormHitSendSeq);
                float now = Time.realtimeSinceStartup;
                if (now - _lastWormHitLogAt > 1f)
                {
                    _lastWormHitLogAt = now;
                    Plugin.Log.Info($"[EmperorWorm] client -> host worm hit dmg={damage:F1} dtype={damageTypeInt} seq={_wormHitSendSeq} (local damage suppressed)");
                }
                return true; // suppress local: the host is authoritative; its WeakpointHit drives destroy/death which we mirror
            }
            catch { return false; }
        }

        /// <summary>Host: apply a client-forwarded worm hit to the real vulnerable tail via the vanilla ReceiveDamage.
        /// This fires onDamageRecieved → WeakpointHit, advancing the real mechanic (health, section-destroy, death),
        /// whose results are broadcast back through the existing enemy health-state sync + EMP-3b/3c events.</summary>
        public static void HostApplyClientWormHit(float damage, int damageTypeInt, int seq)
        {
            try
            {
                var worm = _hostWormRef;
                if (worm == null) { Plugin.Log.Warn($"[EmperorWorm] client worm hit seq={seq} but no host worm is active — dropped."); return; }
                var tail = _lastSectionNpcField?.GetValue(worm);
                if (tail == null) { Plugin.Log.Warn($"[EmperorWorm] client worm hit seq={seq}: host lastSectionNpc is null — dropped."); return; }

                object? source = BossDamageReflect.ResolveHostPlayerUnit();
                bool ok = BossDamageReflect.TryApplyRealDamage(tail, damage, damageTypeInt, source, out bool vanillaResult, out string detail);
                float now = Time.realtimeSinceStartup;
                if (now - _lastWormHitLogAt > 1f)
                {
                    _lastWormHitLogAt = now;
                    Plugin.Log.Info($"[EmperorWorm] host applied client worm hit seq={seq} dmg={damage:F1} ok={ok} vanilla={vanillaResult} ({detail})");
                }
            }
            catch (System.Exception ex) { Plugin.Log.Warn($"[EmperorWorm] HostApplyClientWormHit seq={seq} failed: {ex.Message}"); }
        }

        // ================================================================ EMP-4: fight-start (dialog) authority
        // Log254: EmperorBossWorm.StartMovement is the fight-start (Initialize spawns the 10 sections, then the worm
        // emerges + the music/emergence play), invoked by the pre-fight dialog's final MultipleChoiceNode option —
        // fired INDEPENDENTLY on each end by that player's own dialog choice → unsynced start. Make it
        // host-authoritative: whoever picks the option commits, the host broadcasts, and every end runs the SAME
        // native StartMovement together. The client's local StartMovement (its own dialog pick) is blocked and
        // deferred to the host commit; the reentry flag lets our own authoritative invoke pass the gate.

        private static int  _fightStartCommittedWormId;   // instanceID of the worm whose fight-start is committed (0 = none)
        private static int  _fightStartRequestedWormId;    // client: worm id we've already requested a commit for
        private static bool _hostFightStartPending;        // host: a client requested before the host worm was live
        private static bool _inFightStartCommit;           // reentry: our own StartMovement invoke must pass the gate

        /// <summary>Prefix on <c>EmperorBossWorm.StartMovement</c>. Returns false to BLOCK the native start (a linked
        /// client deferring to the host's authoritative commit), true to let it run (single-player, unlinked-solo, the
        /// host's own commit, or our own reentry invoke). Registered unconditionally (functional, not diagnostic).</summary>
        public static bool TryGateFightStart(object worm)
        {
            try
            {
                if (_inFightStartCommit) return true; // our own authoritative invoke — always run
                if (!(worm is Component c) || c == null) return true;
                var mode = NetGameplaySyncBridge.BossMode;

                if (mode == NetMode.Host)
                {
                    // Host's own dialog pick starts the fight. Commit once (broadcast to clients), then let it run natively.
                    if (_fightStartCommittedWormId != c.GetInstanceID())
                    {
                        _fightStartCommittedWormId = c.GetInstanceID();
                        NetGameplaySyncBridge.BroadcastEmperorFightStart();
                        DisableBossDialogOnFightStart(); // picking host: its dialog closes natively; prevent any re-open
                        Plugin.Log.Info($"[EmperorWorm] host fight-start (local dialog) worm={c.GetInstanceID()} -> broadcast commit");
                    }
                    return true;
                }

                if (mode == NetMode.Client && SULFURTogether.Networking.NetLinkState.ClientLinked)
                {
                    // Linked client: never start the worm off its own dialog choice. Ask the host to commit and wait for
                    // the broadcast (which invokes StartMovement here via reentry). Request once per worm instance.
                    if (_fightStartCommittedWormId == c.GetInstanceID()) return false; // already committed → block strays
                    if (_fightStartRequestedWormId != c.GetInstanceID())
                    {
                        _fightStartRequestedWormId = c.GetInstanceID();
                        NetGameplaySyncBridge.SendClientEmperorFightStart();
                        Plugin.Log.Info($"[EmperorWorm] client fight-start (local dialog) worm={c.GetInstanceID()} -> requested host commit (local start blocked)");
                    }
                    return false;
                }

                return true; // Off / unlinked-solo client → vanilla start
            }
            catch { return true; }
        }

        /// <summary>Host: a client picked the fight-start option. Commit authoritatively (if not already), which starts
        /// the host worm and broadcasts to every client. If the host worm isn't live yet, defer to the next HostCapture.</summary>
        public static void HostOnClientFightStartRequest()
        {
            var worm = _hostWormRef;
            if (!(worm is Component c) || c == null)
            {
                _hostFightStartPending = true; // host still loading into the level — commit when the worm appears
                Plugin.Log.Info("[EmperorWorm] client fight-start request arrived before host worm is live — deferred.");
                return;
            }
            if (_fightStartCommittedWormId == c.GetInstanceID())
            {
                // Already committed (host picked first, or an earlier request). The commit broadcast already reached
                // this client, so nothing to do — its own OnFightStartCommitReceived starts its worm.
                return;
            }
            HostCommitFightStart(c, "client request");
        }

        /// <summary>Host: run the authoritative fight-start — mark committed, broadcast to clients, and invoke the real
        /// StartMovement on the host worm via reentry (bypassing the gate). Used for a client-originated commit; the
        /// host's OWN dialog pick commits inline in TryGateFightStart (StartMovement is already running natively).</summary>
        private static void HostCommitFightStart(Component worm, string origin)
        {
            _fightStartCommittedWormId = worm.GetInstanceID();
            NetGameplaySyncBridge.BroadcastEmperorFightStart();
            InvokeStartMovement(worm, origin);
            FinalizeLocalDialog(origin); // host did NOT pick the option (a client did) → close its own open pre-fight dialog
            DisableBossDialogOnFightStart(); // and prevent it from re-opening during combat
            Plugin.Log.Info($"[EmperorWorm] host fight-start ({origin}) worm={worm.GetInstanceID()} -> committed + broadcast");
        }

        /// <summary>Client: the host committed the fight-start — run the same native StartMovement on the local worm so
        /// Initialize/emergence/music begin in step with the host.</summary>
        public static void OnFightStartCommitReceived()
        {
            var worm = _clientWormRef;
            if (!(worm is Component c) || c == null)
            {
                Plugin.Log.Warn("[EmperorWorm] fight-start commit received before any client worm is being driven — dropped.");
                return;
            }
            if (_fightStartCommittedWormId == c.GetInstanceID())
            {
                Plugin.Log.Info($"[EmperorWorm] fight-start commit ignored — worm={c.GetInstanceID()} already started.");
                return;
            }
            _fightStartCommittedWormId = c.GetInstanceID();
            InvokeStartMovement(c, "host commit");
            FinalizeLocalDialog("host commit"); // this client did NOT pick the option → close its own open pre-fight dialog
            DisableBossDialogOnFightStart(); // and prevent it from re-opening during combat
            Plugin.Log.Info($"[EmperorWorm] client mirrored fight-start (host commit) worm={c.GetInstanceID()}");
        }

        // Close this end's open pre-fight dialog when the fight commits but this end did NOT pick the fight-start option
        // (the picking end's dialog closes natively via the option). Reuses the real Graph.Stop(true); no-ops when no
        // dialog is open. Matches the FF14 intent "committing the fight closes every player's boss dialog".
        private static void FinalizeLocalDialog(string origin)
        {
            if (BossDialogReflect.TryFinalizeCurrentDialog(out string detail))
                Plugin.Log.Info($"[EmperorWorm] fight-start ({origin}) closed local pre-fight dialog — {detail}");
        }

        private static System.Type   _npcType;
        private static PropertyInfo   _npcDialogProp;

        /// <summary>Fight committed: DISABLE the phase-1 worm boss dialog on THIS end so it can't be re-opened once
        /// combat starts (Log258: the speaker is the worm head Npc, <c>[0]-ShavwaEmperorWorm</c>, unitHasDialog=True).
        /// Nulls the Npc's <c>dialog</c> — <c>HasDialog =&gt; dialog != null</c>, so <c>Npc.Interact</c> (which the
        /// <c>NEWPlayerTrigger_StartEvent</c> re-fires) skips the dialog branch — the same trigger-agnostic primitive
        /// Cousin uses (BossAdapterBase.TryRemoveDialogInteractable). Idempotent. Does NOT touch the phase-2 SPIDER
        /// dialog (a different Npc, <c>ShavwaEmperorSpider</c>), which must stay reachable after the real StartPhase2.</summary>
        public static void DisableBossDialogOnFightStart()
        {
            try
            {
                Component wc = _hostWormRef ?? _clientWormRef;
                if (wc == null) return;
                if (_npcType == null) _npcType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Npc");
                if (_npcType == null) return;
                var npc = wc.GetComponent(_npcType) ?? wc.GetComponentInChildren(_npcType);
                if (npc == null) { Plugin.Log.Warn("[EmperorWorm] fight-start: worm boss Npc not found (dialog not disabled)"); return; }
                if (_npcDialogProp == null) _npcDialogProp = _npcType.GetProperty("dialog", BindingFlags.Public | BindingFlags.Instance);
                if (_npcDialogProp != null && _npcDialogProp.CanWrite && _npcDialogProp.GetValue(npc) != null)
                {
                    _npcDialogProp.SetValue(npc, null);
                    Plugin.Log.Info("[EmperorWorm] fight-start disabled P1 boss dialog (worm can no longer be re-talked during combat)");
                }
            }
            catch (System.Exception ex) { Plugin.Log.Warn($"[EmperorWorm] DisableBossDialogOnFightStart failed: {ex.Message}"); }
        }

        private static void InvokeStartMovement(Component worm, string origin)
        {
            EnsureReflect(worm);
            if (_startMovementMi == null) { Plugin.Log.Warn($"[EmperorWorm] fight-start ({origin}): StartMovement not resolved."); return; }
            _inFightStartCommit = true;
            try { _startMovementMi.Invoke(worm, null); }
            catch (System.Exception ex) { Plugin.Log.Warn($"[EmperorWorm] fight-start ({origin}) invoke failed: {ex.Message}"); }
            finally { _inFightStartCommit = false; }
        }

        // ================================================================ EMP-3b: section destroy (HOST -> CLIENT)

        private static int _sectionDestroySendSeq;

        /// <summary>Host: a real DestroySection(index) just ran natively — tell clients to mirror it. Called from a
        /// postfix on EmperorBossWorm.DestroySection (unconditional, host-only gate applied by the caller).</summary>
        public static void HostAnnounceSectionDestroy(object worm, int index)
        {
            _sectionDestroySendSeq++;
            NetGameplaySyncBridge.BroadcastEmperorWormSectionDestroy(_sectionDestroySendSeq);
            Plugin.Log.Info($"[EmperorWorm] host DestroySection index={index} -> broadcast seq={_sectionDestroySendSeq}");
        }

        /// <summary>Client: mirror one host-authoritative section destruction on the LOCAL worm. The client's own
        /// WeakpointHit never runs a real destroy (its damage is fully redirected to the host), so this is the only
        /// place client sections shrink. Replays the exact same call pair the host's WeakpointHit makes
        /// (DestroySection(lastActiveIndex-1) + MoveVulnerableSectionUp()) so gibs/frag/invuln/list-shrink/tail-teleport
        /// all run through the real native code — nothing here re-derives that math.</summary>
        public static void OnSectionDestroyReceived(int seq)
        {
            try
            {
                var worm = _clientWormRef;
                if (worm == null) { Plugin.Log.Warn($"[EmperorWorm] section-destroy seq={seq} received before any client worm is being driven — dropped."); return; }
                EnsureReflect(worm);
                if (_destroySectionMi == null || _moveVulnerableUpMi == null)
                { Plugin.Log.Warn("[EmperorWorm] section-destroy: DestroySection/MoveVulnerableSectionUp not resolved."); return; }

                int lastActive = ReadLastActiveIndex(worm);
                int index = lastActive - 1;
                if (index < 0)
                { Plugin.Log.Warn($"[EmperorWorm] section-destroy seq={seq}: lastActiveIndex={lastActive}, nothing left to destroy — dropped."); return; }

                _destroySectionMi.Invoke(worm, new object[] { index });
                _moveVulnerableUpMi.Invoke(worm, null);
                Plugin.Log.Info($"[EmperorWorm] client mirrored DestroySection seq={seq} index={index}");
            }
            catch (System.Exception ex) { Plugin.Log.Warn($"[EmperorWorm] OnSectionDestroyReceived seq={seq} failed: {ex.Message}"); }
        }

        private static FieldInfo _lastActiveIndexField;
        private static int ReadLastActiveIndex(object worm)
        {
            if (_lastActiveIndexField == null) _lastActiveIndexField = AccessTools.Field(worm.GetType(), "lastActiveIndex");
            return (_lastActiveIndexField?.GetValue(worm) is int li) ? li : -1;
        }

        // ================================================================ EMP-3c: death (HOST -> CLIENT)

        private static int  _deathSendSeq;
        private static bool _clientDeathApplied;

        /// <summary>Host: the real DeathAnimation() coroutine was just kicked off (WeakpointHit hit lethal health) —
        /// tell clients right away so both ends play the ~5s death sequence in step, not 5s apart.</summary>
        public static void HostAnnounceDeath(object worm)
        {
            _deathSendSeq++;
            NetGameplaySyncBridge.BroadcastEmperorWormDeath(_deathSendSeq);
            Plugin.Log.Info($"[EmperorWorm] host DeathAnimation started -> broadcast seq={_deathSendSeq}");
        }

        /// <summary>Client: mirror the host's terminal worm death by starting the SAME native DeathAnimation coroutine
        /// on the local worm. The client's own WeakpointHit never reaches lethal health (damage is fully
        /// host-authoritative), so this is the only place the client's worm dies. DeathAnimation itself plays the
        /// full vanilla sequence AND calls EmperorBossFightHelper.StartPhase2() at the end — the phase-1/phase-2
        /// handoff comes for free by reusing the real method instead of hand-rolling it.</summary>
        public static void OnDeathReceived(int seq)
        {
            try
            {
                if (_clientDeathApplied) { Plugin.Log.Info($"[EmperorWorm] death seq={seq} ignored — already applied for this worm."); return; }
                var worm = _clientWormRef;
                if (!(worm is MonoBehaviour mb) || mb == null)
                { Plugin.Log.Warn($"[EmperorWorm] death seq={seq} received before any client worm is being driven — dropped."); return; }
                EnsureReflect(worm);
                if (_deathAnimationMi == null)
                { Plugin.Log.Warn("[EmperorWorm] death: DeathAnimation not resolved."); return; }

                _clientDeathApplied = true; // stop the head stream from fighting DeathAnimation's own position lerp
                if (_deathAnimationMi.Invoke(worm, null) is System.Collections.IEnumerator routine)
                    mb.StartCoroutine(routine);
                Plugin.Log.Info($"[EmperorWorm] client mirrored DeathAnimation seq={seq}");
            }
            catch (System.Exception ex) { Plugin.Log.Warn($"[EmperorWorm] OnDeathReceived seq={seq} failed: {ex.Message}"); }
        }

        /// <summary>Reset per-encounter client state (call on scene/session change if needed).</summary>
        public static void ResetClient()
        {
            _hasHead = false;
            _headSeq = -1;
            _headRecvAt = 0f;
            _prevRecvAt = 0f;
            _headTailHp = -1f;
            _lastWrittenTailHp = -1f;
            _headCollidersDisabled.Clear();
            _sectionColliders.Clear();
            _clientWormRef = null;
            _hostWormRef = null;
            _clientDeathApplied = false;
            _sectionDestroySendSeq = 0;
            _deathSendSeq = 0;
            _wormHitSendSeq = 0;
            // EMP-4: fight-start commit state (self-rearms per worm instance, but clear on full teardown).
            _fightStartCommittedWormId = 0;
            _fightStartRequestedWormId = 0;
            _hostFightStartPending = false;
            _inFightStartCommit = false;
        }
    }
}
