using System;

namespace SULFURTogether.Config
{
    /// <summary>
    /// A persisted mod setting that is deliberately <b>not</b> a BepInEx <c>ConfigEntry</c>, so it never lands in the
    /// standard <c>.cfg</c> and is therefore invisible to external config managers (Gale). The value is backed by
    /// <see cref="CoopSettingsStore"/>'s own JSON file via the get/set delegates passed in.
    ///
    /// It exposes the same <c>.Value</c> get/set surface as <c>ConfigEntry&lt;T&gt;</c>, so the ~50 existing
    /// <c>Plugin.Cfg.Xxx.Value</c> call sites compile and behave unchanged — only the storage backend moved.
    /// </summary>
    public sealed class Setting<T>
    {
        private readonly Func<T>   _get;
        private readonly Action<T> _set;

        public Setting(Func<T> get, Action<T> set)
        {
            _get = get;
            _set = set;
        }

        public T Value
        {
            get => _get();
            set => _set(value);
        }

        public override string ToString() => Value?.ToString() ?? string.Empty;
    }
}
