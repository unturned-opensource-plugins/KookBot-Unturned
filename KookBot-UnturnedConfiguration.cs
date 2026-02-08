using Rocket.API;
using SDG.Unturned;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Xml.Serialization;

namespace Emqo.KookBot_Unturned
{
    public class KookBot_UnturnedConfiguration : IRocketPluginConfiguration
    {
        private static readonly string[] DefaultGameEventKeys =
        {
            "PlayerJoined",
            "PlayerLeft",
            "PlayerDeath",
            "PlayerRevive",
            "ChatMessage",
            "PlayerDamaged",
            "ServerStart",
            "ServerStop"
        };

        private static readonly string[] DefaultCommandKeys =
        {
            "help",
            "status",
            "list",
            "say",
            "cmd",
            "console",
            "exec",
            "mute",
            "unmute",
            "mutes"
        };

        public string ServerName { get; set; }
        public string BotToken { get; set; }
        public string ChannelId { get; set; }
        public bool EnableSync { get; set; }
        public string MessagePrefix { get; set; }
        public bool KookToGame { get; set; }
        public bool ExposeReviveCoordinates { get; set; }

        public List<SettingItem> GameToKookSettings { get; set; }
        public List<SettingItem> CommandSettings { get; set; }

        public List<string> Admin { get; set; }
        public bool Debug { get; set; }

        public ChatModerationConfig Moderation { get; set; }

        [XmlIgnore]
        public Dictionary<string, bool> GameToKook { get; private set; }

        [XmlIgnore]
        public Dictionary<string, bool> Commands { get; private set; }

        public KookBot_UnturnedConfiguration()
        {
            GameToKook = new Dictionary<string, bool>();
            Commands = new Dictionary<string, bool>();
            Moderation = ChatModerationConfig.CreateDefault();
        }

        public void LoadDefaults()
        {
            ServerName = "Unturned";
            BotToken = "Token";
            ChannelId = "ID";
            MessagePrefix = "[Unturned]";
            Admin = new List<string> { "123456", "654321" };
            EnableSync = true;
            KookToGame = true;
            Debug = false;
            ExposeReviveCoordinates = false;

            GameToKook = NormalizeSettings(null, DefaultGameEventKeys);
            Commands = NormalizeSettings(null, DefaultCommandKeys);

            GameToKookSettings = ToSettingList(GameToKook);
            CommandSettings = ToSettingList(Commands);

            Moderation = ChatModerationConfig.CreateDefault();
        }

        public void ConvertListsToDictionaries()
        {
            GameToKook = NormalizeSettings(GameToKookSettings, DefaultGameEventKeys);
            Commands = NormalizeSettings(CommandSettings, DefaultCommandKeys);

            GameToKookSettings = ToSettingList(GameToKook);
            CommandSettings = ToSettingList(Commands);

            Moderation ??= ChatModerationConfig.CreateDefault();
            Moderation.ApplyDefaultsIfNeeded();
        }

        public void ConvertDictionariesToLists()
        {
            GameToKook ??= NormalizeSettings(GameToKookSettings, DefaultGameEventKeys);
            Commands ??= NormalizeSettings(CommandSettings, DefaultCommandKeys);

            GameToKookSettings = ToSettingList(GameToKook);
            CommandSettings = ToSettingList(Commands);

            Moderation ??= ChatModerationConfig.CreateDefault();
            Moderation.ApplyDefaultsIfNeeded();
        }

        public void CleanDuplicateSettings()
        {
            ConvertListsToDictionaries();
        }

        public bool IsGameToKookEnabled(string eventName)
        {
            return GameToKook != null && GameToKook.ContainsKey(eventName) && GameToKook[eventName];
        }

        public bool IsCommandEnabled(string commandName)
        {
            return Commands != null && Commands.ContainsKey(commandName) && Commands[commandName];
        }

        private static Dictionary<string, bool> NormalizeSettings(List<SettingItem> source, IEnumerable<string> defaults)
        {
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in defaults)
            {
                if (!result.ContainsKey(key))
                {
                    result[key] = true;
                }
            }

            if (source == null)
            {
                return result;
            }

            foreach (var item in source.Where(x => !string.IsNullOrWhiteSpace(x.Key)))
            {
                result[item.Key] = item.Value;
            }

            return result;
        }

        private static List<SettingItem> ToSettingList(Dictionary<string, bool> source)
        {
            return source?
                .Select(pair => new SettingItem(pair.Key, pair.Value))
                .ToList()
                ?? new List<SettingItem>();
        }

        public void ApplyFrom(KookBot_UnturnedConfiguration source)
        {
            if (source == null)
            {
                return;
            }

            ServerName = source.ServerName;
            BotToken = source.BotToken;
            ChannelId = source.ChannelId;
            EnableSync = source.EnableSync;
            MessagePrefix = source.MessagePrefix;
            KookToGame = source.KookToGame;
            Debug = source.Debug;
            ExposeReviveCoordinates = source.ExposeReviveCoordinates;

            Admin = source.Admin != null
                ? new List<string>(source.Admin)
                : new List<string>();

            GameToKook = new Dictionary<string, bool>(source.GameToKook ?? new Dictionary<string, bool>(), StringComparer.OrdinalIgnoreCase);
            Commands = new Dictionary<string, bool>(source.Commands ?? new Dictionary<string, bool>(), StringComparer.OrdinalIgnoreCase);

            GameToKookSettings = ToSettingList(GameToKook);
            CommandSettings = ToSettingList(Commands);

            if (source.Moderation == null)
            {
                Moderation = ChatModerationConfig.CreateDefault();
            }
            else
            {
                Moderation = source.Moderation;
                Moderation.ApplyDefaultsIfNeeded();
            }
        }
    }

    public class SettingItem
    {
        public string Key { get; set; }
        public bool Value { get; set; }

        public SettingItem()
        {
        }

        public SettingItem(string key, bool value)
        {
            Key = key;
            Value = value;
        }
    }

    /// <summary>
    /// Chat moderation configuration including mute system, rate limiting, and forbidden word detection.
    /// </summary>
    public class ChatModerationConfig
    {
        // Mute system settings
        public bool EnableModeration { get; set; }
        public int AutoMuteDurationSeconds { get; set; }
        public int DefaultManualMuteMinutes { get; set; }
        public bool BroadcastSpamMutes { get; set; }

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
                AutoMuteDurationSeconds = 120,
                DefaultManualMuteMinutes = 10,
                BroadcastSpamMutes = true,

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

            if (AutoMuteDurationSeconds <= 0)
            {
                AutoMuteDurationSeconds = defaults.AutoMuteDurationSeconds;
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
                   AutoMuteDurationSeconds == 0 &&
                   DefaultManualMuteMinutes == 0;
        }

        private void CopyFrom(ChatModerationConfig other)
        {
            EnableModeration = other.EnableModeration;
            AutoMuteDurationSeconds = other.AutoMuteDurationSeconds;
            DefaultManualMuteMinutes = other.DefaultManualMuteMinutes;
            BroadcastSpamMutes = other.BroadcastSpamMutes;

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
