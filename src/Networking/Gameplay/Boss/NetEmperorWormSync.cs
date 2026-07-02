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

        // ---- host send throttle ----
        private const float SendIntervalSeconds = 1f / 20f; // 20 Hz
        private static int   _sendSeq;
        private static float _lastSendAt = -999f;

        // ---- reflection cache (fields/method are private on EmperorBossWorm) ----
        private static bool       _reflectTried;
        private static FieldInfo  _rootField;
        private static FieldInfo  _rbField;
        private static MethodInfo _updateSectionsMi;

        private static void EnsureReflect(object worm)
        {
            if (_reflectTried) return;
            _reflectTried = true;
            var t = worm.GetType();
            _rootField        = AccessTools.Field(t, "root");
            _rbField          = AccessTools.Field(t, "rb");
            _updateSectionsMi = AccessTools.Method(t, "UpdateWormSections");
        }

        private static float _lastSendLogAt = -999f;

        // ================================================================ HOST
        /// <summary>Host: capture the worm head transform and broadcast it (throttled). Called from the worm's
        /// FixedUpdate prefix on the host (before the native movement runs — the ~1 frame lag is irrelevant at 20 Hz).</summary>
        public static void HostCapture(object worm)
        {
            if (!(worm is Component c) || c == null) return;
            float now = Time.realtimeSinceStartup;
            if (now - _lastSendAt < SendIntervalSeconds) return;
            _lastSendAt = now;

            Vector3 p = c.transform.position;
            float rotY = c.transform.eulerAngles.y;
            _sendSeq++;
            NetGameplaySyncBridge.BroadcastEmperorWormHead(p.x, p.y, p.z, rotY, _sendSeq);
            if (now - _lastSendLogAt > 1f)
            {
                _lastSendLogAt = now;
                Plugin.Log.Info($"[EmperorWormHead] host sent seq={_sendSeq} pos={p:F1}");
            }
        }

        private static float _lastRecvLogAt = -999f;

        // ================================================================ CLIENT
        /// <summary>Client: store a received head sample (drop out-of-order / duplicate).</summary>
        public static void OnHeadReceived(Vector3 pos, float rotY, int seq)
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
        }

        // Head assembly: colliders disabled once (worm root's own colliders; sections are separate scene objects).
        private static readonly System.Collections.Generic.HashSet<int> _headCollidersDisabled
            = new System.Collections.Generic.HashSet<int>();
        // Section Npc instanceID → its cached colliders (GetComponentsInChildren is done once per section, no per-frame alloc).
        private static readonly System.Collections.Generic.Dictionary<int, Collider[]> _sectionColliders
            = new System.Collections.Generic.Dictionary<int, Collider[]>();
        private static FieldInfo _wormNpcsField;
        private static FieldInfo _lastActiveField;

        /// <summary>Client visual-only worm: disable the colliders that PhysX would otherwise re-broadphase every substep
        /// as the head streams around. The worm root's own colliders are disabled once; each section's colliders are
        /// disabled except the current weakpoint (<c>lastActiveIndex</c>, which moves as sections are destroyed) so the
        /// player can still shoot the tail. Per-frame work is a cheap enabled-flag toggle (only written on change).</summary>
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

                // 2) Sections — keep only the current weakpoint hittable, disable the rest.
                if (_wormNpcsField == null)   _wormNpcsField   = AccessTools.Field(worm.GetType(), "wormNpcs");
                if (_lastActiveField == null) _lastActiveField = AccessTools.Field(worm.GetType(), "lastActiveIndex");
                if (!(_wormNpcsField?.GetValue(worm) is System.Collections.IList npcs) || npcs.Count == 0) return; // not spawned yet
                int vulnerable = (_lastActiveField?.GetValue(worm) is int li) ? li : npcs.Count - 1;

                for (int i = 0; i < npcs.Count; i++)
                {
                    if (!(npcs[i] is Component uc) || uc == null) continue;
                    int sid = uc.GetInstanceID();
                    if (!_sectionColliders.TryGetValue(sid, out var cols))
                    {
                        cols = uc.GetComponentsInChildren<Collider>(true);
                        _sectionColliders[sid] = cols;
                    }
                    bool keep = (i == vulnerable);
                    foreach (var col in cols)
                        if (col != null && col.enabled != keep) col.enabled = keep;
                }
            }
            catch (System.Exception ex) { Plugin.Log.Warn($"[EmperorWormHead] EnsureWormVisualOnly failed: {ex.Message}"); }
        }

        /// <summary>Reset per-encounter client state (call on scene/session change if needed).</summary>
        public static void ResetClient()
        {
            _hasHead = false;
            _headSeq = -1;
            _headRecvAt = 0f;
            _prevRecvAt = 0f;
            _headCollidersDisabled.Clear();
            _sectionColliders.Clear();
        }
    }
}
