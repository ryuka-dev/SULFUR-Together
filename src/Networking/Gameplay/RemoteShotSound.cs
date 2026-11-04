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
            return _supported;
        }

        // ----------------------------------------------------------------- capture (firing peer)

        private static Weapon? _capturing;
        private static readonly List<NetShotSound> _pending = new List<NetShotSound>(MaxSoundsPerShot);
        private static int _unmatchedLogs;

        /// <summary>Weapon.Shoot prefix: start recording for the local player's own gun.</summary>
        internal static void BeginShot(Weapon weapon)
        {
            _pending.Clear();
            _capturing = null;
            if (!_supported || weapon == null) return;
            if (!Plugin.Cfg.EnablePlayerWeaponSync.Value || !NetGameplaySyncBridge.IsSessionActive) return;
            var owner = weapon.SourceUnit;
            if (owner == null || !owner.isPlayer || !NetPlayerLifeManager.IsLocalPlayerUnit(owner)) return;
            _capturing = weapon;
        }

        /// <summary>Weapon.Shoot postfix: hand over what was played during this shot and stop recording.</summary>
        internal static NetShotSound[] EndShot(Weapon weapon)
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
                if (!TryClassify(weapon, soundEvent, out var slot, out int sourceId))
                {
                    if (_unmatchedLogs++ < 3)
                        NetLogger.Warn($"[PlayerWeaponFire] shot sound '{soundEvent.name}' of {weapon.weaponDefinition?.name} comes from no known shot-sound slot — not sent");
                    return;
                }
                _pending.Add(new NetShotSound { Slot = slot, SourceId = sourceId, ParamKind = kind, Param = param });
            }
            catch { /* never disturb the local shot */ }
        }

        private static bool TryClassify(Weapon weapon, SoundEvent ev, out ShotSoundSlot slot, out int sourceId)
        {
            slot = ShotSoundSlot.None;
            sourceId = 0;
            var def = weapon.weaponDefinition;
            if (def != null)
            {
                int id = def.id.value;
                if (ReferenceEquals(ev, _fSoundShot!.GetValue(weapon)))         { slot = ShotSoundSlot.WeaponShot; sourceId = id; return true; }
                if (ReferenceEquals(ev, _fSoundShotSilenced!.GetValue(weapon))) { slot = ShotSoundSlot.WeaponShotSilenced; sourceId = id; return true; }
                if (ReferenceEquals(ev, _fSoundShotMutation!.GetValue(weapon))) { slot = ShotSoundSlot.WeaponShotMutation; sourceId = id; return true; }
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
        private static readonly Dictionary<int, SoundEvent> _weaponSoundCache = new Dictionary<int, SoundEvent>();

        /// <summary>Plays the shot sounds of one remote trigger pull at <paramref name="at"/>. Input comes off the wire:
        /// unknown slots, ids this end cannot resolve and out-of-range parameters are skipped one by one.</summary>
        internal static void Play(NetShotSound[] sounds, Transform at)
        {
            if (!_supported || sounds == null || sounds.Length == 0 || at == null) return;
            int n = Math.Min(sounds.Length, MaxSoundsPerShot);
            for (int i = 0; i < n; i++)
            {
                try
                {
                    var s = sounds[i];
                    SoundEvent? ev = ResolveSound(s.Slot, s.SourceId);
                    if (ev == null) continue;
                    switch (s.ParamKind)
                    {
                        case ShotSoundParam.VolumeDecibel when !float.IsNaN(s.Param) && s.Param >= -80f && s.Param <= 24f:
                            ev.Play(at, new SoundParameterVolumeDecibel(s.Param));
                            break;
                        case ShotSoundParam.Intensity when !float.IsNaN(s.Param):
                            ev.Play(at, new SoundParameterIntensity(Mathf.Clamp01(s.Param)));
                            break;
                        case ShotSoundParam.None:
                            ev.Play(at);
                            break;
                    }
                }
                catch { /* one bad entry must not cost the others */ }
            }
        }

        private static SoundEvent? ResolveSound(ShotSoundSlot slot, int sourceId)
        {
            switch (slot)
            {
                case ShotSoundSlot.WeaponShot:
                case ShotSoundSlot.WeaponShotSilenced:
                case ShotSoundSlot.WeaponShotMutation:
                    return ResolveWeaponSound(slot, sourceId);
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

        private static SoundEvent? ResolveWeaponSound(ShotSoundSlot slot, int itemId)
        {
            if (itemId <= 0 || itemId > ushort.MaxValue) return null;
            int key = ((int)slot << 16) | itemId;
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
                    FieldInfo f = slot == ShotSoundSlot.WeaponShot ? _fSoundShot!
                                : slot == ShotSoundSlot.WeaponShotSilenced ? _fSoundShotSilenced!
                                : _fSoundShotMutation!;
                    ev = f.GetValue(weapon) as SoundEvent;
                }
            }
            catch { ev = null; }

            // Only cache a hit: the item database may not be loaded yet on the first shot after a level load.
            if (ev != null) _weaponSoundCache[key] = ev;
            return ev;
        }
    }
}
