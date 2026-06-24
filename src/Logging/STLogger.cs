using BepInEx.Logging;
using SULFURTogether.Config;

namespace SULFURTogether.Logging
{
    public class STLogger
    {
        private const string Prefix = "[SULFUR Together] ";

        private readonly ManualLogSource _src;
        private readonly CoopConfig      _cfg;

        public STLogger(ManualLogSource src, CoopConfig cfg)
        {
            _src = src;
            _cfg = cfg;
        }

        public void Info(string msg)  => _src.LogInfo(Prefix + msg);
        public void Warn(string msg)  => _src.LogWarning(Prefix + msg);
        public void Error(string msg) => _src.LogError(Prefix + msg);

        public void Debug(string msg)
        {
            if (_cfg.EnableDebugLog.Value)
                _src.LogDebug(Prefix + msg);
        }
    }
}
