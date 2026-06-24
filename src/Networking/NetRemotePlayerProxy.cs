using UnityEngine;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// A local-only visual marker for a remote peer. It is not a Player, Unit, Npc, damager, inventory owner, or network authority.
    /// </summary>
    internal sealed class NetRemotePlayerProxy
    {
        private readonly string _peerId;
        private GameObject? _root;
        private TextMesh?   _label;
        private Vector3     _targetPosition;
        private Quaternion  _targetRotation = Quaternion.identity;
        private bool        _hasAppliedState;
        private static bool _loggedCollisionDiag;

        // Phase 5.6-WS-3 billboard body (NPC prefab or embedded Father sprite).
        private GameObject? _bodyHolder;
        private GameObject? _bodyModel;
        private bool        _capsuleHidden;
        private Gameplay.RemotePlayerSpriteBody? _spriteBody;
        private float       _lookYaw;
        private float       _lookPitch;
        private bool        _moving;

        // Phase 5.6-WS-3 name label (in-world, golden text + black translucent background, camera-facing, pitch-limited).
        private GameObject? _labelHolder;
        private Transform?  _labelBg;
        private static readonly Color LabelTextColor = new Color(0.93f, 0.74f, 0.27f, 1f); // SULFUR theme gold
        private static readonly Color LabelBgColor   = new Color(0f, 0f, 0f, 0.55f);

        // Phase 5.6-WS-2 held weapon model.
        private GameObject? _weaponHolder;
        private GameObject? _weaponModel;
        private string      _heldWeaponSig = "none";

        // GameManager.Instance.PlayerUnit.gameObject.layer — the local player's physics layer. Cached after first resolve.
        private static int  _cachedPlayerLayer = -1; // -1 = not yet resolved (retry until a player is available)

        private static int ResolveLocalPlayerLayer()
        {
            if (_cachedPlayerLayer >= 0) return _cachedPlayerLayer;
            try
            {
                var gmType = HarmonyLib.AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
                var gm = gmType == null ? null : HarmonyLib.AccessTools.Property(gmType, "Instance")?.GetValue(null, null);
                var playerUnit = gm == null ? null : HarmonyLib.AccessTools.Property(gmType, "PlayerUnit")?.GetValue(gm, null);
                if (playerUnit is Component pc && pc != null)
                    _cachedPlayerLayer = pc.gameObject.layer;
            }
            catch { }
            return _cachedPlayerLayer;
        }

        public string PeerId { get { return _peerId; } }
        public string PlayerName { get; private set; } = "Remote";
        public int LastSequence { get; private set; } = -1;
        public float LastUpdatedAt { get; private set; }
        public bool IsVisible => _root != null && _root.activeSelf;
        /// <summary>The remote player's latest reported world position (authoritative report, not the lagged visual).</summary>
        public Vector3 TargetPosition => _targetPosition;
        /// <summary>The proxy's current on-screen position (interpolated) — what the local player visually bumps into.</summary>
        public Vector3 VisualPosition => _root != null ? _root.transform.position : _targetPosition;

        public NetRemotePlayerProxy(string peerId)
        {
            _peerId = string.IsNullOrWhiteSpace(peerId) ? "remote" : peerId;
        }

        public void Apply(NetPlayerTransformState state, float now)
        {
            EnsureCreated();
            if (_root == null) return;

            bool wasHidden = !_root.activeSelf;

            PlayerName = string.IsNullOrWhiteSpace(state.PlayerName) ? _peerId : state.PlayerName;
            LastSequence = state.Sequence;
            LastUpdatedAt = now;
            _targetPosition = state.Position;
            _targetRotation = Quaternion.Euler(0f, state.RotationY, 0f);
            _lookYaw = state.LookYaw;
            _lookPitch = state.LookPitch;
            _moving = state.Moving;
            _root.SetActive(true);

            if (_label != null && _label.text != PlayerName)
            {
                _label.text = PlayerName;
                UpdateNameLabelLayout();
            }

            if (!_hasAppliedState || wasHidden)
                SnapToTarget();

            _hasAppliedState = true;
        }

        public void Tick(float deltaTime, float now, float timeoutSeconds, float interpolationSpeed, float snapDistance)
        {
            if (_root == null) return;

            UpdateSpriteBodyState(deltaTime);

            if (timeoutSeconds > 0f && now - LastUpdatedAt > timeoutSeconds)
            {
                Hide();
                return;
            }

            if (!_root.activeSelf || !_hasAppliedState)
                return;

            if (snapDistance > 0f)
            {
                float sqrSnapDistance = snapDistance * snapDistance;
                if ((_root.transform.position - _targetPosition).sqrMagnitude > sqrSnapDistance)
                {
                    SnapToTarget();
                    UpdateLabelFacingCamera();
                    return;
                }
            }

            if (interpolationSpeed <= 0f)
            {
                SnapToTarget();
            }
            else
            {
                float lerp = Mathf.Clamp01(deltaTime * interpolationSpeed);
                _root.transform.position = Vector3.Lerp(_root.transform.position, _targetPosition, lerp);
                _root.transform.rotation = Quaternion.Slerp(_root.transform.rotation, _targetRotation, lerp);
            }

            UpdateLabelFacingCamera();
        }

        public void Hide()
        {
            if (_root != null)
                _root.SetActive(false);
            _hasAppliedState = false;
        }

        public void Destroy()
        {
            if (_root != null)
                Object.Destroy(_root);
            _root = null;
            _label = null;
            _hasAppliedState = false;
            // _weaponHolder/_weaponModel are children of _root and are destroyed with it; reset so a recreated proxy rebuilds.
            _weaponHolder = null;
            _weaponModel = null;
            _heldWeaponSig = "none";
            _bodyHolder = null;
            _bodyModel = null;
            _capsuleHidden = false;
            _spriteBody = null;
        }

        // -------------------------------------------------------------- Phase 5.6-WS-3 NPC billboard body

        public bool HasBody => _bodyModel != null;

        /// <summary>Attach an NPC billboard body (visual-only) and hide the placeholder capsule mesh. Proxy owns it.</summary>
        public void SetBody(GameObject body)
        {
            EnsureCreated();
            if (_root == null || body == null)
            {
                if (body != null) Object.Destroy(body);
                return;
            }

            if (_bodyHolder == null)
            {
                _bodyHolder = new GameObject("NpcBody");
                var bt = _bodyHolder.transform;
                bt.SetParent(_root.transform, worldPositionStays: false);
                // Neutralize the capsule's non-uniform scale, then apply the configurable body scale uniformly.
                Vector3 rs = _root.transform.localScale;
                float bodyScale = 1f;
                try { bodyScale = Plugin.Cfg.RemoteBodyScale.Value; } catch { }
                if (bodyScale <= 0f) bodyScale = 1f;
                bt.localScale = new Vector3(
                    (Mathf.Approximately(rs.x, 0f) ? 1f : 1f / rs.x) * bodyScale,
                    (Mathf.Approximately(rs.y, 0f) ? 1f : 1f / rs.y) * bodyScale,
                    (Mathf.Approximately(rs.z, 0f) ? 1f : 1f / rs.z) * bodyScale);
                // The sprite pivot is its CENTRE (waist) — like NPC billboards — so the billboard tilts around the waist,
                // not the feet. Anchor the holder at waist height = half the sprite's world height above the ground
                // (sprite is 2m tall at scale 1 → half-height ≈ bodyScale metres). FeetYOffset fine-tunes ground contact.
                float feetOffset = 0f;
                try { feetOffset = Plugin.Cfg.RemoteBodyFeetYOffset.Value; } catch { }
                float ry = Mathf.Approximately(rs.y, 0f) ? 1f : rs.y;
                float waistWorld = bodyScale + feetOffset; // half of (2m * bodyScale)
                bt.localPosition = new Vector3(0f, waistWorld / ry, 0f);
                bt.localRotation = Quaternion.identity;
            }

            if (_bodyModel != null)
                Object.Destroy(_bodyModel);

            _bodyModel = body;
            _spriteBody = body.GetComponent<Gameplay.RemotePlayerSpriteBody>();
            body.transform.SetParent(_bodyHolder.transform, worldPositionStays: false);
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.identity;
            body.SetActive(true);

            HideCapsuleMesh();
        }

        // WS-3: feed the Father sprite body the remote player's CAMERA/look yaw (front/back) and INPUT-moving flag
        // (walk animation — only on intentional movement, not pushes/sliding; stops cleanly when input stops).
        private void UpdateSpriteBodyState(float deltaTime)
        {
            if (_spriteBody != null)
                _spriteBody.SetState(_lookYaw, _moving);
            UpdateWeaponOrientation();
        }

        private void HideCapsuleMesh()
        {
            if (_capsuleHidden || _root == null) return;
            // _root itself is the capsule primitive; disable only its renderer (keep collider/transform for collision).
            var rend = _root.GetComponent<Renderer>();
            if (rend != null) rend.enabled = false;
            _capsuleHidden = true;
        }

        // -------------------------------------------------------------- Phase 5.6-WS-2 held weapon model

        public string HeldWeaponSig => _heldWeaponSig;

        /// <summary>Attach a freshly built (visual-only) weapon model to the proxy's hands. The proxy owns its lifetime.</summary>
        public void UpdateHeldWeapon(GameObject model, string signature)
        {
            EnsureCreated();
            if (_root == null || model == null)
            {
                if (model != null) Object.Destroy(model);
                return;
            }

            EnsureWeaponHolder();
            if (_weaponHolder == null)
            {
                Object.Destroy(model);
                return;
            }

            if (_weaponModel != null)
                Object.Destroy(_weaponModel);

            _weaponModel = model;
            var t = model.transform;
            t.SetParent(_weaponHolder.transform, worldPositionStays: false);
            // Offset within the (look-yaw-rotated, weapon-scaled) holder: front-right of the look direction, at hip.
            float wScale = SafeCfg(() => Plugin.Cfg.RemoteWeaponScale.Value, 1.4f);
            if (wScale <= 0f) wScale = 1f;
            float right = SafeCfg(() => Plugin.Cfg.RemoteWeaponRight.Value, 0.15f);
            float fwd = SafeCfg(() => Plugin.Cfg.RemoteWeaponForward.Value, 0.30f);
            t.localPosition = new Vector3(right / wScale, 0f, fwd / wScale); // holder units → world = ×wScale
            t.localRotation = Quaternion.identity;
            model.SetActive(true);
            _heldWeaponSig = signature ?? "none";
        }

        public void ClearHeldWeapon()
        {
            if (_weaponModel != null)
                Object.Destroy(_weaponModel);
            _weaponModel = null;
            _heldWeaponSig = "none";
        }

        private void EnsureWeaponHolder()
        {
            if (_weaponHolder != null || _root == null) return;
            _weaponHolder = new GameObject("HeldWeapon");
            var ht = _weaponHolder.transform;
            ht.SetParent(_root.transform, worldPositionStays: false);

            Vector3 rs = _root.transform.localScale;
            float wScale = SafeCfg(() => Plugin.Cfg.RemoteWeaponScale.Value, 1.4f);
            if (wScale <= 0f) wScale = 1f;
            // Neutralize the capsule's non-uniform scale and apply the weapon scale (holder-local 1 unit = wScale metres).
            ht.localScale = new Vector3(
                (Mathf.Approximately(rs.x, 0f) ? 1f : 1f / rs.x) * wScale,
                (Mathf.Approximately(rs.y, 0f) ? 1f : 1f / rs.y) * wScale,
                (Mathf.Approximately(rs.z, 0f) ? 1f : 1f / rs.z) * wScale);

            // Holder pivot = hip point: centred, at waist height above the feet (the body's feet are at the proxy origin).
            float hip = SafeCfg(() => Plugin.Cfg.RemoteWeaponHipHeight.Value, 1.2f);
            float ry = Mathf.Approximately(rs.y, 0f) ? 1f : rs.y;
            ht.localPosition = new Vector3(0f, hip / ry, 0f);
            ht.localRotation = Quaternion.identity; // updated each tick to the look yaw
        }

        // WS-2: orbit the held weapon around the hip to point along the remote player's look (camera) yaw each frame.
        private void UpdateWeaponOrientation()
        {
            if (_weaponHolder == null) return;
            // Orbit the weapon around the hip with the player's look yaw AND pitch (aim up/down), pivoting at the hip.
            _weaponHolder.transform.rotation = Quaternion.Euler(_lookPitch, _lookYaw, 0f);
        }

        private static float SafeCfg(System.Func<float> get, float fallback)
        {
            try { return get(); } catch { return fallback; }
        }

        private void EnsureCreated()
        {
            if (_root != null) return;

            _root = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            _root.name = "SULFUR Together Remote Player Proxy - " + _peerId;
            _root.transform.localScale = new Vector3(0.55f, 0.95f, 0.55f);

            // Keep the capsule's collider as a solid (non-trigger) blocker so the local player physically bumps into the
            // remote player instead of passing through (config-gated; restores pre-config player-player collision).
            // No Rigidbody is added, so it stays a static collider that just moves with the proxy transform each tick.
            var collider = _root.GetComponent<Collider>();
            if (collider != null)
            {
                bool collide = true;
                try { collide = Plugin.Cfg.EnableRemotePlayerProxyCollision.Value; } catch { }
                bool soft = true;
                try { soft = Plugin.Cfg.RemotePlayerCollisionSoft.Value; } catch { }

                if (collide && soft)
                {
                    // Soft mode does NOT use a physics collider at all — overlap is resolved by nudging the local player
                    // out each frame (NetRemotePlayerProxyManager.ApplySoftCollision). Removing the collider avoids the
                    // hard-wall artifacts (standing on heads, getting shoved across the room).
                    Object.Destroy(collider);
                }
                else if (collide)
                {
                    collider.isTrigger = false;
                    // The local player is a Rigidbody-based ExtendedAdvancedWalkerController (CMF). PhysX does NOT reliably
                    // sweep a *static* collider that we teleport via transform each frame, so the player tunnels through.
                    // A kinematic Rigidbody is built to be moved and acts as an immovable blocker for the player's
                    // dynamic body. We also put the proxy on the local player's OWN physics layer so it shares whatever
                    // collision-matrix relationship real players have with each other (the capsule defaults to Default).
                    var rb = _root.GetComponent<Rigidbody>();
                    if (rb == null) rb = _root.AddComponent<Rigidbody>();
                    rb.isKinematic = true;
                    rb.useGravity = false;
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

                    int playerLayer = ResolveLocalPlayerLayer();
                    if (playerLayer >= 0)
                        _root.layer = playerLayer;

                    if (!_loggedCollisionDiag)
                    {
                        _loggedCollisionDiag = true;
                        int proxyLayer = _root.layer;
                        bool ignored = (playerLayer >= 0) && UnityEngine.Physics.GetIgnoreLayerCollision(proxyLayer, playerLayer);
                        NetLogger.Info($"[RemotePlayerCollision] proxyLayer={proxyLayer} playerLayer={playerLayer} ignoreLayerCollision={ignored} colliderEnabled={collider.enabled} isTrigger={collider.isTrigger} rbKinematic={rb.isKinematic}");
                    }
                }
                else
                    Object.Destroy(collider);
            }

            var renderer = _root.GetComponent<Renderer>();
            if (renderer != null)
                renderer.material.color = new Color(0.55f, 0.25f, 0.95f, 0.72f);

            BuildNameLabel();

            _root.SetActive(false);
        }

        private void BuildNameLabel()
        {
            if (_root == null || _labelHolder != null) return;

            // Holder neutralizes the capsule's non-uniform scale (so text isn't squashed) and sits above the head.
            _labelHolder = new GameObject("NameLabel");
            var ht = _labelHolder.transform;
            ht.SetParent(_root.transform, worldPositionStays: false);
            Vector3 rs = _root.transform.localScale;
            ht.localScale = new Vector3(
                Mathf.Approximately(rs.x, 0f) ? 1f : 1f / rs.x,
                Mathf.Approximately(rs.y, 0f) ? 1f : 1f / rs.y,
                Mathf.Approximately(rs.z, 0f) ? 1f : 1f / rs.z);
            float bodyScale = SafeCfg(() => Plugin.Cfg.RemoteBodyScale.Value, 1.2f);
            float nameGap = SafeCfg(() => Plugin.Cfg.RemoteNameHeight.Value, 0.45f);
            float headWorld = 2f * bodyScale + nameGap;       // body ~2m tall at scale 1; label sits a gap above the head
            float ry = Mathf.Approximately(rs.y, 0f) ? 1f : rs.y;
            ht.localPosition = new Vector3(0f, headWorld / ry, 0f);

            // Black translucent background quad (no collider), behind the text.
            var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bg.name = "Bg";
            var bgCol = bg.GetComponent<Collider>();
            if (bgCol != null) Object.Destroy(bgCol);
            var bgRend = bg.GetComponent<MeshRenderer>();
            var bgShader = Shader.Find("Sprites/Default");
            if (bgShader != null) bgRend.material = new Material(bgShader);
            bgRend.material.color = LabelBgColor;
            bgRend.sortingOrder = 0;
            bgRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            bgRend.receiveShadows = false;
            bg.transform.SetParent(ht, false);
            bg.transform.localPosition = new Vector3(0f, 0f, 0.01f); // just behind the text
            _labelBg = bg.transform;

            // Golden text.
            var textObj = new GameObject("Text");
            textObj.transform.SetParent(ht, false);
            textObj.transform.localPosition = Vector3.zero;
            _label = textObj.AddComponent<TextMesh>();
            _label.text = PlayerName;
            _label.characterSize = SafeCfg(() => Plugin.Cfg.RemoteNameSize.Value, 0.03f);
            _label.fontSize = 64;
            _label.anchor = TextAnchor.MiddleCenter;
            _label.alignment = TextAlignment.Center;
            _label.color = LabelTextColor;
            var textRend = textObj.GetComponent<MeshRenderer>();
            if (textRend != null) textRend.sortingOrder = 1; // text over the background

            UpdateNameLabelLayout();
        }

        // Size the background to the current text bounds (+ padding).
        private void UpdateNameLabelLayout()
        {
            if (_label == null || _labelBg == null) return;
            var rend = _label.GetComponent<Renderer>();
            float w = 0.5f, h = 0.2f;
            if (rend != null)
            {
                Vector3 sz = rend.bounds.size; // world size; holder scale ~1 so ≈ local
                if (sz.x > 0.001f) w = sz.x;
                if (sz.y > 0.001f) h = sz.y;
            }
            _labelBg.localScale = new Vector3(w + 0.12f, h + 0.06f, 1f);
        }

        private void SnapToTarget()
        {
            if (_root == null) return;
            _root.transform.position = _targetPosition;
            _root.transform.rotation = _targetRotation;
        }

        private void UpdateLabelFacingCamera()
        {
            if (_labelHolder == null) return;
            var camera = Camera.main;
            if (camera == null) return;

            // Face the camera, upright, with the same clamped pitch as the body/NPC sprites.
            Vector3 anchor = _labelHolder.transform.position;
            Vector3 toCam = camera.transform.position - anchor;
            Vector3 flat = new Vector3(toCam.x, 0f, toCam.z);
            if (flat.sqrMagnitude < 1e-5f) flat = Vector3.forward;
            float pitchLimit = SafeCfg(() => Plugin.Cfg.RemoteBodyPitchLimit.Value, 25f);
            float maxY = flat.magnitude * Mathf.Tan(Mathf.Clamp(pitchLimit, 0f, 89f) * Mathf.Deg2Rad);
            float clampedY = Mathf.Clamp(toCam.y, -maxY, maxY);
            Vector3 dir = new Vector3(toCam.x, clampedY, toCam.z);
            if (dir.sqrMagnitude < 1e-6f) dir = flat;
            // TextMesh/Quad face +Z away from camera by default; LookRotation toward -dir keeps text readable (not mirrored).
            _labelHolder.transform.rotation = Quaternion.LookRotation(-dir.normalized, Vector3.up);
        }
    }
}
