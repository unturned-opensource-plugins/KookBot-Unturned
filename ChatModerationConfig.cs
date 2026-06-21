using System.Collections.Generic;

namespace Emqo.KookBot_Unturned
{
    /// <summary>
    /// Chat moderation configuration including mute system, rate limiting, and forbidden word detection.
    /// </summary>
    public class ChatModerationConfig
    {
        // Mute system settings
        public bool EnableModeration { get; set; }
        public int DefaultManualMuteMinutes { get; set; }
        public bool BroadcastAutoMutes { get; set; }

        // Rate limit detection settings
        public bool EnableRateLimitDetection { get; set; }
        public int MinimumSecondsBetweenMessages { get; set; }
        public string RateLimitWarningMessage { get; set; }

        // Forbidden word detection settings
        public bool EnableForbiddenWordFilter { get; set; }
        public List<string> ForbiddenWords { get; set; }
        public string ForbiddenWordWarningMessage { get; set; }
        public bool AutoMuteOnForbiddenWord { get; set; }
        public int AutoMuteForbiddenWordSeconds { get; set; }

        public static ChatModerationConfig CreateDefault()
        {
            return new ChatModerationConfig
            {
                // Mute system defaults
                EnableModeration = true,
                DefaultManualMuteMinutes = 10,
                BroadcastAutoMutes = true,

                // Rate limit defaults
                EnableRateLimitDetection = true,
                MinimumSecondsBetweenMessages = 1,
                RateLimitWarningMessage = "说话太快啦，请稍后再试。",

                // Forbidden word defaults
                EnableForbiddenWordFilter = true,
                ForbiddenWords = new List<string>(),
                ForbiddenWordWarningMessage = "消息包含违禁词，已被拦截。",
                AutoMuteOnForbiddenWord = false,
                AutoMuteForbiddenWordSeconds = 300
            };
        }

        public void ApplyDefaultsIfNeeded()
        {
            var defaults = CreateDefault();

            if (IsUninitialized())
            {
                CopyFrom(defaults);
                return;
            }

            if (DefaultManualMuteMinutes <= 0)
            {
                DefaultManualMuteMinutes = defaults.DefaultManualMuteMinutes;
            }

            // Rate limit defaults
            if (MinimumSecondsBetweenMessages <= 0)
            {
                MinimumSecondsBetweenMessages = defaults.MinimumSecondsBetweenMessages;
            }

            if (string.IsNullOrWhiteSpace(RateLimitWarningMessage))
            {
                RateLimitWarningMessage = defaults.RateLimitWarningMessage;
            }

            // Forbidden word defaults
            ForbiddenWords ??= new List<string>();

            if (string.IsNullOrWhiteSpace(ForbiddenWordWarningMessage))
            {
                ForbiddenWordWarningMessage = defaults.ForbiddenWordWarningMessage;
            }

            if (AutoMuteForbiddenWordSeconds <= 0)
            {
                AutoMuteForbiddenWordSeconds = defaults.AutoMuteForbiddenWordSeconds;
            }
        }

        private bool IsUninitialized()
        {
            return !EnableModeration &&
                   DefaultManualMuteMinutes == 0;
        }

        private void CopyFrom(ChatModerationConfig other)
        {
            EnableModeration = other.EnableModeration;
            DefaultManualMuteMinutes = other.DefaultManualMuteMinutes;
            BroadcastAutoMutes = other.BroadcastAutoMutes;

            EnableRateLimitDetection = other.EnableRateLimitDetection;
            MinimumSecondsBetweenMessages = other.MinimumSecondsBetweenMessages;
            RateLimitWarningMessage = other.RateLimitWarningMessage;

            EnableForbiddenWordFilter = other.EnableForbiddenWordFilter;
            ForbiddenWords = other.ForbiddenWords != null ? new List<string>(other.ForbiddenWords) : new List<string>();
            ForbiddenWordWarningMessage = other.ForbiddenWordWarningMessage;
            AutoMuteOnForbiddenWord = other.AutoMuteOnForbiddenWord;
            AutoMuteForbiddenWordSeconds = other.AutoMuteForbiddenWordSeconds;
        }
    }
}
