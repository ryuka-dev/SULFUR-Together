using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Unity.Mathematics;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Items;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Weapons;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// TB-MECH: Terrorbaum in-fight mechanic sync for the pure-puppet client (BossAuthority.md §10).
    /// <para><b>Root spin (TB-ROOT):</b> the rotating thorn vines are `rootPoints` rotated per-frame by
    /// `UpdateRotatingRoot` inside the helper's (suppressed) Update, with random reversals — not replicable
    /// deterministically. The host streams the Y angle (~8 Hz, only while `isDoingRoot` and moving); the client eases
    /// its `rootPoints` toward it. Vine DAMAGE is natively per-end-local (`LineRendererCollision` only tests the LOCAL
    /// player), so a correctly-angled client vine damages the client player exactly like vanilla — no forward, no
    /// double-hit. The random small-root lashes (`UpdateAttackRoot`) are host-rolled and mirrored by index so the
    /// dodge cue matches. `StopRoot` is mirrored so the client's vines retract with the host's.</para>
    /// <para><b>Sky spikes (TB-AOE):</b> `OnShootBulletFromSky` re-fires an absorbed player bullet DOWN from cloud
    /// height and drops a pooled `bulletDetectMarker` at the raycast ground point (the landing preview). None of that
    /// runs on the client (empty absorb queue + suppressed drivers). The host broadcasts each shot (ground point +
    /// speed + projectile visual identity); the client spawns the marker from the same pool + a ZERO-damage visual
    /// projectile falling onto it. Damage stays host-authoritative: the host's real spikes hit the remote players'
    /// ghost proxies (Hitmesh-carrying) and forward — a damaging client replica would double-hit.</para>
    /// </summary>
    internal static class TerrorbaumMechanicSync
    {
        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // ---------------------------------------------------------------- cached reflection (TerrorbaumBossFightHelper)
        private static bool _resolved;
        private static FieldInfo _fIsDoingRoot, _fRootPoints, _fAttackRootTimer, _fAttackRootHitTimer,
            _fRootAtkMin, _fRootAtkMax, _fSmallRoots, _fSkyQueue, _fMarkerRefs, _fMarkerPrefab, _fStartHeight,
            _fAbsorbQueue, _fTreeShake, _fCasing, _fCasingRate, _fAoeLoopSound, _fBulletSpawn, _fSpawnDelay,
            _fMaxCollect;
        private static MethodInfo _mAbsorbBullet;

        private static bool EnsureResolved(object helper)
        {
            if (_resolved) return _fRootPoints != null;
            _resolved = true;
            try
            {
                var t = helper.GetType(); // TerrorbaumBossFightHelper
                _fIsDoingRoot        = t.GetField("isDoingRoot", BF);
                _fRootPoints         = t.GetField("rootPoints", BF);
                _fAttackRootTimer    = t.GetField("attackRootTimer", BF);
                _fAttackRootHitTimer = t.GetField("attackRootHitTimer", BF);
                _fRootAtkMin         = t.GetField("randomRootAttackMinTimer", BF);
                _fRootAtkMax         = t.GetField("randomRootAttackMaxTimer", BF);
                _fSmallRoots         = t.GetField("smallRootsAnimators", BF);
                _fSkyQueue           = t.GetField("skyProjectiles", BF);
                _fMarkerRefs         = t.GetField("projectileMarkerRefs", BF);
                _fMarkerPrefab       = t.GetField("bulletDetectMarker", BF);
                _fStartHeight        = t.GetField("randomBulletStartHeight", BF);
                _fAbsorbQueue        = t.GetField("absorbedProjectiles", BF);
                _fTreeShake          = t.GetField("treeShakeObject", BF);
                _fCasing             = t.GetField("casingParticle", BF);
                _fCasingRate         = t.GetField("casingRateOverTime", BF);
                _fAoeLoopSound       = t.GetField("aoeBulletLoopSoundEvent", BF);
                _fBulletSpawn        = t.GetField("bulletSpawnpoint", BF);
                _fSpawnDelay         = t.GetField("projectileDelayEachSpawn", BF);
                _fMaxCollect         = t.GetField("projectileMaxCollect", BF);
                _mAbsorbBullet       = t.GetMethod("AbsorbPlayerBullet", BF);
            }
            catch { }
            return _fRootPoints != null;
        }

        private static bool LogOn { get { try { return Plugin.Cfg.LogBossEncounter.Value; } catch { return false; } } }

        // ================================================================ HOST: root-spin angle stream (TB-ROOT)

        private static float _lastSentAngle = float.MinValue;
        private static bool _wasDoingRoot;

        /// <summary>HOST (~8 Hz from the manager's Tick): while the root phase is active, broadcast the vines' current
        /// Y angle when it moved. The client's own vine damage keys off this geometry, so keep it fresh.</summary>
        public static void HostStreamRoot(object helper)
        {
            try
            {
                if (!(helper is Component hc) || hc == null || !EnsureResolved(helper)) return;
                bool doingRoot = _fIsDoingRoot?.GetValue(helper) is bool b && b;
                if (!doingRoot) { _wasDoingRoot = false; _lastSentAngle = float.MinValue; return; }
                _wasDoingRoot = true;
                if (!(_fRootPoints?.GetValue(helper) is GameObject rp) || rp == null) return;
                float y = rp.transform.localEulerAngles.y;
                if (_lastSentAngle != float.MinValue && Mathf.Abs(Mathf.DeltaAngle(_lastSentAngle, y)) < 0.5f) return;
                _lastSentAngle = y;
                NetBossEncounterManager.OnHostTerrorStateEvent(helper, "TerrorRootAngle", true, new Vector3(y, 0f, 0f));
            }
            catch { }
        }

        // ================================================================ HOST: small-root lash (TB-ROOT, host-rolled)

        /// <summary>HOST functional replacement of <c>UpdateAttackRoot</c> (Harmony prefix returns false when this
        /// returns true): identical native logic, but the rolled root INDEX is ours to broadcast — the native method
        /// rolls it internally, unreachable from a postfix. Non-host / unresolved → false (native runs untouched).</summary>
        public static bool TryHostLash(object helper)
        {
            try
            {
                if (NetGameplaySyncBridge.BossMode != NetMode.Host || !NetGameplaySyncBridge.IsSessionActive) return false;
                if (helper == null || !EnsureResolved(helper) || _fAttackRootTimer == null || _fAttackRootHitTimer == null
                    || _fRootAtkMin == null || _fRootAtkMax == null || _fSmallRoots == null) return false;
                if (!(_fSmallRoots.GetValue(helper) is Animator[] roots) || roots.Length == 0) return false;

                // Native UpdateAttackRoot, verbatim, with the index exposed:
                float timer = _fAttackRootTimer.GetValue(helper) is float ft ? ft : 0f;
                float hitAt = _fAttackRootHitTimer.GetValue(helper) is float fh ? fh : 0f;
                timer += Time.deltaTime;
                if (timer > hitAt)
                {
                    timer = 0f;
                    float min = _fRootAtkMin.GetValue(helper) is float mn ? mn : 5f;
                    float max = _fRootAtkMax.GetValue(helper) is float mx ? mx : 12f;
                    _fAttackRootHitTimer.SetValue(helper, UnityEngine.Random.Range(min, max));
                    int idx = UnityEngine.Random.Range(0, roots.Length);
                    if (roots[idx] != null) roots[idx].SetTrigger("Attack");
                    NetBossEncounterManager.OnHostTerrorStateEvent(helper, "TerrorRootLash:" + idx, false, default);
                }
                _fAttackRootTimer.SetValue(helper, timer);
                return true; // handled — skip the native roll
            }
            catch { return false; }
        }

        // ================================================================ HOST: sky-spike capture (TB-AOE)

        internal sealed class SkyShotState
        {
            public HashSet<object> MarkerKeys;
            public float Speed; public int Type, Caliber, Effect, Vfx, DamageType;
            public bool Valid;
        }

        /// <summary>HOST prefix of <c>OnShootBulletFromSky</c>: snapshot the landing-marker dictionary keys + peek the
        /// sky queue head (the projectile about to be re-fired down) for its speed/visual identity.</summary>
        public static SkyShotState CaptureSkyShotPre(object helper)
        {
            try
            {
                if (NetGameplaySyncBridge.BossMode != NetMode.Host || !NetGameplaySyncBridge.IsSessionActive) return null;
                if (helper == null || !EnsureResolved(helper) || _fSkyQueue == null || _fMarkerRefs == null) return null;

                var st = new SkyShotState { MarkerKeys = new HashSet<object>() };
                if (_fMarkerRefs.GetValue(helper) is System.Collections.IDictionary dict)
                    foreach (var k in dict.Keys) st.MarkerKeys.Add(k);

                if (!(_fSkyQueue.GetValue(helper) is System.Collections.IEnumerable queue)) return st;
                foreach (var head in queue) // Queue<(ID, FullProjectileDescription)> — first element = Peek
                {
                    if (head is System.Runtime.CompilerServices.ITuple tup && tup.Length >= 2
                        && tup[1] is PerfectRandom.Sulfur.Core.Weapons.FullProjectileDescription desc)
                    {
                        // Mirrors the native clamp: float num2 = Mathf.Clamp(math.length(ray.velocity), 5, 30)
                        st.Speed = Mathf.Clamp(new Vector3(desc.ray.velocity.x, desc.ray.velocity.y, desc.ray.velocity.z).magnitude, 5f, 30f);
                        st.Type = (int)desc.ray.type; st.Caliber = (int)desc.data.caliber;
                        st.Effect = (int)desc.ray.effect; st.Vfx = (int)desc.ray.vfxAsset;
                        st.DamageType = (int)desc.data.damageType;
                        st.Valid = true;
                    }
                    break;
                }
                return st;
            }
            catch { return null; }
        }

        /// <summary>HOST postfix: if the native shot added a landing marker, broadcast the shot (marker ground pos +
        /// the peeked identity) so the client shows the same preview + a falling visual spike.</summary>
        public static void BroadcastSkyShotPost(object helper, SkyShotState st)
        {
            try
            {
                if (st == null || !st.Valid || helper == null || _fMarkerRefs == null) return;
                if (!(_fMarkerRefs.GetValue(helper) is System.Collections.IDictionary dict)) return;
                foreach (System.Collections.DictionaryEntry e in dict)
                {
                    if (st.MarkerKeys.Contains(e.Key)) continue;
                    if (!(e.Value is Component marker) || marker == null) return; // new entry but unusable
                    string ev = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "TerrorSky:{0:F1}:{1}:{2}:{3}:{4}:{5}", st.Speed, st.Type, st.Caliber, st.Effect, st.Vfx, st.DamageType);
                    NetBossEncounterManager.OnHostTerrorStateEvent(helper, ev, true, marker.transform.position);
                    return;
                }
                // No new marker (no ground under the roll / forced shot off-mesh) — nothing to preview; skip.
            }
            catch { }
        }

        // ================================================================ HOST: absorb visual + upward volley (TB-ABSORB)

        private static float _lastAbsorbSentAt;

        /// <summary>HOST postfix of <c>AbsorbPlayerBullet</c>: the tree just swallowed a player bullet (TreeShake VFX
        /// natively plays only on this end). Broadcast it (throttled — absorbs come per hit while a player mags into
        /// the tree) so the client shakes the same tree.</summary>
        public static void HostAbsorbed(object helper)
        {
            try
            {
                if (NetGameplaySyncBridge.BossMode != NetMode.Host || !NetGameplaySyncBridge.IsSessionActive) return;
                float now = Time.realtimeSinceStartup;
                if (now - _lastAbsorbSentAt < 0.2f) return;
                _lastAbsorbSentAt = now;
                NetBossEncounterManager.OnHostTerrorStateEvent(helper, "TerrorAbsorb", false, default);
            }
            catch { }
        }

        /// <summary>HOST postfix of <c>Anim_OnSpawnProjectiles</c>: the upward volley just started (the tree spews the
        /// absorbed bullets skyward — casing particles + ShootingProjectiles pose + loop sound + one shot per
        /// <c>projectileDelayEachSpawn</c>). Broadcast a volley summary (count + cadence + the queue head's identity);
        /// the client replays the presentation with zero-damage visual shots.</summary>
        public static void HostVolleyStarted(object helper)
        {
            try
            {
                if (NetGameplaySyncBridge.BossMode != NetMode.Host || !NetGameplaySyncBridge.IsSessionActive) return;
                if (helper == null || !EnsureResolved(helper) || _fAbsorbQueue == null) return;
                if (!(_fAbsorbQueue.GetValue(helper) is System.Collections.ICollection queue) || queue.Count <= 0) return;
                float delay = _fSpawnDelay?.GetValue(helper) is float d && d > 0.01f ? d : 0.2f;
                float speed = 30f; int ty = 0, cal = 0, eff = 0, vfx = 0, dmg = 7;
                foreach (var head in (System.Collections.IEnumerable)queue)
                {
                    if (head is PerfectRandom.Sulfur.Core.Weapons.FullProjectileDescription desc)
                    {
                        // Mirrors the native clamp in SpawnProjectiles: num = clamp(length(velocity), 20, 40).
                        speed = Mathf.Clamp(new Vector3(desc.ray.velocity.x, desc.ray.velocity.y, desc.ray.velocity.z).magnitude, 20f, 40f);
                        ty = (int)desc.ray.type; cal = (int)desc.data.caliber;
                        eff = (int)desc.ray.effect; vfx = (int)desc.ray.vfxAsset; dmg = (int)desc.data.damageType;
                    }
                    break;
                }
                // The queue can far exceed projectileMaxCollect (each absorb enqueues 3; the absorb feed batches) —
                // cap generously so the client's replay runs about as long as the real volley (Log367: 471 entries).
                string ev = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "TerrorVolley:{0}:{1:F2}:{2:F1}:{3}:{4}:{5}:{6}:{7}", Mathf.Min(queue.Count, 600), delay, speed, ty, cal, eff, vfx, dmg);
                NetBossEncounterManager.OnHostTerrorStateEvent(helper, ev, false, default);
            }
            catch { }
        }

        // ================================================================ TB-ABSORB2: client bullets feed the host absorb

        // The tree's absorb feed is HOST-LOCAL: OnHitReceivedFrom counts the local player's ReceiveDamage calls and
        // TryAbsorbBullets converts the non-damaging surplus into AbsorbPlayerBullet(lastFiredProjectile). A client's
        // body shots never reach it — they are swallowed client-side (TB-DMG "body" sentinel) and their WS replicas on
        // the host are zero-damage visuals that never call ReceiveDamage. So: the client batches its swallowed body
        // hits (count + its own lastFiredProjectile identity) into one report per window; the host replays the native
        // AbsorbPlayerBullet with the same halving TryAbsorbBullets applies to a non-damaging batch. Everything
        // downstream (TreeShake broadcast, volley, sky spikes) already mirrors off the native calls.
        private const float AbsorbBatchWindow = 0.3f;
        private static readonly object _absorbLock = new object();
        private static string _abKey; private static object _abComponent;
        private static int _abCount; private static float _abDamage; private static int _abDtype;
        private static float _abSpeed; private static int _abType, _abCaliber, _abEffect, _abVfx;
        private static float _abStartedAt;

        /// <summary>CLIENT: one swallowed outside-window body hit. Collect it iff the native OnHitReceivedFrom would
        /// have (a held gun in Weapon0/1, not a raygun, a real projectile) — the identity comes from the LOCAL
        /// player's <c>lastFiredProjectile</c>, exactly the value the native feed snapshots on the host for its own
        /// player.</summary>
        public static void ClientNoteBodyHit(string key, object component, float damage, int dtype)
        {
            try
            {
                GameManager gm = GameManager.Instance;
                EquipmentManager em = gm != null ? gm.EquipmentManager : null;
                if (em == null) return;
                if (em.weaponCurrentSlot != InventorySlot.Weapon0 && em.weaponCurrentSlot != InventorySlot.Weapon1) return;
                FullProjectileDescription last = em.lastFiredProjectile;
                if (last.isRaygun || last.ray.type == ProjectileTypes.None) return;
                lock (_absorbLock)
                {
                    if (_abCount > 0 && !string.Equals(_abKey, key, StringComparison.Ordinal)) _abCount = 0; // key switched mid-batch — restart
                    if (_abCount == 0) { _abKey = key; _abComponent = component; _abStartedAt = Time.realtimeSinceStartup; }
                    _abCount++;
                    _abDamage = damage; _abDtype = dtype;
                    _abSpeed  = math.length(last.ray.velocity);
                    _abType   = (int)last.ray.type; _abCaliber = (int)last.data.caliber;
                    _abEffect = (int)last.ray.effect; _abVfx = (int)last.ray.vfxAsset;
                }
            }
            catch { }
        }

        /// <summary>CLIENT (manager Tick): when the batch window closed, hand out ONE absorb summary — the role string
        /// carries count + projectile identity; damage/dtype ride the request's normal fields.</summary>
        public static bool TryFlushClientAbsorb(out string key, out object component, out string role, out float damage, out int dtype)
        {
            key = null; component = null; role = null; damage = 0f; dtype = 0;
            lock (_absorbLock)
            {
                if (_abCount <= 0 || Time.realtimeSinceStartup - _abStartedAt < AbsorbBatchWindow) return false;
                key = _abKey; component = _abComponent; damage = _abDamage; dtype = _abDtype;
                role = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "absorb:{0}:{1:F1}:{2}:{3}:{4}:{5}", Mathf.Min(_abCount, 200), _abSpeed, _abType, _abCaliber, _abEffect, _abVfx);
                _abCount = 0; _abKey = null; _abComponent = null;
                return true;
            }
        }

        /// <summary>HOST: replay the absorb feed for a client's batched body hits. Rebuilds a
        /// <c>FullProjectileDescription</c> from the reported identity (velocity = magnitude-only up-vector: the
        /// native volley/sky re-aim it and only clamp its length) and invokes the native private
        /// <c>AbsorbPlayerBullet</c> — TreeShake + queue growth + the existing TerrorAbsorb/TerrorVolley/TerrorSky
        /// mirrors all follow natively.</summary>
        public static bool HostAbsorbFeed(object helper, string role, float damage, int dtype, out string detail)
        {
            detail = "";
            try
            {
                if (!(helper is Component hc) || hc == null || !EnsureResolved(helper)) { detail = "unresolved"; return false; }
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string[] p = role.Split(':');
                if (p.Length < 7) { detail = "malformed role"; return false; }
                int count = int.Parse(p[1], inv);
                float speed = float.Parse(p[2], inv);
                int type = int.Parse(p[3], inv), caliber = int.Parse(p[4], inv), effect = int.Parse(p[5], inv), vfx = int.Parse(p[6], inv);
                if (count <= 0) { detail = "count<=0"; return false; }

                // Native collection gates (OnHitReceivedFrom): fight running, not mid-volley, queue below the cap.
                if (!(BossReflect.GetMember(helper, "fightStarted") is bool fs && fs)) { detail = "fight not started"; return false; }
                if (BossReflect.GetMember(helper, "isDoingAoe") is bool aoe && aoe) { detail = "mid-volley (isDoingAoe)"; return false; }
                int cap = _fMaxCollect?.GetValue(helper) is int mc && mc > 0 ? mc : 150;
                int have = _fAbsorbQueue?.GetValue(helper) is System.Collections.ICollection q ? q.Count : 0;
                if (have >= cap) { detail = $"queue full {have}/{cap}"; return false; }
                if (_mAbsorbBullet == null) { detail = "AbsorbPlayerBullet not found"; return false; }

                float3 o = new float3(hc.transform.position.x, hc.transform.position.y, hc.transform.position.z);
                ProjectileRay ray = new ProjectileRay(o, (ProjectileTypes)type);
                ray.velocity  = new float3(0f, Mathf.Max(1f, speed), 0f);
                ray.radius    = 0.05f;
                ray.timeScale = 1f;
                ray.lifeTime  = 20f;
                ray.effect    = (ProjectileEffect)effect;
                ray.vfxAsset  = (VFX_Persistent)vfx;
                ray.drawDefaultBullet = true;
                ray.playImpactSounds  = true;
                ray.createBulletHoles = true;
                ray.shotOrLastBounceFrom = o;
                ray.barrelPosition       = o;
                // Laser calibers render as a BEAM, not the default bullet quad — without this the re-fired laser
                // pellets are invisible on every end (Log367).
                PlayerWeaponFireManager.ApplyNativeCaliberPresentation(ref ray, caliber);
                // Unlike the WS visual replicas this desc keeps its damage: the tree RE-FIRES it at players, and those
                // shots are host-authoritative real projectiles (ghost proxies forward the damage) like any host bullet.
                ray.damageComps.Add(new ProjectileDamage(damage, (DamageTypes)dtype));
                var desc = new FullProjectileDescription
                {
                    ray = ray,
                    data = new ProjectileData { damageType = (DamageTypes)dtype, caliber = (CaliberTypes)caliber, isPlayer = true },
                    visuals = null, isRaygun = false,
                };

                // TryAbsorbBullets halves a non-damaging hit surplus: max(1, count*0.5). Body hits never damage (the
                // standing invulnerability rejects them), so the whole batch is surplus — same arithmetic here.
                int absorbs = Mathf.Max(1, (int)(count * 0.5f));
                object[] arg = { desc };
                for (int i = 0; i < absorbs; i++) _mAbsorbBullet.Invoke(helper, arg);
                detail = $"hits={count} absorbs={absorbs} dmg={damage:0.0} dtype={dtype} speed={speed:0.0} queue={have}->{have + absorbs * 3}";
                return true;
            }
            catch (Exception ex) { detail = $"{ex.GetType().Name}: {ex.Message}"; return false; }
        }

        // ================================================================ CLIENT: applies + per-frame visuals

        // Root-spin easing target per helper (one Terrorbaum per level in practice).
        private static Component _rootHelper;
        private static float _rootTargetAngle;
        private static float _rootLastPacketAt;

        public static void ApplyRootAngle(object component, float angleY)
        {
            if (component is Component c && c != null) { _rootHelper = c; _rootTargetAngle = angleY; _rootLastPacketAt = Time.realtimeSinceStartup; }
        }

        public static void ApplyRootStop(object component)
        {
            _rootHelper = null;
            if (BossReflect.TryInvokeArg(component, "StopRoot", false, out string d))
            { if (LogOn) Plugin.Log.Info($"[BossMech] client StopRoot mirrored ({d})"); }
        }

        public static void ApplyLash(object component, int idx)
        {
            try
            {
                if (component == null || !EnsureResolved(component)) return;
                if (!(_fSmallRoots?.GetValue(component) is Animator[] roots) || roots.Length == 0) return;
                if (idx < 0 || idx >= roots.Length || roots[idx] == null) return;
                roots[idx].SetTrigger("Attack");
            }
            catch { }
        }

        /// <summary>CLIENT: mirror the absorb moment — the same native one-shot TreeShake VFX at the tree.</summary>
        public static void ApplyAbsorb(object component)
        {
            try
            {
                if (component == null || !EnsureResolved(component)) return;
                if (!(_fTreeShake?.GetValue(component) is Component shake) || shake == null) return;
                PerfectRandom.Sulfur.Core.StaticInstance<PerfectRandom.Sulfur.Core.VFXPlayManager>.Instance?
                    .PlayOneShotEffect(PerfectRandom.Sulfur.Core.VFX_OneShot.TreeShake, shake.transform.position);
            }
            catch { }
        }

        // CLIENT upward-volley replay state (one volley at a time — native volleys never overlap).
        private sealed class VolleyState
        {
            public Component Helper; public int Remaining; public float Delay, Speed, NextShotAt, EndAt;
            public int Type, Caliber, Effect, Vfx, DamageType;
        }
        private static VolleyState _volley;

        /// <summary>CLIENT: replay the upward volley — presentation on (casing particles + ShootingProjectiles pose +
        /// loop sound), then one zero-damage visual shot per cadence from the tree's bullet spawnpoint in the native
        /// 15° up-cone; presentation off when done. The real spikes coming DOWN are the separately-mirrored TerrorSky
        /// shots (host-authoritative damage).</summary>
        public static void ApplyVolley(object component, int count, float delay, float speed, int type, int caliber, int effect, int vfx, int damageType)
        {
            try
            {
                if (!(component is Component hc) || hc == null || !EnsureResolved(component) || count <= 0) return;
                float now = Time.realtimeSinceStartup;
                _volley = new VolleyState
                {
                    Helper = hc, Remaining = count, Delay = delay, Speed = speed, NextShotAt = now,
                    EndAt = now + count * delay + 1f,
                    Type = type, Caliber = caliber, Effect = effect, Vfx = vfx, DamageType = damageType,
                };
                SetVolleyPresentation(component, on: true);
            }
            catch { }
        }

        private static void SetVolleyPresentation(object helper, bool on)
        {
            try
            {
                float rate = on && _fCasingRate?.GetValue(helper) is float r ? r : 0f;
                // Casing particles via reflection (UnityEngine.ParticleSystemModule is not referenced — same as D2):
                // boxed EmissionModule writes through to the native system, MinMaxCurve has a float ctor.
                if (_fCasing?.GetValue(helper) is System.Collections.IEnumerable casings)
                    foreach (var ps in casings)
                    {
                        if (!(ps is UnityEngine.Object uo) || uo == null) continue;
                        try
                        {
                            var emProp = ps.GetType().GetProperty("emission", BF | BindingFlags.Public);
                            object em = emProp?.GetValue(ps);
                            var rateProp = em?.GetType().GetProperty("rateOverTime");
                            if (em == null || rateProp == null) continue;
                            object curve = Activator.CreateInstance(rateProp.PropertyType, rate);
                            rateProp.SetValue(em, curve);
                        }
                        catch { }
                    }
                if (BossReflect.GetMember(helper, "bossAnimator") is Animator anim && anim != null)
                    anim.SetBool("ShootingProjectiles", on);
                var snd = _fAoeLoopSound?.GetValue(helper);
                if (snd != null && helper is Component hc && hc != null)
                {
                    var m = snd.GetType().GetMethod(on ? "Play" : "Stop", BF, null, new[] { typeof(Transform) }, null);
                    m?.Invoke(snd, new object[] { hc.transform });
                }
            }
            catch { }
        }

        // Pooled preview markers awaiting release (flight time elapsed).
        private static readonly List<(Component marker, float releaseAt)> _markers = new List<(Component, float)>();

        /// <summary>CLIENT: one host sky shot → the landing preview marker (same pool prefab) + a zero-damage visual
        /// spike falling from cloud height onto it. Marker auto-released after the flight time (the native release is
        /// projectile-destroy-driven; a timer replica is visually equivalent).</summary>
        public static void ApplySkySpike(object component, Vector3 ground, float speed, int type, int caliber, int effect, int vfx, int damageType)
        {
            try
            {
                if (!(component is Component hc) || hc == null || !EnsureResolved(component)) return;
                float startY = _fStartHeight?.GetValue(component) is float sh ? sh : ground.y + 40f;
                Vector3 origin = new Vector3(ground.x, startY, ground.z);
                float fall = Mathf.Max(0.2f, (startY - ground.y) / Mathf.Max(1f, speed));

                PlayerWeaponFireManager.FireVisualStraight(origin, Vector3.down * speed, type, caliber, effect, vfx, damageType);

                if (_fMarkerPrefab?.GetValue(component) is GameObject prefab && prefab != null)
                {
                    var pooled = AutoPoolGetInstance(prefab);
                    if (pooled != null)
                    {
                        pooled.transform.position = ground;
                        _markers.Add((pooled, Time.realtimeSinceStartup + fall + 0.75f));
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossMech] ApplySkySpike failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>CLIENT per-frame (manager Tick): ease the vines toward the streamed angle + release due markers.</summary>
        public static void TickClient()
        {
            try
            {
                if (_rootHelper != null && _rootHelper)
                {
                    // Packets stop when the phase ends (host stream is isDoingRoot-gated) — stop chasing after 2 s idle.
                    if (Time.realtimeSinceStartup - _rootLastPacketAt > 2f) _rootHelper = null;
                    else if (EnsureResolved(_rootHelper) && _fRootPoints?.GetValue(_rootHelper) is GameObject rp && rp != null)
                    {
                        var e = rp.transform.localEulerAngles;
                        e.y = Mathf.LerpAngle(e.y, _rootTargetAngle, Mathf.Clamp01(12f * Time.deltaTime));
                        rp.transform.localEulerAngles = e;
                    }
                }
                for (int i = _markers.Count - 1; i >= 0; i--)
                {
                    if (Time.realtimeSinceStartup < _markers[i].releaseAt) continue;
                    var m = _markers[i].marker; _markers.RemoveAt(i);
                    if (m != null && m) AutoPoolRelease(m);
                }

                // Upward-volley replay: fire the scheduled visual shots, then drop the presentation.
                var v = _volley;
                if (v != null)
                {
                    float now = Time.realtimeSinceStartup;
                    if (v.Helper == null || !v.Helper || now >= v.EndAt)
                    {
                        if (v.Helper != null && v.Helper) SetVolleyPresentation(v.Helper, on: false);
                        _volley = null;
                    }
                    else if (v.Remaining > 0 && now >= v.NextShotAt)
                    {
                        v.NextShotAt = now + v.Delay;
                        v.Remaining--;
                        if (_fBulletSpawn?.GetValue(v.Helper) is Transform spawn && spawn != null)
                        {
                            // Native SpawnProjectiles: origin = bulletSpawnpoint + local x jitter, dir = 15° cone around up.
                            Vector3 origin = spawn.TransformPoint(new Vector3(UnityEngine.Random.Range(0f, 0.5f), 0f, 0f));
                            Vector3 dir = PerfectRandom.Sulfur.Core.Utilities.Helpers.GetRandomDirectionInCode(15f, Vector3.up);
                            PlayerWeaponFireManager.FireVisualStraight(origin, dir * v.Speed, v.Type, v.Caliber, v.Effect, v.Vfx, v.DamageType);
                        }
                        if (v.Remaining <= 0) v.EndAt = now + 0.4f; // shots done — brief tail then presentation off
                    }
                }
            }
            catch { }
        }

        public static void OnLevelChanged()
        {
            _rootHelper = null; _lastSentAngle = float.MinValue; _wasDoingRoot = false;
            _markers.Clear(); // pooled objects die with the scene; no release needed
            _volley = null; _lastAbsorbSentAt = 0f;
            lock (_absorbLock) { _abCount = 0; _abKey = null; _abComponent = null; }
        }

        // ---------------------------------------------------------------- AutoPool (reflection, same pattern as D2)
        private static bool _poolResolved;
        private static Type _autoPoolType;
        private static MethodInfo _poolGet, _poolRelease;
        private static UnityEngine.Object _autoPool;

        private static Component AutoPoolGetInstance(GameObject prefab)
        {
            try
            {
                if (!_poolResolved)
                {
                    _poolResolved = true;
                    _autoPoolType = HarmonyLib.AccessTools.TypeByName("PerfectRandom.Sulfur.Core.AutoPool");
                    if (_autoPoolType != null)
                    {
                        foreach (var m in _autoPoolType.GetMethods(BF))
                        {
                            var ps = m.GetParameters();
                            if (m.Name == "GetInstance" && !m.IsGenericMethodDefinition && ps.Length == 1 && ps[0].ParameterType == typeof(GameObject)) _poolGet = m;
                            if (m.Name == "ReleaseInstance" && ps.Length == 1 && typeof(Component).IsAssignableFrom(ps[0].ParameterType)) _poolRelease = m;
                        }
                    }
                }
                if (_autoPoolType == null || _poolGet == null) return null;
                if (_autoPool == null || !_autoPool) _autoPool = UnityEngine.Object.FindAnyObjectByType(_autoPoolType);
                if (_autoPool == null) return null;
                return _poolGet.Invoke(_autoPool, new object[] { prefab }) as Component;
            }
            catch { return null; }
        }

        private static void AutoPoolRelease(Component pooled)
        {
            try
            {
                if (_poolRelease == null || _autoPool == null || !_autoPool) { if (pooled != null) pooled.gameObject.SetActive(false); return; }
                _poolRelease.Invoke(_autoPool, new object[] { pooled });
            }
            catch { try { if (pooled != null) pooled.gameObject.SetActive(false); } catch { } }
        }
    }
}
