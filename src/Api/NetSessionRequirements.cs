using System;
using SULFURTogether.Networking.Session;

namespace SULFURTogether.Api
{
    /// <summary>How a requirement is treated when two players' installs are compared.</summary>
    public enum SessionRequirementTier : byte
    {
        /// <summary>
        /// Content both ends need in the same state. A difference never refuses the connection: the joining player is
        /// shown it and chooses, the host is told, and ids inside the entry's claims are not exchanged between the two.
        /// </summary>
        Content = 1,
    }

    /// <summary>The kind of id a claim covers.</summary>
    public enum SessionIdKind : byte
    {
        /// <summary>The game's item ids (<c>ItemId.value</c>).</summary>
        Item = 1,
    }

    /// <summary>An inclusive range of ids a requirement's content occupies.</summary>
    public readonly struct SessionIdClaim
    {
        public SessionIdClaim(SessionIdKind kind, ushort min, ushort max)
        {
            Kind = kind;
            Min = min;
            Max = max;
        }

        public SessionIdKind Kind { get; }
        public ushort Min { get; }
        public ushort Max { get; }

        /// <summary>The item ids <paramref name="min"/> to <paramref name="max"/>, both included.</summary>
        public static SessionIdClaim Items(ushort min, ushort max) => new SessionIdClaim(SessionIdKind.Item, min, max);
    }

    /// <summary>
    /// Lets a mod declare content that both ends of a co-op session must have in the same state.
    ///
    /// <para>SULFUR Together knows nothing about the mods that register here. Each registers one requirement: an id, a
    /// name for the player, a callback returning a digest of its current state, and the id ranges its content occupies.
    /// At every handshake both ends exchange their requirements. Two entries with the same id match when their digests
    /// and their claims are equal; an entry only one end has does not match.</para>
    ///
    /// <para>When something does not match, the session still works, with two consequences. The joining player is shown
    /// the differences and decides whether to join. And an id inside the claims of a mismatched entry is withheld
    /// between those two machines: the item is absent on the other screen. It is never shown as a different item.</para>
    ///
    /// <para><b>When to call.</b> Once, from the plugin's <c>Awake</c>, on the Unity main thread. Declare
    /// <c>[BepInDependency("com.ryuka.sulfur.together", BepInDependency.DependencyFlags.SoftDependency)]</c> and keep
    /// the call in a class that is only loaded when SULFUR Together is installed. Registration is refused while a
    /// session is running.</para>
    ///
    /// <para><b>The digest</b> is 1 to 64 printable ASCII characters without spaces, and should cover everything that
    /// decides which id means which content (for a content library: its table of keys and numbers). It is requested at
    /// handshake time, not at registration, so content that is only known after the game's databases have loaded is
    /// fine. A callback that throws or returns anything else counts as a mismatch with every peer; throw on purpose
    /// when the content is in a state that must not be trusted.</para>
    ///
    /// <para><b>Claims</b> must lie above the game's own item ids. Ids a mod puts on the wire outside its claims are
    /// not protected by any of this.</para>
    ///
    /// <para>New parameters will only ever arrive as new overloads, so a mod compiled against this one keeps working.</para>
    /// </summary>
    public static class NetSessionRequirements
    {
        /// <summary>Read at run time (a property, not a constant), so a mod sees the installed version.</summary>
        public static int ApiVersion => 1;

        /// <summary>
        /// Register a requirement for the lifetime of the process.
        /// </summary>
        /// <param name="id">The mod's own id, normally its plugin GUID: 1 to 64 characters of <c>[A-Za-z0-9._-]</c>.
        /// Two ends compare the entries that carry the same id.</param>
        /// <param name="tier">How a difference is treated.</param>
        /// <param name="displayName">What the player sees in the list of differences, at most 48 characters. Shown
        /// as plain text: rich-text brackets, backslashes and braces are replaced, invisible characters and characters
        /// outside the Basic Multilingual Plane (emoji) are dropped.</param>
        /// <param name="digest">Returns the digest of the current state. Called on the main thread at each handshake.</param>
        /// <param name="claims">The id ranges the content occupies, at most 8.</param>
        /// <returns>False when the registration was refused; the reason is in the log (Unity's own log when
        /// SULFUR Together has not started yet). Never throws.</returns>
        public static bool Register(string id, SessionRequirementTier tier, string displayName, Func<string?> digest,
            params SessionIdClaim[] claims)
        {
            if (tier != SessionRequirementTier.Content)
            {
                SessionRequirementLog.Error($"refused to register '{SessionRequirementRegistry.Printable(id)}': unknown tier {(byte)tier}.");
                return false;
            }

            SessionIdRange[] ranges;
            if (claims == null) ranges = Array.Empty<SessionIdRange>();
            else
            {
                ranges = new SessionIdRange[claims.Length];
                for (int i = 0; i < claims.Length; i++)
                    ranges[i] = new SessionIdRange((byte)claims[i].Kind, claims[i].Min, claims[i].Max);
            }

            return SessionRequirementRegistry.TryRegister(id!, displayName!, digest!, ranges);
        }
    }
}
