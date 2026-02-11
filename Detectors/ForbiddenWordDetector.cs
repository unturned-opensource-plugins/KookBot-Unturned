using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rocket.Unturned.Player;
using Steamworks;

namespace Emqo.KookBot_Unturned.Detectors
{
    /// <summary>
    /// Detects forbidden words in player messages.
    /// Uses case-insensitive substring matching with HashSet for performance.
    /// </summary>
    public class ForbiddenWordDetector : IMessageDetector
    {
        public string Name => "ForbiddenWord";
        public bool IsEnabled => _isEnabled;

        private volatile bool _isEnabled;
        private volatile string _warningMessage;
        private volatile bool _autoMuteEnabled;
        private volatile int _autoMuteSeconds;

        // Pre-cached lowercase forbidden words - volatile reference for copy-on-write
        private volatile HashSet<string> _forbiddenWordsLower = new(StringComparer.OrdinalIgnoreCase);

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

            _isEnabled = config.EnableForbiddenWordFilter;
            _warningMessage = !string.IsNullOrWhiteSpace(config.ForbiddenWordWarningMessage)
                ? config.ForbiddenWordWarningMessage
                : "消息包含违禁词，已被拦截。";
            _autoMuteEnabled = config.AutoMuteOnForbiddenWord;
            _autoMuteSeconds = config.AutoMuteForbiddenWordSeconds > 0
                ? config.AutoMuteForbiddenWordSeconds
                : 300;

            // Rebuild the forbidden words cache (copy-on-write)
            var newSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (config.ForbiddenWords != null)
            {
                foreach (var word in config.ForbiddenWords)
                {
                    if (!string.IsNullOrWhiteSpace(word))
                    {
                        newSet.Add(word.Trim());
                    }
                }
            }
            _forbiddenWordsLower = newSet;
        }

        public Task<DetectionResult> DetectAsync(UnturnedPlayer player, string message)
        {
            return Task.FromResult(DetectSync(player, message));
        }

        public DetectionResult DetectSync(UnturnedPlayer player, string message)
        {
            if (!_isEnabled || player == null || string.IsNullOrWhiteSpace(message))
            {
                return DetectionResult.Allowed();
            }

            // Read volatile reference snapshot (no lock needed with copy-on-write)
            var wordsSnapshot = _forbiddenWordsLower;
            if (wordsSnapshot.Count == 0)
            {
                return DetectionResult.Allowed();
            }

            // Check for forbidden words using substring matching
            var messageLower = message.ToLowerInvariant();
            foreach (var word in wordsSnapshot)
            {
                if (messageLower.Contains(word.ToLowerInvariant()))
                {
                    TimeSpan? muteDuration = _autoMuteEnabled
                        ? TimeSpan.FromSeconds(_autoMuteSeconds)
                        : (TimeSpan?)null;

                    return DetectionResult.Violation(
                        Name,
                        _warningMessage,
                        shouldAutoMute: _autoMuteEnabled,
                        autoMuteDuration: muteDuration
                    );
                }
            }

            return DetectionResult.Allowed();
        }

        public void Shutdown()
        {
            _forbiddenWordsLower = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public void CleanupPlayerData(CSteamID steamId)
        {
            // No per-player data to clean up
        }
    }
}
