using System.Collections.Generic;

namespace TwoBirds
{
    public sealed class GameLogOptions
    {
        public GameLogLevel? MinimumLevel;
        public Dictionary<string, GameLogLevel> ScopeOverrides;
        public bool ConsoleEnabled = true;
        public bool FileEnabled = true;
        public string DirectoryPath;

        internal GameLogOptions Copy()
        {
            var copy = new GameLogOptions
            {
                MinimumLevel = MinimumLevel,
                ConsoleEnabled = ConsoleEnabled,
                FileEnabled = FileEnabled,
                DirectoryPath = DirectoryPath
            };
            if (ScopeOverrides is { Count: > 0 })
            {
                int count = ScopeOverrides.Count > 128 ? 128 : ScopeOverrides.Count;
                copy.ScopeOverrides = new Dictionary<string, GameLogLevel>(count);
                int i = 0;
                foreach (var kv in ScopeOverrides)
                {
                    if (i++ >= 128) break;
                    string normalized = GameLogService.NormalizeScope(kv.Key);
                    copy.ScopeOverrides[normalized] = kv.Value;
                }
            }
            return copy;
        }
    }
}
