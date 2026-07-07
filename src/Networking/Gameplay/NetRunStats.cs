namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>Per-player statistics for a single Run (leaving the safe hub until the next return to it).
    /// Owned exclusively by the Host (<see cref="NetRunStatsManager"/>) while a Run is in progress; clients only
    /// ever receive a finalized, read-only copy via the Host's broadcast.</summary>
    internal sealed class NetRunStats
    {
        public string PeerId { get; set; } = "";
        public string PlayerName { get; set; } = "";
        public int ShotsFired { get; set; }
        public int DamageDealt { get; set; }
        public int Kills { get; set; }
        public int TimesDowned { get; set; }
        public int Rescues { get; set; }
        public int DamageTaken { get; set; }
        public int DestructiblesDestroyed { get; set; }

        public NetRunStats Clone()
        {
            return new NetRunStats
            {
                PeerId = PeerId,
                PlayerName = PlayerName,
                ShotsFired = ShotsFired,
                DamageDealt = DamageDealt,
                Kills = Kills,
                TimesDowned = TimesDowned,
                Rescues = Rescues,
                DamageTaken = DamageTaken,
                DestructiblesDestroyed = DestructiblesDestroyed,
            };
        }

        public string ToCompactString()
        {
            return $"peer={PeerId} name={PlayerName} shots={ShotsFired} dmgDealt={DamageDealt} kills={Kills} " +
                   $"downed={TimesDowned} rescues={Rescues} dmgTaken={DamageTaken} broken={DestructiblesDestroyed}";
        }
    }
}
