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
            "say",
            "cmd",
            "console",
            "exec"
        };

        public string ServerName { get; set; }
        public string BotToken { get; set; }
        public string ChannelId { get; set; }
        public bool EnableSync { get; set; }
        public string MessagePrefix { get; set; }
        public bool KookToGame { get; set; }
        public bool ExposeReviveCoordinates { get; set; }
        // Auto Update
        public bool AutoUpdateEnabled { get; set; }
        public bool AutoRestartAfterUpdate { get; set; }
        public string UpdateRepoOwner { get; set; }
        public string UpdateRepoName { get; set; }
        public bool IncludePrereleases { get; set; }
        public int UpdateCheckIntervalMinutes { get; set; }

        // 使用可序列化的设置项列表替代Dictionary
        public List<SettingItem> GameToKookSettings { get; set; }
        public List<SettingItem> CommandSettings { get; set; }

        public List<string> Admin { get; set; }
        public bool Debug { get; set; }

        public ChatModerationConfig Moderation { get; set; }

        // 不序列化的Dictionary属性，用于代码中快速访问
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
            // Auto Update defaults
            AutoUpdateEnabled = false;
            AutoRestartAfterUpdate = false;
            UpdateRepoOwner = "Emqo";
            UpdateRepoName = "KookBot-Unturned";
            IncludePrereleases = false;
            UpdateCheckIntervalMinutes = 60;

            GameToKook = NormalizeSettings(null, DefaultGameEventKeys);
            Commands = NormalizeSettings(null, DefaultCommandKeys);

            GameToKookSettings = ToSettingList(GameToKook);
            CommandSettings = ToSettingList(Commands);

            Moderation = ChatModerationConfig.CreateDefault();
        }

        /// <summary>
        /// 将设置列表转换为Dictionary以便代码中使用
        /// </summary>
        public void ConvertListsToDictionaries()
        {
            GameToKook = NormalizeSettings(GameToKookSettings, DefaultGameEventKeys);
            Commands = NormalizeSettings(CommandSettings, DefaultCommandKeys);

            GameToKookSettings = ToSettingList(GameToKook);
            CommandSettings = ToSettingList(Commands);

            Moderation ??= ChatModerationConfig.CreateDefault();
            Moderation.ApplyDefaultsIfNeeded();
        }

        /// <summary>
        /// 将Dictionary转换回设置列表以便保存
        /// </summary>
        public void ConvertDictionariesToLists()
        {
            GameToKook ??= NormalizeSettings(GameToKookSettings, DefaultGameEventKeys);
            Commands ??= NormalizeSettings(CommandSettings, DefaultCommandKeys);

            GameToKookSettings = ToSettingList(GameToKook);
            CommandSettings = ToSettingList(Commands);

            Moderation ??= ChatModerationConfig.CreateDefault();
            Moderation.ApplyDefaultsIfNeeded();
        }

        /// <summary>
        /// 清理配置中的重复项
        /// </summary>
        public void CleanDuplicateSettings()
        {
            ConvertListsToDictionaries();
        }

        /// <summary>
        /// 检查指定事件是否启用
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <returns>是否启用</returns>
        public bool IsGameToKookEnabled(string eventName)
        {
            return GameToKook != null && GameToKook.ContainsKey(eventName) && GameToKook[eventName];
        }

        /// <summary>
        /// 检查指定指令是否启用
        /// </summary>
        /// <param name="commandName">指令名称</param>
        /// <returns>是否启用</returns>
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
            AutoUpdateEnabled = source.AutoUpdateEnabled;
            AutoRestartAfterUpdate = source.AutoRestartAfterUpdate;
            UpdateRepoOwner = string.IsNullOrWhiteSpace(source.UpdateRepoOwner) ? "Emqo" : source.UpdateRepoOwner;
            UpdateRepoName = string.IsNullOrWhiteSpace(source.UpdateRepoName) ? "KookBot-Unturned" : source.UpdateRepoName;
            IncludePrereleases = source.IncludePrereleases;
            UpdateCheckIntervalMinutes = source.UpdateCheckIntervalMinutes > 0 ? source.UpdateCheckIntervalMinutes : 60;

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

    /// <summary>
    /// 可序列化的设置项
    /// </summary>
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

    public class ChatModerationConfig
    {
        public bool EnableModeration { get; set; }
        public bool EnableSpamDetection { get; set; }
        public int SpamMessageLimit { get; set; }
        public int SpamIntervalSeconds { get; set; }
        public int MaxMessagesPerSecond { get; set; }
        public int AutoMuteDurationSeconds { get; set; }
        public bool EnableForbiddenWordFilter { get; set; }
        public bool AutoMuteOnForbiddenWord { get; set; }
        public List<string> ForbiddenWords { get; set; }
        public int MinimumSecondsBetweenMessages { get; set; }
        public string SpamWarningMessage { get; set; }
        public string ForbiddenWordWarningMessage { get; set; }
        public string MinIntervalWarningMessage { get; set; }
        public int DefaultManualMuteMinutes { get; set; }
        public bool EnableProfanityFilter { get; set; }
        public bool ReplaceProfanityWithMask { get; set; }
        public string ProfanityMask { get; set; }
        public string ProfanityWarningMessage { get; set; }
        public bool AutoMuteOnProfanity { get; set; }
        public int AutoMuteProfanitySeconds { get; set; }
        public List<string> ProfanityWords { get; set; }
        public List<string> ProfanityPatterns { get; set; }
        public bool BroadcastSpamMutes { get; set; }

        public bool EnableRepeatedCharacterDetection { get; set; }
        public int MaxRepeatedCharacters { get; set; }
        public bool EnableSimilarMessageDetection { get; set; }
        public int SimilarMessageThreshold { get; set; }
        public int SimilarMessageTimeWindowSeconds { get; set; }
        public double SimilarityPercentage { get; set; }
        public bool EnableEscalatingPenalties { get; set; }
        public List<int> MuteDurationEscalationSeconds { get; set; }
        public int ViolationResetHours { get; set; }

        public static ChatModerationConfig CreateDefault()
        {
            return new ChatModerationConfig
            {
                EnableModeration = true,
                EnableSpamDetection = true,
                SpamMessageLimit = 5,
                SpamIntervalSeconds = 8,
                MaxMessagesPerSecond = 3,
                AutoMuteDurationSeconds = 120,
                EnableForbiddenWordFilter = true,
                AutoMuteOnForbiddenWord = false,
                ForbiddenWords = new List<string> { "外挂", "作弊器", "刷钱" },
                MinimumSecondsBetweenMessages = 1,
                SpamWarningMessage = "你说话太频繁了，稍后再试。",
                ForbiddenWordWarningMessage = "消息包含违禁词，已被拦截。",
                MinIntervalWarningMessage = "说话太快啦，请稍后再试。",
                DefaultManualMuteMinutes = 10,
                EnableProfanityFilter = true,
                ReplaceProfanityWithMask = true,
                ProfanityMask = "***",
                ProfanityWarningMessage = "消息包含不当言辞，已被拦截。",
                AutoMuteOnProfanity = false,
                AutoMuteProfanitySeconds = 300,
                ProfanityWords = new List<string>
                {
                    "fuck", "shit", "bitch", "asshole", "bastard",
                    "sb", "cnm", "nmsl", "wdnmd", "mlgb",
                    "傻逼", "煞笔", "妈的", "操你", "草泥马", "日你"
                },
                ProfanityPatterns = new List<string>
                {
                    "f\\s*u\\s*c\\s*k",
                    "s\\s*h\\s*i\\s*t",
                    "b\\s*i\\s*t\\s*c\\s*h",
                    "c\\s*a\\s*o\\s*n\\s*i",
                    "n\\s*m\\s*s\\s*l"
                },
                BroadcastSpamMutes = true,
                EnableSimilarMessageDetection = true,
                SimilarMessageThreshold = 3,
                SimilarMessageTimeWindowSeconds = 30,
                SimilarityPercentage = 0.85,
                EnableEscalatingPenalties = true,
                MuteDurationEscalationSeconds = new List<int> { 60, 300, 900, 3600 },
                ViolationResetHours = 24
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

            EnableModeration = true;
            EnableSpamDetection = EnsureTrue(EnableSpamDetection, defaults.EnableSpamDetection);
            SpamMessageLimit = EnsureMinimum(SpamMessageLimit, defaults.SpamMessageLimit);
            SpamIntervalSeconds = EnsureMinimum(SpamIntervalSeconds, defaults.SpamIntervalSeconds);
            MaxMessagesPerSecond = EnsureNonNegative(MaxMessagesPerSecond, defaults.MaxMessagesPerSecond);
            AutoMuteDurationSeconds = EnsureNonNegative(AutoMuteDurationSeconds, defaults.AutoMuteDurationSeconds);
            MinimumSecondsBetweenMessages = EnsureMinimum(MinimumSecondsBetweenMessages, defaults.MinimumSecondsBetweenMessages);
            DefaultManualMuteMinutes = EnsureMinimum(DefaultManualMuteMinutes, defaults.DefaultManualMuteMinutes);

            EnableForbiddenWordFilter = EnsureTrue(EnableForbiddenWordFilter, defaults.EnableForbiddenWordFilter);
            EnableProfanityFilter = EnsureTrue(EnableProfanityFilter, defaults.EnableProfanityFilter);

            if (string.IsNullOrWhiteSpace(SpamWarningMessage))
            {
                SpamWarningMessage = defaults.SpamWarningMessage;
            }

            if (string.IsNullOrWhiteSpace(ForbiddenWordWarningMessage))
            {
                ForbiddenWordWarningMessage = defaults.ForbiddenWordWarningMessage;
            }

            if (string.IsNullOrWhiteSpace(MinIntervalWarningMessage))
            {
                MinIntervalWarningMessage = defaults.MinIntervalWarningMessage;
            }

            if (string.IsNullOrWhiteSpace(ProfanityWarningMessage))
            {
                ProfanityWarningMessage = defaults.ProfanityWarningMessage;
            }

            if (string.IsNullOrWhiteSpace(ProfanityMask))
            {
                ProfanityMask = defaults.ProfanityMask;
            }

            AutoMuteOnForbiddenWord |= defaults.AutoMuteOnForbiddenWord;
            AutoMuteOnProfanity |= defaults.AutoMuteOnProfanity;
            AutoMuteProfanitySeconds = EnsureNonNegative(AutoMuteProfanitySeconds, defaults.AutoMuteProfanitySeconds);

            ForbiddenWords = EnsureList(ForbiddenWords, defaults.ForbiddenWords);
            ProfanityWords = EnsureList(ProfanityWords, defaults.ProfanityWords);
            ProfanityPatterns = EnsureList(ProfanityPatterns, defaults.ProfanityPatterns);

            EnableRepeatedCharacterDetection = EnableRepeatedCharacterDetection || defaults.EnableRepeatedCharacterDetection;
            MaxRepeatedCharacters = EnsureMinimum(MaxRepeatedCharacters, defaults.MaxRepeatedCharacters);
            EnableSimilarMessageDetection = EnableSimilarMessageDetection || defaults.EnableSimilarMessageDetection;
            SimilarMessageThreshold = EnsureMinimum(SimilarMessageThreshold, defaults.SimilarMessageThreshold);
            SimilarMessageTimeWindowSeconds = EnsureMinimum(SimilarMessageTimeWindowSeconds, defaults.SimilarMessageTimeWindowSeconds);
            SimilarityPercentage = SimilarityPercentage > 0 && SimilarityPercentage <= 1 ? SimilarityPercentage : defaults.SimilarityPercentage;
            EnableEscalatingPenalties = EnableEscalatingPenalties || defaults.EnableEscalatingPenalties;
            MuteDurationEscalationSeconds = EnsureList(MuteDurationEscalationSeconds, defaults.MuteDurationEscalationSeconds);
            ViolationResetHours = EnsureMinimum(ViolationResetHours, defaults.ViolationResetHours);
        }

        private bool IsUninitialized()
        {
            return !EnableModeration &&
                   !EnableSpamDetection &&
                   SpamMessageLimit == 0 &&
                   SpamIntervalSeconds == 0 &&
                   MaxMessagesPerSecond == 0 &&
                   AutoMuteDurationSeconds == 0 &&
                   !EnableForbiddenWordFilter &&
                   !AutoMuteOnForbiddenWord &&
                   (ForbiddenWords == null || ForbiddenWords.Count == 0) &&
                   MinimumSecondsBetweenMessages == 0 &&
                   string.IsNullOrWhiteSpace(SpamWarningMessage) &&
                   string.IsNullOrWhiteSpace(ForbiddenWordWarningMessage) &&
                   string.IsNullOrWhiteSpace(MinIntervalWarningMessage) &&
                   DefaultManualMuteMinutes == 0 &&
                   !EnableProfanityFilter &&
                   !ReplaceProfanityWithMask &&
                   string.IsNullOrWhiteSpace(ProfanityMask) &&
                   string.IsNullOrWhiteSpace(ProfanityWarningMessage) &&
                   !AutoMuteOnProfanity &&
                   AutoMuteProfanitySeconds == 0 &&
                   (ProfanityWords == null || ProfanityWords.Count == 0) &&
                   (ProfanityPatterns == null || ProfanityPatterns.Count == 0);
        }

        private static bool EnsureTrue(bool current, bool defaultValue)
        {
            return current || defaultValue;
        }

        private static int EnsureMinimum(int current, int minimum)
        {
            return current > 0 ? current : minimum;
        }

        private static int EnsureNonNegative(int current, int fallback)
        {
            return current >= 0 ? current : fallback;
        }

        private static List<string> EnsureList(List<string> current, List<string> defaults)
        {
            var cleaned = current?
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (cleaned.Count == 0 && defaults != null)
            {
                cleaned.AddRange(defaults);
            }

            return cleaned;
        }

        private static List<int> EnsureList(List<int> current, List<int> defaults)
        {
            if (current == null || current.Count == 0)
            {
                return defaults != null ? new List<int>(defaults) : new List<int>();
            }
            return new List<int>(current);
        }

        private void CopyFrom(ChatModerationConfig other)
        {
            EnableModeration = other.EnableModeration;
            EnableSpamDetection = other.EnableSpamDetection;
            SpamMessageLimit = other.SpamMessageLimit;
            SpamIntervalSeconds = other.SpamIntervalSeconds;
            AutoMuteDurationSeconds = other.AutoMuteDurationSeconds;
            EnableForbiddenWordFilter = other.EnableForbiddenWordFilter;
            AutoMuteOnForbiddenWord = other.AutoMuteOnForbiddenWord;
            ForbiddenWords = other.ForbiddenWords != null ? new List<string>(other.ForbiddenWords) : new List<string>();
            MinimumSecondsBetweenMessages = other.MinimumSecondsBetweenMessages;
            SpamWarningMessage = other.SpamWarningMessage;
            ForbiddenWordWarningMessage = other.ForbiddenWordWarningMessage;
            MinIntervalWarningMessage = other.MinIntervalWarningMessage;
            DefaultManualMuteMinutes = other.DefaultManualMuteMinutes;
            EnableProfanityFilter = other.EnableProfanityFilter;
            ReplaceProfanityWithMask = other.ReplaceProfanityWithMask;
            ProfanityMask = other.ProfanityMask;
            ProfanityWarningMessage = other.ProfanityWarningMessage;
            AutoMuteOnProfanity = other.AutoMuteOnProfanity;
            AutoMuteProfanitySeconds = other.AutoMuteProfanitySeconds;
            ProfanityWords = other.ProfanityWords != null ? new List<string>(other.ProfanityWords) : new List<string>();
            ProfanityPatterns = other.ProfanityPatterns != null ? new List<string>(other.ProfanityPatterns) : new List<string>();
        }
    }
}