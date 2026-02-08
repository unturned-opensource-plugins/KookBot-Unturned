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

        // Pre-cached lowercase forbidden words for fast lookup
        private HashSet<string> _forbiddenWordsLower = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _wordsLock = new();

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

            // Rebuild the forbidden words cache
            lock (_wordsLock)
            {
                _forbiddenWordsLower = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (config.ForbiddenWords != null)
                {
                    foreach (var word in config.ForbiddenWords)
                    {
                        if (!string.IsNullOrWhiteSpace(word))
                        {
                            _forbiddenWordsLower.Add(word.Trim());
                        }
                    }
                }
            }
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

            // Get a snapshot of the forbidden words
            HashSet<string> wordsSnapshot;
            lock (_wordsLock)
            {
                if (_forbiddenWordsLower.Count == 0)
                {
                    return DetectionResult.Allowed();
                }
                wordsSnapshot = new HashSet<string>(_forbiddenWordsLower, StringComparer.OrdinalIgnoreCase);
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
            lock (_wordsLock)
            {
                _forbiddenWordsLower.Clear();
            }
        }

        public void CleanupPlayerData(CSteamID steamId)
        {
            // No per-player data to clean up
        }
    }
}
