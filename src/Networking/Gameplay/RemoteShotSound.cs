using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Items;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Weapons;
using Sonity;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>Where a shot sound came from, as the game itself stores it. The firing end names the source; every
    /// end resolves it from its own copy of the same asset, so no sound asset ever has to be identified by name.</summary>
    internal enum ShotSoundSlot : byte
    {
        None = 0,
        WeaponShot = 1,          // Weapon.soundShot          (SourceId = weapon ItemId)
        WeaponShotSilenced = 2,  // Weapon.soundShotSilenced  (SourceId = weapon ItemId)
        WeaponShotMutation = 3,  // Weapon.soundEventShotMutation (SourceId = weapon ItemId)
        CaliberShot = 4,         // CaliberType.soundShot         (SourceId = CaliberTypes)
        CaliberShotSilenced = 5, // CaliberType.soundShotSilenced (SourceId = CaliberTypes)
        EffectShot = 6,          // ProjectileEffectDefinition.soundEventShot (SourceId = ProjectileEffect)

        // WS-SND-3: handling sounds a weapon's animation plays through PlayMiscClip (throw, reload steps), all
        // weapon-prefab fields (SourceId = weapon ItemId).
        WeaponHandlingMagIn = 7,         // Weapon.soundEventHandlingMagIn
        WeaponHandlingMagOut = 8,        // Weapon.soundEventHandlingMagOut
        WeaponHandlingSlideBack = 9,     // Weapon.soundEventHandlingSlideBack
        WeaponHandlingSlideForward = 10, // Weapon.soundEventHandlingSlideForward
        WeaponHandlingExtraA = 11,       // Weapon.soundEventHandlingExtraA
        WeaponHandlingExtraB = 12,       // Weapon.soundEventHandlingExtraB
        WeaponHandlingReload = 13,       // Weapon.soundHandlingReloads[Index]
    }

    /// <summary>The one Sonity parameter the game passes with a shot sound.</summary>
    internal enum ShotSoundParam : byte
    {
        None = 0,
        VolumeDecibel = 1, // SoundParameterVolumeDecibel (silenced effect shot)
        Intensity = 2,     // SoundParameterIntensity (ammo-used intensity)
    }

    internal struct NetShotSound
    {
        public ShotSoundSlot Slot;
        public int SourceId;
        public ShotSoundParam ParamKind;
        public float Param;
        public byte Index;      // WeaponHandlingReload only: the element of soundHandlingReloads
    }

    /// <summary>
    /// WS-SND-1: the shot sound of a remote player's gun.
    /// <para>The bullet replay (5.6-WS) only ever spawned projectiles, so nobody heard a teammate's gunfire: vanilla plays
    /// the shot inside the firing weapon's own <c>Weapon.SoundShot</c>, which no replay runs. That method picks one to
    /// three sounds from the weapon's live state (silencer, modded caliber, the oil's projectile effect, ammo-used
    /// intensity, the effect-sound-only attribute) and hands each to <c>PlayShootSoundEvent</c> /
    /// <c>PlayShootSoundEventIntensity</c>.</para>
    /// <para>Capture: while the local player's own <c>Weapon.Shoot</c> runs, prefixes on those two methods record what
    /// vanilla decided to play — the choice stays the game's, nothing here re-implements it. Each recorded sound is
    /// matched by reference against the six places the game stores shot sounds (<see cref="ShotSoundSlot"/>); a sound
    /// from anywhere else is left out rather than guessed.</para>
    /// <para>Playback: the receiver resolves the slot from its own assets and plays it the way vanilla plays a shot
    /// that is not the listener's own (its Npc branch): <c>SoundEvent.Play(transform, parameter)</c> at the shooter,
    /// spatialized, without the <c>Force2D</c> parameter the local player's own gun uses. The transform is the remote
    /// player's visual proxy, so the sound follows them.</para>
    /// </summary>
    internal static class RemoteShotSound
    {
        internal const int MaxSoundsPerShot = 4;

        private static FieldInfo? _fSoundShot;
        private static FieldInfo? _fSoundShotSilenced;
        private static FieldInfo? _fSoundShotMutation;
        private static FieldInfo? _fEffectOnProjectile;
        private static bool _supported;

        /// <summary>Resolves the weapon's private shot-sound fields once, at patch time. Returns false (and logs) when
        /// this game build no longer has them; capture and playback then stay off.</summary>
        internal static bool Resolve()
        {
            _fSoundShot          = AccessTools.Field(typeof(Weapon), "soundShot");
            _fSoundShotSilenced  = AccessTools.Field(typeof(Weapon), "soundShotSilenced");
            _fSoundShotMutation  = AccessTools.Field(typeof(Weapon), "soundEventShotMutation");
            _fEffectOnProjectile = AccessTools.Field(typeof(Weapon), "effectOnProjectile");
            _supported = _fSoundShot != null && _fSoundShotSilenced != null && _fSoundShotMutation != null
                         && _fEffectOnProjectile != null
                         && typeof(SoundEvent).IsAssignableFrom(_fSoundShot.FieldType)
                         && _fEffectOnProjectile.FieldType == typeof(ProjectileEffect);
            if (!_supported)
                Plugin.Log.Warn("[WeaponFire] remote shot sound not supported on this game build — Weapon.soundShot/"
                                + "soundShotSilenced/soundEventShotMutation/effectOnProjectile not found; teammates' gunfire stays silent");

            // WS-SND-3: the handling fields PlayMiscClip picks from. Optional on their own: without them, throw and
            // reload sounds stay local while shots still work.
            for (int i = 0; i < HandlingFieldNames.Length; i++)
                _fHandling[i] = AccessTools.Field(typeof(Weapon), HandlingFieldNames[i]);
            _fHandlingReloads = AccessTools.Field(typeof(Weapon), "soundHandlingReloads");
            _handlingSupported = _supported && _fHandlingReloads != null && _fHandlingReloads.FieldType == typeof(SoundEvent[]);
            foreach (var f in _fHandling)
                if (f == null || !typeof(SoundEvent).IsAssignableFrom(f.FieldType)) _handlingSupported = false;
            if (_supported && !_handlingSupported)
                Plugin.Log.Warn("[WeaponFire] remote handling sounds not supported on this game build — Weapon.soundEventHandling*/"
                                + "soundHandlingReloads not found; teammates' throw and reload sounds stay silent");
            return _supported;
        }

        // Same order as PlayMiscClip's switch (clip 0..5) and as ShotSoundSlot.WeaponHandlingMagIn..ExtraB.
        private static readonly string[] HandlingFieldNames =
        {
            "soundEventHandlingMagIn", "soundEventHandlingMagOut", "soundEventHandlingSlideBack",
            "soundEventHandlingSlideForward", "soundEventHandlingExtraA", "soundEventHandlingExtraB",
        };
        private static readonly FieldInfo?[] _fHandling = new FieldInfo?[6];
        private static FieldInfo? _fHandlingReloads;
        private static bool _handlingSupported;

        // ----------------------------------------------------------------- WS-SND-2: beam loop

        // A beam weapon has no per-shot report; while it fires, vanilla keeps one looping sound
        // (Weapon.StartBeamLoopSound / StopBeamLoopSound, chosen by GetBeamLoopSound from the SuperSoaker, IceBeam or
        // Beam projectile effect's soundEventShootLoop). The firing end sends the two transitions, the receiver runs the
        // same loop at the shooter's proxy and stops it on the stop event, when the proxy goes away, on the peer leaving
        // or on a session reset.
        private static FieldInfo? _fBeamLoopSound;
        private static FieldInfo? _fBeamLoopPlaying;
        private static readonly ProjectileEffect[] BeamLoopEffects =
            { ProjectileEffect.SuperSoaker, ProjectileEffect.IceBeam, ProjectileEffect.Beam };

        internal const byte BeamLoopNone = 0, BeamLoopStart = 1, BeamLoopStop = 2;

        /// <summary>Resolves the beam loop fields once at patch time; false (logged) when this build lacks them.</summary>
        internal static bool ResolveBeamLoop()
        {
            _fBeamLoopSound = AccessTools.Field(typeof(Weapon), "beamLoopSound");
            _fBeamLoopPlaying = AccessTools.Field(typeof(Weapon), "beamLoopPlaying");
            bool ok = _fBeamLoopSound != null && _fBeamLoopPlaying != null
                      && typeof(SoundEvent).IsAssignableFrom(_fBeamLoopSound.FieldType) && _fBeamLoopPlaying.FieldType == typeof(bool);
            if (!ok)
                Plugin.Log.Warn("[WeaponFire] remote beam loop sound not supported on this game build — Weapon.beamLoopSound/beamLoopPlaying not found");
            return ok;
        }

        /// <summary>Prefix of Start/StopBeamLoopSound: the loop state before the call (both methods guard on it).</summary>
        internal static bool ReadBeamLoopPlaying(Weapon weapon)
        {
            try { return _fBeamLoopPlaying != null && _fBeamLoopPlaying.GetValue(weapon) is bool b && b; }
            catch { return false; }
        }

        /// <summary>Postfix of Start/StopBeamLoopSound on the local player's gun: report an actual start or stop.
        /// Returns the effect id of the loop for a start (0 when it comes from no known effect, which is not sent).</summary>
        internal static bool TryReadBeamLoopTransition(Weapon weapon, bool wasPlaying, out byte transition, out int effectId)
        {
            transition = BeamLoopNone;
            effectId = 0;
            if (_fBeamLoopPlaying == null || weapon == null) return false;
            bool playing = ReadBeamLoopPlaying(weapon);
            if (playing == wasPlaying) return false;
            if (!playing) { transition = BeamLoopStop; return true; }

            var ev = _fBeamLoopSound?.GetValue(weapon) as SoundEvent;
            if (ev == null) return false;
            foreach (var fx in BeamLoopEffects)
            {
                ProjectileEffectDefinition def = fx.GetAsset();
                if (def != null && ReferenceEquals(def.soundEventShootLoop, ev))
                {
                    transition = BeamLoopStart;
                    effectId = (int)fx;
                    return true;
                }
            }
            if (_unmatchedLogs++ < 3)
                NetLogger.Warn($"[PlayerWeaponFire] beam loop sound '{ev.name}' of {weapon.weaponDefinition?.name} comes from no known beam effect — not sent");
            return false;
        }

        private struct ActiveLoop
        {
            public SoundEvent Event;
            public Transform At;
        }

        private static readonly Dictionary<string, ActiveLoop> _loops = new Dictionary<string, ActiveLoop>();
        private static readonly List<string> _loopScratch = new List<string>();

        /// <summary>Receiver: start or stop a peer's beam loop. Input comes off the wire: only the three beam effects
        /// are accepted.</summary>
        internal static void ApplyBeamLoop(string peerId, byte transition, int effectId, Transform? at)
        {
            if (string.IsNullOrEmpty(peerId) || transition == BeamLoopNone) return;
            StopLoop(peerId);
            if (transition != BeamLoopStart || at == null) return;

            ProjectileEffect fx = ProjectileEffect.None;
            foreach (var e in BeamLoopEffects) if ((int)e == effectId) fx = e;
            if (fx == ProjectileEffect.None) return;
            try
            {
                ProjectileEffectDefinition def = fx.GetAsset();
                SoundEvent? ev = def != null ? def.soundEventShootLoop : null;
                if (ev == null) return;
                ev.Play(at);
                _loops[peerId] = new ActiveLoop { Event = ev, At = at };
                if (Plugin.Cfg.LogPlayerWeaponSync.Value)
                    NetLogger.Info($"[PlayerWeaponFire] beam loop start peer={peerId} effect={fx} sound={ev.name}");
            }
            catch { }
        }

        /// <summary>Per frame: a loop whose shooter is no longer shown here is stopped.</summary>
        internal static void TickLoops()
        {
            if (_loops.Count == 0) return;
            _loopScratch.Clear();
            foreach (var kv in _loops)
                if (kv.Value.At == null || !kv.Value.At.gameObject.activeInHierarchy) _loopScratch.Add(kv.Key);
            foreach (var peer in _loopScratch) StopLoop(peer);
        }

        internal static void RemovePeer(string peerId) => StopLoop(peerId);

        internal static void ResetLoops()
        {
            _loopScratch.Clear();
            _loopScratch.AddRange(_loops.Keys);
            foreach (var peer in _loopScratch) StopLoop(peer);
        }

        private static void StopLoop(string peerId)
        {
            if (!_loops.TryGetValue(peerId, out var loop)) return;
            _loops.Remove(peerId);
            try { if (loop.Event != null && loop.At != null) loop.Event.Stop(loop.At); } catch { }
            if (Plugin.Cfg.LogPlayerWeaponSync.Value)
                NetLogger.Info($"[PlayerWeaponFire] beam loop stop peer={peerId}");
        }

        // ----------------------------------------------------------------- capture (firing peer)

        private static Weapon? _capturing;
        private static readonly List<NetShotSound> _pending = new List<NetShotSound>(MaxSoundsPerShot);
        private static int _unmatchedLogs;

        /// <summary>Prefix of a recording window — Weapon.Shoot, and (WS-SND-3) the animation-event entries
        /// Weapon.ShootSound (a melee swing) and Weapon.PlayMiscClip (throw and reload steps): start recording for the
        /// local player's own weapon. None of these calls another, so windows never nest.</summary>
        internal static void BeginWindow(Weapon weapon)
        {
            _pending.Clear();
            _capturing = null;
            if (!_supported || weapon == null) return;
            if (!Plugin.Cfg.EnablePlayerWeaponSync.Value || !NetGameplaySyncBridge.IsSessionActive) return;
            var owner = weapon.SourceUnit;
            if (owner == null || !owner.isPlayer || !NetPlayerLifeManager.IsLocalPlayerUnit(owner)) return;
            _capturing = weapon;
        }

        /// <summary>Postfix of a recording window: hand over what was played during it and stop recording.</summary>
        internal static NetShotSound[] EndWindow(Weapon weapon)
        {
            NetShotSound[] result = (_capturing != null && ReferenceEquals(_capturing, weapon) && _pending.Count > 0)
                ? _pending.ToArray()
                : Array.Empty<NetShotSound>();
            _pending.Clear();
            _capturing = null;
            return result;
        }

        /// <summary>Prefix of Weapon.PlayShootSoundEvent(SoundEvent, bool isEffectShootSound, SoundParameterVolumeDecibel).</summary>
        internal static void OnPlay(Weapon weapon, SoundEvent soundEvent, bool isEffectShootSound, SoundParameterVolumeDecibel? volumeDb)
        {
            if (!IsCapturing(weapon) || soundEvent == null) return;
            if (SkippedByEffectSoundOnly(weapon, isEffectShootSound)) return; // vanilla returns before playing
            if (volumeDb != null) Record(weapon, soundEvent, ShotSoundParam.VolumeDecibel, volumeDb.VolumeDecibel);
            else Record(weapon, soundEvent, ShotSoundParam.None, 0f);
        }

        /// <summary>Prefix of Weapon.PlayShootSoundEventIntensity(SoundEvent, bool isEffectShootSound, float intensityValue).</summary>
        internal static void OnPlayIntensity(Weapon weapon, SoundEvent soundEvent, bool isEffectShootSound, float intensity)
        {
            if (!IsCapturing(weapon) || soundEvent == null) return;
            if (SkippedByEffectSoundOnly(weapon, isEffectShootSound)) return;
            Record(weapon, soundEvent, ShotSoundParam.Intensity, intensity);
        }

        /// <summary>WS-SND-3: prefix of Weapon.PlaySoundEvent2DIfPlayer(SoundEvent) — what PlayMiscClip plays. Its
        /// direct callers (aim, equip, click, reload start) run outside any window and stay local.</summary>
        internal static void OnPlay2D(Weapon weapon, SoundEvent soundEvent)
        {
            if (!_handlingSupported || !IsCapturing(weapon) || soundEvent == null) return;
            var gm = GameManager.Instance;
            if (gm != null && gm.gameState == GameState.Cinematic) return; // vanilla returns before playing
            Record(weapon, soundEvent, ShotSoundParam.None, 0f);
        }

        private static bool IsCapturing(Weapon weapon)
            => _capturing != null && ReferenceEquals(_capturing, weapon) && _pending.Count < MaxSoundsPerShot;

        // Mirrors the guard both vanilla methods open with: `if (HasPlayEffectShootSound && !isEffectShootSound) return;`
        // (HasPlayEffectShootSound = ItemStats.GetAttribute(ShootEffectSoundOnly) > 0).
        private static bool SkippedByEffectSoundOnly(Weapon weapon, bool isEffectShootSound)
        {
            if (isEffectShootSound) return false;
            var stats = weapon.ItemStats;
            return stats != null && stats.GetAttribute(ItemAttributes.ShootEffectSoundOnly) > 0f;
        }

        private static void Record(Weapon weapon, SoundEvent soundEvent, ShotSoundParam kind, float param)
        {
            try
            {
                if (!TryClassify(weapon, soundEvent, out var slot, out int sourceId, out byte index))
                {
                    if (_unmatchedLogs++ < 3)
                        NetLogger.Warn($"[PlayerWeaponFire] shot sound '{soundEvent.name}' of {weapon.weaponDefinition?.name} comes from no known shot-sound slot — not sent");
                    return;
                }
                _pending.Add(new NetShotSound { Slot = slot, SourceId = sourceId, ParamKind = kind, Param = param, Index = index });
            }
            catch { /* never disturb the local shot */ }
        }

        private static bool TryClassify(Weapon weapon, SoundEvent ev, out ShotSoundSlot slot, out int sourceId, out byte index)
        {
            slot = ShotSoundSlot.None;
            sourceId = 0;
            index = 0;
            var def = weapon.weaponDefinition;
            if (def != null)
            {
                int id = def.id.value;
                if (ReferenceEquals(ev, _fSoundShot!.GetValue(weapon)))         { slot = ShotSoundSlot.WeaponShot; sourceId = id; return true; }
                if (ReferenceEquals(ev, _fSoundShotSilenced!.GetValue(weapon))) { slot = ShotSoundSlot.WeaponShotSilenced; sourceId = id; return true; }
                if (ReferenceEquals(ev, _fSoundShotMutation!.GetValue(weapon))) { slot = ShotSoundSlot.WeaponShotMutation; sourceId = id; return true; }
                if (_handlingSupported)
                {
                    for (int i = 0; i < _fHandling.Length; i++)
                        if (ReferenceEquals(ev, _fHandling[i]!.GetValue(weapon))) { slot = (ShotSoundSlot)((int)ShotSoundSlot.WeaponHandlingMagIn + i); sourceId = id; return true; }
                    if (_fHandlingReloads!.GetValue(weapon) is SoundEvent[] reloads)
                        for (int i = 0; i < reloads.Length && i <= byte.MaxValue; i++)
                            if (ReferenceEquals(ev, reloads[i])) { slot = ShotSoundSlot.WeaponHandlingReload; sourceId = id; index = (byte)i; return true; }
                }
            }

            CaliberType caliber = weapon.Caliber.GetAsset();
            if (caliber != null)
            {
                if (ReferenceEquals(ev, caliber.soundShot))         { slot = ShotSoundSlot.CaliberShot; sourceId = (int)weapon.Caliber; return true; }
                if (ReferenceEquals(ev, caliber.soundShotSilenced)) { slot = ShotSoundSlot.CaliberShotSilenced; sourceId = (int)weapon.Caliber; return true; }
            }

            var effect = (ProjectileEffect)_fEffectOnProjectile!.GetValue(weapon);
            if (effect != ProjectileEffect.None)
            {
                ProjectileEffectDefinition fx = effect.GetAsset();
                if (fx != null && ReferenceEquals(ev, fx.soundEventShot)) { slot = ShotSoundSlot.EffectShot; sourceId = (int)effect; return true; }
            }
            return false;
        }

        // ----------------------------------------------------------------- playback (receiving peer)

        // Weapon-prefab sounds, resolved once per (slot, weapon ItemId). Caliber and effect sounds need no cache: the
        // game's own GetAsset is an array index. An id from the wire that is out of range throws there and is caught
        // per entry.
        private static readonly Dictionary<long, SoundEvent> _weaponSoundCache = new Dictionary<long, SoundEvent>();

        /// <summary>Plays the shot sounds of one remote trigger pull at <paramref name="at"/>. Input comes off the wire:
        /// unknown slots, ids this end cannot resolve and out-of-range parameters are skipped one by one. Returns how
        /// many were played.</summary>
        internal static int Play(NetShotSound[] sounds, Transform at)
        {
            if (!_supported || sounds == null || sounds.Length == 0 || at == null) return 0;
            int n = Math.Min(sounds.Length, MaxSoundsPerShot);
            int played = 0;
            for (int i = 0; i < n; i++)
            {
                try
                {
                    var s = sounds[i];
                    SoundEvent? ev = ResolveSound(s.Slot, s.SourceId, s.Index);
                    if (ev == null) continue;
                    switch (s.ParamKind)
                    {
                        case ShotSoundParam.VolumeDecibel when !float.IsNaN(s.Param) && s.Param >= -80f && s.Param <= 24f:
                            ev.Play(at, new SoundParameterVolumeDecibel(s.Param));
                            played++;
                            break;
                        case ShotSoundParam.Intensity when !float.IsNaN(s.Param):
                            ev.Play(at, new SoundParameterIntensity(Mathf.Clamp01(s.Param)));
                            played++;
                            break;
                        case ShotSoundParam.None:
                            ev.Play(at);
                            played++;
                            break;
                    }
                }
                catch { /* one bad entry must not cost the others */ }
            }
            return played;
        }

        private static SoundEvent? ResolveSound(ShotSoundSlot slot, int sourceId, byte index)
        {
            switch (slot)
            {
                case ShotSoundSlot.WeaponShot:
                case ShotSoundSlot.WeaponShotSilenced:
                case ShotSoundSlot.WeaponShotMutation:
                    return ResolveWeaponSound(slot, sourceId, 0);
                case ShotSoundSlot.WeaponHandlingMagIn:
                case ShotSoundSlot.WeaponHandlingMagOut:
                case ShotSoundSlot.WeaponHandlingSlideBack:
                case ShotSoundSlot.WeaponHandlingSlideForward:
                case ShotSoundSlot.WeaponHandlingExtraA:
                case ShotSoundSlot.WeaponHandlingExtraB:
                case ShotSoundSlot.WeaponHandlingReload:
                    return _handlingSupported ? ResolveWeaponSound(slot, sourceId, index) : null;
                case ShotSoundSlot.CaliberShot:
                case ShotSoundSlot.CaliberShotSilenced:
                {
                    if (sourceId < 0 || sourceId > byte.MaxValue) return null;
                    CaliberType caliber = ((CaliberTypes)sourceId).GetAsset();
                    if (caliber == null) return null;
                    return slot == ShotSoundSlot.CaliberShot ? caliber.soundShot : caliber.soundShotSilenced;
                }
                case ShotSoundSlot.EffectShot:
                {
                    if (sourceId <= 0 || sourceId > ushort.MaxValue) return null;
                    ProjectileEffectDefinition fx = ((ProjectileEffect)sourceId).GetAsset();
                    return fx != null ? fx.soundEventShot : null;
                }
                default:
                    return null;
            }
        }

        private static SoundEvent? ResolveWeaponSound(ShotSoundSlot slot, int itemId, byte index)
        {
            if (itemId <= 0 || itemId > ushort.MaxValue) return null;
            long key = ((long)slot << 24) | ((long)index << 16) | (long)itemId;
            if (_weaponSoundCache.TryGetValue(key, out var cached)) return cached;

            SoundEvent? ev = null;
            try
            {
                ItemDatabase? db = AsyncAssetLoading.Instance != null ? AsyncAssetLoading.Instance.itemDatabase : null;
                ItemDefinition? def = db != null ? db[new ItemId((ushort)itemId)] : null;
                GameObject? prefab = def != null ? def.prefab : null;
                Weapon? weapon = prefab != null ? prefab.GetComponentInChildren<Weapon>(true) : null;
                if (weapon != null)
                {
                    if (slot == ShotSoundSlot.WeaponHandlingReload)
                    {
                        if (_fHandlingReloads!.GetValue(weapon) is SoundEvent[] reloads && index < reloads.Length)
                            ev = reloads[index];
                    }
                    else
                    {
                        FieldInfo f = slot == ShotSoundSlot.WeaponShot ? _fSoundShot!
                                    : slot == ShotSoundSlot.WeaponShotSilenced ? _fSoundShotSilenced!
                                    : slot == ShotSoundSlot.WeaponShotMutation ? _fSoundShotMutation!
                                    : _fHandling[slot - ShotSoundSlot.WeaponHandlingMagIn]!;
                        ev = f.GetValue(weapon) as SoundEvent;
                    }
                }
            }
            catch { ev = null; }

            // Only cache a hit: the item database may not be loaded yet on the first shot after a level load.
            if (ev != null) _weaponSoundCache[key] = ev;
            return ev;
        }
    }
}
