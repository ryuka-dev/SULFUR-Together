namespace SULFURTogether.Config
{
    /// <summary>
    /// A hardcoded setting value. Used for functional behaviour that is no longer a player-tunable option — it ran on
    /// a forced dev-default anyway, so its value is now baked into code and removed from the BepInEx <c>.cfg</c>
    /// (release cleanup; see <c>Docs/ConfigAndLoggingConventions.md</c>).
    ///
    /// Exposes the same read-only <c>.Value</c> surface as <c>ConfigEntry&lt;T&gt;</c>, so the existing
    /// <c>Plugin.Cfg.Xxx.Value</c> call sites compile and behave identically — only the storage (a constant instead of
    /// a bound config entry) changed. A struct, so there is no per-entry allocation.
    /// </summary>
    public readonly struct Fixed<T>
    {
        public Fixed(T value) { Value = value; }

        public T Value { get; }

        public override string ToString() => Value?.ToString() ?? string.Empty;
    }
}
