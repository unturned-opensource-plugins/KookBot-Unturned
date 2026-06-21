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

namespace Emqo.KookBot_Unturned.KookApi
{
    public class Message
    {
        public readonly List<(int Type, string ChannelId, object Content)> Sent = new();

        public Message(string botToken = "test-token")
        {
        }

        public Task<string> CreateMessageAsync(int type, string channelId, object content)
        {
            Sent.Add((type, channelId, content));
            return Task.FromResult("{}");
        }
    }
}

namespace Emqo.KookBot_Unturned
{
    internal static class Events
    {
        public static readonly Dictionary<string, bool> RuntimeEvents = new(StringComparer.OrdinalIgnoreCase);

        public static void SetEventEnabled(string eventName, bool enabled)
        {
            RuntimeEvents[eventName] = enabled;
        }

        public static string BuildDiagnosticsReport() => "diagnostics";
    }
}

namespace Emqo.KookBot_Unturned.Monitoring
{
    internal class ServerStatusSnapshot
    {
        public DateTimeOffset CapturedAt { get; set; }
        public int OnlinePlayers { get; set; }
        public int MaxPlayers { get; set; }
        public int QueueLength { get; set; }
        public int EstimatedTps { get; set; }
        public TimeSpan Uptime { get; set; }
    }
}
