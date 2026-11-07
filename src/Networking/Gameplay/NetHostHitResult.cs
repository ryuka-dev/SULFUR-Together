using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>HR-1: how the host settled a <see cref="NetClientHitRequest"/>.</summary>
    internal enum NetHitOutcome : byte
    {
        /// <summary>The game's ReceiveDamage took health off the unit (it returned true).</summary>
        Applied = 1,
        /// <summary>The host refused the request before reaching the game (scene, target, malformed or out-of-range
        /// input, rate budget).</summary>
        Rejected = 2,
        /// <summary>The target was alive and the game declined the damage (invulnerable unit or hitbox, block, parry,
        /// petrified vs ranged, protected NPC, fully mitigated).</summary>
        Invulnerable = 3,
        /// <summary>The target was already dead when the request arrived (a corpse hit always reports this).</summary>
        TargetDead = 4,
    }

    /// <summary>A further damage instance the game dealt to the SAME target while settling the request — e.g. the
    /// Crusader status's Holy damage, which the game deals from inside the outer ReceiveDamage.</summary>
    internal struct NetHostHitSecondary
    {
        public float Damage;          // the CreateDamageLog damage of that inner call
        public float HealthRemoved;
        public byte  DamageType;
        public bool  IsCritical;
        public bool  Killed;          // this inner call took the unit from alive to dead
    }

    /// <summary>
    /// HR-1: Host → the requesting client only. One per <see cref="NetClientHitRequest.RequestSeq"/>.
    /// Field meanings follow the game: <see cref="Damage"/> is the amount <c>Unit.CreateDamageLog</c> logged (after
    /// crit, hitbox multiplier and mitigation), <see cref="IncomingDamage"/> its pre-mitigation figure.
    /// </summary>
    internal sealed class NetHostHitResult
    {
        public int           RequestSeq;
        public NetHitOutcome Outcome;
        public float         Damage;
        public float         HealthRemoved;
        public float         IncomingDamage;
        public byte          DamageType;
        public bool          IsCritical;
        public ushort        ShapeId;
        /// <summary><c>Hitmesh.Data.GetShapeMultiplier()</c> of the part that was hit; 0 when the hit had no part.</summary>
        public float         HitMultiplier;
        public bool          WasAlive;
        /// <summary>Settling this request (its own damage or a secondary instance) took the unit from alive to dead.</summary>
        public bool          Killed;
        public List<NetHostHitSecondary>? Secondary;

        public const int MaxSecondary = 8;

        public string ToCompact()
            => $"seq={RequestSeq} outcome={Outcome} dmg={Damage:F1} hpRemoved={HealthRemoved:F1} incoming={IncomingDamage:F1} " +
               $"type={DamageType} crit={IsCritical} shape=0x{ShapeId:X4} mult={HitMultiplier:F2} wasAlive={WasAlive} killed={Killed} " +
               $"secondary={DescribeSecondary()}";

        private string DescribeSecondary()
        {
            if (Secondary == null || Secondary.Count == 0) return "0";
            var sb = new System.Text.StringBuilder();
            sb.Append(Secondary.Count).Append('[');
            for (int i = 0; i < Secondary.Count; i++)
            {
                var x = Secondary[i];
                if (i > 0) sb.Append(' ');
                sb.Append($"{x.Damage:F1}/{x.HealthRemoved:F1}t{x.DamageType}{(x.IsCritical ? "c" : "")}{(x.Killed ? "k" : "")}");
            }
            return sb.Append(']').ToString();
        }
    }

    internal static class NetHostHitResultCodec
    {
        public const byte Version = 1;

        public static void Write(NetDataWriter w, NetHostHitResult m)
        {
            w.Put(Version);
            w.Put(m.RequestSeq);
            w.Put((byte)m.Outcome);
            w.Put(m.Damage);
            w.Put(m.HealthRemoved);
            w.Put(m.IncomingDamage);
            w.Put(m.DamageType);
            w.Put(m.IsCritical);
            w.Put(m.ShapeId);
            w.Put(m.HitMultiplier);
            w.Put(m.WasAlive);
            w.Put(m.Killed);
            int n = m.Secondary == null ? 0 : System.Math.Min(m.Secondary.Count, NetHostHitResult.MaxSecondary);
            w.Put((byte)n);
            for (int i = 0; i < n; i++)
            {
                var s = m.Secondary![i];
                w.Put(s.Damage);
                w.Put(s.HealthRemoved);
                w.Put(s.DamageType);
                w.Put(s.IsCritical);
                w.Put(s.Killed);
            }
        }

        public static bool TryRead(NetDataReader r, out NetHostHitResult result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != Version) return false;
                var m = new NetHostHitResult
                {
                    RequestSeq     = r.GetInt(),
                    Outcome        = (NetHitOutcome)r.GetByte(),
                    Damage         = r.GetFloat(),
                    HealthRemoved  = r.GetFloat(),
                    IncomingDamage = r.GetFloat(),
                    DamageType     = r.GetByte(),
                    IsCritical     = r.GetBool(),
                    ShapeId        = r.GetUShort(),
                    HitMultiplier  = r.GetFloat(),
                    WasAlive       = r.GetBool(),
                    Killed         = r.GetBool(),
                };
                if (m.Outcome < NetHitOutcome.Applied || m.Outcome > NetHitOutcome.TargetDead) return false;
                int n = r.GetByte();
                if (n > NetHostHitResult.MaxSecondary) return false;
                if (n > 0)
                {
                    m.Secondary = new List<NetHostHitSecondary>(n);
                    for (int i = 0; i < n; i++)
                    {
                        m.Secondary.Add(new NetHostHitSecondary
                        {
                            Damage        = r.GetFloat(),
                            HealthRemoved = r.GetFloat(),
                            DamageType    = r.GetByte(),
                            IsCritical    = r.GetBool(),
                            Killed        = r.GetBool(),
                        });
                    }
                }
                result = m;
                return true;
            }
            catch { return false; }
        }
    }
}
