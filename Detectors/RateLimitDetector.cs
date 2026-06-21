using System;
using System.Collections.Concurrent;
using Rocket.Unturned.Player;
using Steamworks;

namespace Emqo.KookBot_Unturned.Detectors
{
    /// <summary>
    /// Detects rate limit violations by checking time intervals between player messages.
    /// </summary>
    public class RateLimitDetector : IMessageDetector
    {
        public string Name => "RateLimit";
        public bool IsEnabled => _isEnabled;

        private volatile bool _isEnabled;
        private volatile int _minimumSecondsBetweenMessages;
        private volatile string _warningMessage;

        // Thread-safe storage for last message times
        private readonly ConcurrentDictionary<CSteamID, DateTimeOffset> _lastMessageTimes = new();

        public void Initialize(ChatModerationConfig config)
        {
            UpdateConfig(config);
        }

        public void UpdateConfig(ChatModerationConfig config)
        {
            if (config == null)
            {
                _isEnabled = false;
                return;
            }

            _isEnabled = config.EnableRateLimitDetection;
            _minimumSecondsBetweenMessages = config.MinimumSecondsBetweenMessages > 0
                ? config.MinimumSecondsBetweenMessages
                : 1;
            _warningMessage = !string.IsNullOrWhiteSpace(config.RateLimitWarningMessage)
                ? config.RateLimitWarningMessage
                : "说话太快啦，请稍后再试。";
        }

        public DetectionResult DetectSync(UnturnedPlayer player, string message)
        {
            if (!_isEnabled || player == null)
            {
                return DetectionResult.Allowed();
            }

            var steamId = player.CSteamID;
            var now = DateTimeOffset.UtcNow;

            // Check if player has sent a message recently
            if (_lastMessageTimes.TryGetValue(steamId, out var lastTime))
            {
                var elapsed = (now - lastTime).TotalSeconds;
                if (elapsed < _minimumSecondsBetweenMessages)
                {
                    // Update the last message time even on violation to prevent spam
                    _lastMessageTimes[steamId] = now;

                    return DetectionResult.Violation(
                        Name,
                        _warningMessage,
                        shouldAutoMute: false
                    );
                }
            }

            // Update last message time
            _lastMessageTimes[steamId] = now;

            return DetectionResult.Allowed();
        }

        public void Shutdown()
        {
            _lastMessageTimes.Clear();
        }

        public void CleanupPlayerData(CSteamID steamId)
        {
            _lastMessageTimes.TryRemove(steamId, out _);
        }
    }
}
