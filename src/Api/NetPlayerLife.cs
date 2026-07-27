namespace SULFURTogether.Api
{
    /// <summary>
    /// Public, mod-neutral answer to one question: is this player still in the fight?
    ///
    /// In cooperative play a player who runs out of health is not immediately dead — they go down and wait to be
    /// rescued. SULFUR Together already treats such a player as no longer a target: its own patch for the cave
    /// boss's arm skips them, and a downed <i>remote</i> player is dropped from <c>GameManager.Players</c>
    /// altogether, so anything reading that roster stops seeing them without asking.
    ///
    /// The gap this closes is the <b>local</b> player, who stays in that roster while downed because they are the
    /// game's own player singleton. A mod that picks its own targets — a boss that aims at everyone in the room —
    /// has no way to tell that they are lying on the floor awaiting a rescue, and will keep attacking them.
    ///
    /// Threading: call from the Unity main thread, as with every other part of this API.
    /// </summary>
    public static class NetPlayerLife
    {
        /// <summary>Bumped on any breaking change to this API. A companion mod may gate on it.</summary>
        public const int ApiVersion = 1;

        /// <summary>
        /// True when <paramref name="playerUnit"/> is a player who is out of the fight — downed awaiting rescue,
        /// or dead. False for a player still fighting, for anything that is not a player, and whenever no
        /// cooperative session is running, so single-player behaviour is unchanged.
        /// </summary>
        /// <param name="playerUnit">A player's <c>Unit</c>, as found on <c>Player.playerUnit</c> or in
        /// <c>GameManager.Players</c>. Passed as <c>object</c> so a caller need not reference the game's
        /// assemblies to ask.</param>
        public static bool IsOutOfTheFight(object? playerUnit)
            => SULFURTogether.Networking.Gameplay.NetPlayerLifeManager.IsDownedLocalPlayerUnit(playerUnit);
    }
}
