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

namespace Rocket.Core.Utils
{
    public static class TaskDispatcher
    {
        public static void QueueOnMainThread(Action action)
        {
            action();
        }
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

        public static string LastChannel { get; private set; }

        public static string BuildDiagnosticsReport() => "diagnostics";

        public static void UpdateChannel(string channelId)
        {
            LastChannel = channelId;
        }
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

namespace Emqo.KookBot_Unturned
{
    internal sealed class PluginConfigurationHolder
    {
        public KookBot_UnturnedConfiguration Instance { get; set; }
    }

    internal sealed class KookBot_UnturnedPlugin
    {
        public static KookBot_UnturnedPlugin Instance { get; set; }
        public PluginConfigurationHolder Configuration { get; set; } = new PluginConfigurationHolder();

        public static TimeSpan GetUptime() => TimeSpan.Zero;
    }

    internal static class ChatModerationManager
    {
        public static ChatModerationConfig LastUpdatedConfig { get; private set; }

        public static void UpdateConfig(ChatModerationConfig config)
        {
            LastUpdatedConfig = config;
        }
    }
}

namespace Steamworks
{
    public readonly struct CSteamID : IEquatable<CSteamID>
    {
        private readonly ulong _value;

        public CSteamID(ulong value)
        {
            _value = value;
        }

        public bool Equals(CSteamID other) => _value == other._value;
        public override bool Equals(object obj) => obj is CSteamID other && Equals(other);
        public override int GetHashCode() => _value.GetHashCode();
        public override string ToString() => _value.ToString();
        public static bool operator ==(CSteamID left, CSteamID right) => left.Equals(right);
        public static bool operator !=(CSteamID left, CSteamID right) => !left.Equals(right);
    }
}

namespace Rocket.Unturned.Player
{
    public class UnturnedPlayer
    {
        public Steamworks.CSteamID CSteamID { get; set; }
        public string DisplayName { get; set; }
        public string CharacterName { get; set; }
    }
}

namespace Emqo.KookBot_Unturned
{
    internal class MuteInfo
    {
        public Steamworks.CSteamID SteamId { get; set; }
        public string PlayerName { get; set; }
        public string Reason { get; set; }
        public string MutedBy { get; set; }
        public DateTimeOffset MutedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public bool IsAuto { get; set; }

        public bool IsExpired => ExpiresAt.HasValue && DateTimeOffset.UtcNow >= ExpiresAt.Value;

        public TimeSpan? Remaining =>
            ExpiresAt.HasValue ? ExpiresAt.Value - DateTimeOffset.UtcNow : (TimeSpan?)null;

        public string GetDurationDescription()
        {
            if (!ExpiresAt.HasValue)
            {
                return "permanent";
            }

            var remaining = ExpiresAt.Value - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return "expiring soon";
            }

            if (remaining.TotalHours >= 1)
            {
                return $"{Math.Ceiling(remaining.TotalHours)} hours";
            }

            if (remaining.TotalMinutes >= 1)
            {
                return $"{Math.Ceiling(remaining.TotalMinutes)} minutes";
            }

            return $"{Math.Ceiling(remaining.TotalSeconds)} seconds";
        }
    }
}
