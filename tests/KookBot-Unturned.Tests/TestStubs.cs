namespace Rocket.API
{
    public interface IRocketPluginConfiguration
    {
        void LoadDefaults();
    }
}

namespace Rocket.Core.Logging
{
    public static class Logger
    {
        public static readonly List<string> Messages = new();

        public static void Log(string message) => Messages.Add(message);
        public static void LogWarning(string message) => Messages.Add($"WARN:{message}");
        public static void LogError(string message) => Messages.Add($"ERROR:{message}");
        public static void Clear() => Messages.Clear();
    }
}

namespace SDG.Unturned
{
}
