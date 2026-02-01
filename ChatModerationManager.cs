using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Rocket.Core.Logging;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using Emqo.KookBot_Unturned.Interfaces;

namespace Emqo.KookBot_Unturned
{
    internal static class ChatModerationManager
    {
        // Configuration constants
        private const int SimilarityMaxLength = 200;  // Maximum length for similarity comparison
        private const double SimilarityLengthThreshold = 0.5;  // Length difference threshold (50%)
        private const int SimilarMessageHistoryMultiplier = 3;  // Multiplier for message history size
        private const int MinimumMessageHistorySize = 10;  // Minimum message history to keep

        // 优化：预定义分隔符数组，避免每次都创建新数组
        private static readonly char[] WordSeparators = { ' ', '\t', '\n', '\r', ',', '.', '!', '?', ';', ':', '-' };

        // Thread-safe concurrent dictionaries - no semaphore needed
        private static readonly ConcurrentDictionary<CSteamID, MuteInfo> MuteMap = new();
        private static readonly ConcurrentDictionary<CSteamID, List<DateTimeOffset>> MessageTimestamps = new();
        private static readonly ConcurrentDictionary<CSteamID, DateTimeOffset> LastMessageTimes = new();
        private static readonly ConcurrentDictionary<CSteamID, List<string>> RecentMessages = new();  // 存储最近消息用于相似度检测
        private static readonly ConcurrentDictionary<CSteamID, ViolationRecord> ViolationRecords = new();  // 违规记录用于递增惩罚
        private static Dictionary<string, string> _profanityWordLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _forbiddenWordsLower = new(StringComparer.OrdinalIgnoreCase);  // Pre-cached lowercase forbidden words
        private static List<Regex> _profanityRegexes = new List<Regex>();
        private static CancellationTokenSource _cleanupCancellationTokenSource;
        private static Task _cleanupTask;

        // Cleanup throttling
        private static DateTimeOffset _lastCleanupTime = DateTimeOffset.MinValue;
        private const int CleanupIntervalSeconds = 60;

        private static ChatModerationConfig _config;
        private static IConfigurationProvider _configProvider;

        private static bool ShouldLogDebug => _configProvider?.IsDebugEnabled ?? KookBot_UnturnedPlugin.Instance?.Configuration?.Instance?.Debug ?? false;

        internal static void Initialize(KookBot_UnturnedConfiguration configuration, IConfigurationProvider configProvider = null)
        {
            _configProvider = configProvider ?? new DefaultConfigurationProvider(() => KookBot_UnturnedPlugin.Instance?.Configuration?.Instance);

            if (configuration?.Moderation == null)
            {
                configuration.Moderation = ChatModerationConfig.CreateDefault();
            }

            configuration.Moderation.ApplyDefaultsIfNeeded();
            _config = configuration.Moderation;
            RefreshProfanityCache();

            // 异步清理过期禁言，不阻塞主线程
            _ = CleanupExpiredMutesAsync();

            // 启动定期清理任务 - 每5分钟清理一次过期禁言和陈旧数据
            _cleanupCancellationTokenSource = new CancellationTokenSource();
            _cleanupTask = StartPeriodicCleanupAsync(_cleanupCancellationTokenSource.Token);
        }

        internal static void UpdateConfig(ChatModerationConfig config)
        {
            if (config == null)
            {
                _config = ChatModerationConfig.CreateDefault();
            }
            else
            {
                config.ApplyDefaultsIfNeeded();
                _config = config;
            }

            RefreshProfanityCache();
        }

        internal static void Shutdown()
        {
            try
            {
                // Cancel the cleanup task
                _cleanupCancellationTokenSource?.Cancel();
                _cleanupCancellationTokenSource?.Dispose();

                // Wait for cleanup task with timeout instead of blocking delay
                if (_cleanupTask != null && !_cleanupTask.IsCompleted)
                {
                    try
                    {
                        _cleanupTask.Wait(TimeSpan.FromSeconds(1));
                    }
                    catch (AggregateException)
                    {
                        // 忽略清理任务的异常，确保关闭流程继续
                    }
                }

                // Clear all dictionaries (ConcurrentDictionary.Clear is thread-safe)
                MuteMap.Clear();
                MessageTimestamps.Clear();
                LastMessageTimes.Clear();
                RecentMessages.Clear();
                ViolationRecords.Clear();
                _profanityWordLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _forbiddenWordsLower = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _profanityRegexes = new List<Regex>();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error during ChatModerationManager shutdown: {ex.Message}");
            }
        }

        internal static async Task<ChatModerationResult> EvaluateMessageAsync(UnturnedPlayer player, string message)
        {
            var result = new ChatModerationResult { IsAllowed = true };

            if (player == null || string.IsNullOrEmpty(message))
            {
                return result;
            }

            var settings = _config ?? ChatModerationConfig.CreateDefault();
            var now = DateTimeOffset.UtcNow;

            if (!settings.EnableModeration)
            {
                await RecordMessageTimestampAsync(player.CSteamID, now, settings);
                UpdateLastMessageTime(player.CSteamID, now);
                return result;
            }

            // Cleanup expired mutes asynchronously with throttling
            if ((now - _lastCleanupTime).TotalSeconds > CleanupIntervalSeconds)
            {
                await CleanupExpiredMutesAsync();
                _lastCleanupTime = now;
            }

            if (IsMuted(player.CSteamID, out var muteInfo))
            {
                result.IsAllowed = false;
                result.DenyReason = BuildMuteMessage(muteInfo);
                result.AppliedMute = muteInfo;
                return result;
            }

            if (EnforceMinimumInterval(player, now, settings, result))
            {
                return result;
            }

            if (settings.EnableForbiddenWordFilter &&
                TryHandleForbiddenWord(player, message, now, settings, result))
            {
                return result;
            }

            var sanitizedMessage = message;
            if (settings.EnableProfanityFilter &&
                TryHandleProfanity(player, message, now, settings, result, ref sanitizedMessage))
            {
                return result;
            }

            // 增强刷屏检测：重复字符
            if (settings.EnableRepeatedCharacterDetection &&
                TryHandleRepeatedCharacters(player, message, settings, result))
            {
                return result;
            }

            await RecordMessageTimestampAsync(player.CSteamID, now, settings);

            // 标准刷屏检测
            if (TryHandleSpamViolation(player, message, now, settings, result))
            {
                return result;
            }

            // 相似消息检测（仅检测单人，排除指令）
            if (settings.EnableSimilarMessageDetection &&
                !message.StartsWith("/") &&
                TryHandleSimilarMessages(player, message, now, settings, result))
            {
                return result;
            }

            // 记录消息用于相似度检测
            if (!message.StartsWith("/"))
            {
                RecordRecentMessage(player.CSteamID, message, now, settings);
            }


            UpdateLastMessageTime(player.CSteamID, now);

            if (result.SanitizedMessage == null &&
                !string.Equals(sanitizedMessage, message, StringComparison.Ordinal))
            {
                result.SanitizedMessage = sanitizedMessage;
            }

            // Only include moderation log if debug logging is enabled
            if (!ShouldLogDebug)
            {
                result.ModerationLog = null;
            }

            return result;
        }

        internal static bool TryMutePlayer(UnturnedPlayer player, TimeSpan? duration, string reason, string mutedBy, out MuteInfo muteInfo)
        {
            muteInfo = null;
            if (player == null)
            {
                return false;
            }

            muteInfo = MutePlayer(player.CSteamID, player.DisplayName, duration, reason, mutedBy, false);
            NotifyPlayerMute(player, muteInfo);
            return true;
        }

        internal static bool MutePlayerById(CSteamID steamId, string playerName, TimeSpan? duration, string reason, string mutedBy, out MuteInfo muteInfo)
        {
            muteInfo = MutePlayer(steamId, playerName, duration, reason, mutedBy, false);
            return muteInfo != null;
        }

        private static MuteInfo MutePlayer(CSteamID steamId, string playerName, TimeSpan? duration, string reason, string mutedBy, bool isAuto)
        {
            var now = DateTimeOffset.UtcNow;
            var info = new MuteInfo
            {
                SteamId = steamId,
                PlayerName = playerName,
                Reason = string.IsNullOrWhiteSpace(reason) ? (isAuto ? "系统自动禁言" : "管理员禁言") : reason,
                MutedBy = string.IsNullOrWhiteSpace(mutedBy) ? "未知" : mutedBy,
                MutedAt = now,
                IsAuto = isAuto
            };

            if (duration.HasValue && duration.Value > TimeSpan.Zero)
            {
                info.ExpiresAt = now.Add(duration.Value);
            }

            try
            {
                MuteMap[steamId] = info;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error muting player: {ex.Message}");
            }

            if (ShouldLogDebug)
            {
                Logger.Log($"🔇 Player {playerName} muted by {info.MutedBy} ({info.GetDurationDescription()}) Reason: {info.Reason}");
            }
            return info;
        }

        internal static bool TryUnmutePlayer(string playerNameOrId, out MuteInfo muteInfo)
        {
            muteInfo = null;

            try
            {
                var entry = MuteMap.FirstOrDefault(pair =>
                    pair.Value.PlayerName.Equals(playerNameOrId, StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.ToString().Equals(playerNameOrId, StringComparison.OrdinalIgnoreCase));

                if (entry.Value == null)
                {
                    return false;
                }

                muteInfo = entry.Value;
                MuteMap.TryRemove(entry.Key, out _);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error unmuting player: {ex.Message}");
                return false;
            }
            finally
            {
                if (muteInfo != null)
                {
                    Logger.Log($"🔈 Player {muteInfo.PlayerName} unmuted manually.");
                }
            }
        }

        internal static bool UnmuteBySteamId(CSteamID steamId, out MuteInfo muteInfo)
        {
            muteInfo = null;

            try
            {
                if (!MuteMap.TryRemove(steamId, out muteInfo))
                {
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error unmuting player by SteamID: {ex.Message}");
                return false;
            }
            finally
            {
                if (muteInfo != null)
                {
                    Logger.Log($"🔈 Player {muteInfo.PlayerName} unmuted (SteamID match).");
                }
            }
        }

        internal static IReadOnlyCollection<MuteInfo> GetActiveMutes()
        {
            try
            {
                var now = DateTimeOffset.UtcNow;

                // Clean up expired mutes using ConcurrentDictionary
                foreach (var pair in MuteMap)
                {
                    if (pair.Value.IsExpired)
                    {
                        MuteMap.TryRemove(pair.Key, out _);
                    }
                }

                return MuteMap.Values.ToList();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error getting active mutes: {ex.Message}");
                return new List<MuteInfo>();
            }
        }

        internal static bool IsMuted(CSteamID steamId, out MuteInfo muteInfo)
        {
            muteInfo = null;
            try
            {
                if (!MuteMap.TryGetValue(steamId, out var info))
                {
                    return false;
                }

                if (info.IsExpired)
                {
                    MuteMap.TryRemove(steamId, out _);
                    return false;
                }

                muteInfo = info;
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error checking mute status: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Fast synchronous mute check for main thread use.
        /// Thread-safe with ConcurrentDictionary.
        /// </summary>
        internal static bool IsMutedSync(CSteamID steamId)
        {
            try
            {
                // ConcurrentDictionary.TryGetValue is thread-safe
                if (!MuteMap.TryGetValue(steamId, out var info))
                {
                    return false;
                }
                return !info.IsExpired;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in IsMutedSync: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Fast synchronous rate limit check for main thread use.
        /// </summary>
        internal static bool IsRateLimitedSync(CSteamID steamId)
        {
            try
            {
                var settings = _config ?? ChatModerationConfig.CreateDefault();
                if (settings.MinimumSecondsBetweenMessages <= 0)
                {
                    return false;
                }

                if (!LastMessageTimes.TryGetValue(steamId, out var lastTime))
                {
                    return false;
                }

                var diffSeconds = (DateTimeOffset.UtcNow - lastTime).TotalSeconds;
                return diffSeconds < settings.MinimumSecondsBetweenMessages;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in IsRateLimitedSync: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Clean up player data when they disconnect to prevent memory leaks.
        /// </summary>
        internal static void CleanupPlayerData(CSteamID steamId)
        {
            try
            {
                // ConcurrentDictionary.TryRemove is thread-safe
                MessageTimestamps.TryRemove(steamId, out _);
                LastMessageTimes.TryRemove(steamId, out _);
                RecentMessages.TryRemove(steamId, out _);
                ViolationRecords.TryRemove(steamId, out _);
                // Note: MuteMap is NOT cleaned - mutes should persist even after disconnect
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error cleaning up player data: {ex.Message}");
            }
        }

        private static void NotifyPlayerMute(UnturnedPlayer player, MuteInfo muteInfo)
        {
            if (player == null || muteInfo == null)
            {
                return;
            }

            Rocket.Core.Utils.TaskDispatcher.QueueOnMainThread(() =>
            {
                var message = BuildMuteMessage(muteInfo);
                UnturnedChat.Say(player, message, UnityEngine.Color.red);
            });
        }

        private static Task RecordMessageTimestampAsync(CSteamID steamId, DateTimeOffset timestamp, ChatModerationConfig settings)
        {
            if (!settings.EnableSpamDetection && settings.MinimumSecondsBetweenMessages <= 0)
            {
                return Task.CompletedTask;
            }

            try
            {
                var timestamps = MessageTimestamps.GetOrAdd(steamId, _ => new List<DateTimeOffset>());

                lock (timestamps)
                {
                    timestamps.Add(timestamp);

                    var threshold = timestamp.AddSeconds(-Math.Max(settings.SpamIntervalSeconds, settings.MinimumSecondsBetweenMessages));
                    timestamps.RemoveAll(t => t < threshold);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error recording message timestamp: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private static Task CleanupExpiredMutesAsync()
        {
            foreach (var pair in MuteMap)
            {
                if (pair.Value.IsExpired)
                {
                    MuteMap.TryRemove(pair.Key, out _);
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 启动定期清理任务 - 每5分钟清理一次过期禁言和陈旧数据
        /// </summary>
        private static async Task StartPeriodicCleanupAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // 每5分钟执行一次清理
                        await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);

                        if (!cancellationToken.IsCancellationRequested)
                        {
                            await PerformFullCleanupAsync();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // 正常取消，退出循环
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Error during periodic cleanup: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Periodic cleanup task failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 执行完整的清理操作：清理过期禁言、陈旧的消息历史和违规记录
        /// </summary>
        private static Task PerformFullCleanupAsync()
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                int expiredMuteCount = 0;
                int staleMessageCount = 0;
                int staleViolationCount = 0;

                // 1. 清理过期禁言 - ConcurrentDictionary iteration is safe
                foreach (var pair in MuteMap)
                {
                    if (pair.Value.IsExpired)
                    {
                        if (MuteMap.TryRemove(pair.Key, out _))
                        {
                            expiredMuteCount++;
                        }
                    }
                }

                // 2. 清理陈旧的消息历史（超过1小时未活动的玩家）
                foreach (var steamId in RecentMessages.Keys)
                {
                    if (LastMessageTimes.TryGetValue(steamId, out var lastTime))
                    {
                        if ((now - lastTime).TotalHours > 1)
                        {
                            if (RecentMessages.TryRemove(steamId, out _))
                            {
                                staleMessageCount++;
                            }
                        }
                    }
                }

                // 3. 清理陈旧的违规记录（超过24小时未活动的玩家）
                foreach (var steamId in ViolationRecords.Keys)
                {
                    if (ViolationRecords.TryGetValue(steamId, out var record))
                    {
                        if ((now - record.LastViolation).TotalHours > 24)
                        {
                            if (ViolationRecords.TryRemove(steamId, out _))
                            {
                                staleViolationCount++;
                            }
                        }
                    }
                }

                if (ShouldLogDebug)
                {
                    Logger.Log($"🧹 Cleanup completed: removed {expiredMuteCount} expired mutes, {staleMessageCount} stale message histories, {staleViolationCount} stale violations");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error during full cleanup: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private static string BuildMuteMessage(MuteInfo muteInfo)
        {
            if (muteInfo == null)
            {
                return "你已被禁言。";
            }

            var durationText = muteInfo.GetDurationDescription();
            var reasonText = muteInfo.Reason ?? "无";

            if (muteInfo.ExpiresAt.HasValue)
            {
                return $"你已被禁言 {durationText}，原因：{reasonText}";
            }

            return $"你已被永久禁言，原因：{reasonText}";
        }

        private static void RefreshProfanityCache()
        {
            try
            {
                if (_config == null)
                {
                    _profanityWordLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _forbiddenWordsLower = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _profanityRegexes = new List<Regex>();
                    return;
                }

                _profanityWordLookup = (_config.ProfanityWords ?? new List<string>())
                    .Where(w => !string.IsNullOrWhiteSpace(w))
                    .Select(w => w.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(w => w.ToLowerInvariant(), w => w, StringComparer.OrdinalIgnoreCase);

                // Pre-cache lowercase forbidden words for Issue #6
                _forbiddenWordsLower = new HashSet<string>(
                    _config.ForbiddenWords?.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.ToLowerInvariant()) ?? Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);

                _profanityRegexes = (_config.ProfanityPatterns ?? new List<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => new Regex(p.Trim(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled))
                    .ToList();

                if (ShouldLogDebug)
                {
                    Logger.Log($"🔁 Profanity cache refreshed: {_profanityWordLookup.Count} words, {_profanityRegexes.Count} patterns, {_forbiddenWordsLower.Count} forbidden words");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error refreshing profanity cache: {ex.Message}");
            }
        }

        private static ProfanityDetectionResult DetectProfanity(string message)
        {
            var result = new ProfanityDetectionResult();
            if (string.IsNullOrWhiteSpace(message))
            {
                return result;
            }

            // Get local references to avoid race conditions during iteration
            var wordLookup = _profanityWordLookup;
            var regexes = _profanityRegexes;

            // Optimization: Convert to lowercase once, then use word boundaries for matching
            if (wordLookup.Count > 0)
            {
                var lowered = message.ToLowerInvariant();

                // Use word tokenization for more efficient matching
                var words = lowered.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);

                foreach (var word in words)
                {
                    if (wordLookup.TryGetValue(word, out var originalWord))
                    {
                        result.MatchedWords.Add(originalWord);
                    }
                }
            }

            if (regexes.Count > 0)
            {
                foreach (var regex in regexes)
                {
                    if (regex.IsMatch(message))
                    {
                        result.MatchedPatterns.Add(regex);
                    }
                }
            }

            return result;
        }

        private static string ApplyProfanityMask(string message, ProfanityDetectionResult detection, string mask)
        {
            var sanitized = message;

            foreach (var word in detection.MatchedWords)
            {
                sanitized = Regex.Replace(sanitized, Regex.Escape(word), mask, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            foreach (var regex in detection.MatchedPatterns)
            {
                sanitized = regex.Replace(sanitized, mask);
            }

            return sanitized;
        }

        private static bool EnforceMinimumInterval(UnturnedPlayer player, DateTimeOffset now, ChatModerationConfig settings, ChatModerationResult result)
        {
            if (settings.MinimumSecondsBetweenMessages <= 0)
            {
                return false;
            }

            if (LastMessageTimes.TryGetValue(player.CSteamID, out var lastTime))
            {
                var diffSeconds = (now - lastTime).TotalSeconds;
                if (diffSeconds < settings.MinimumSecondsBetweenMessages)
                {
                    result.IsAllowed = false;
                    result.WasRateLimited = true;
                    result.DenyReason = settings.MinIntervalWarningMessage ?? "说话太快啦，请稍后再试。";
                    result.BlockReason = $"Rate limited: {player.DisplayName} sent messages {diffSeconds:F1}s apart";
                    UpdateLastMessageTime(player.CSteamID, now);
                    return true;
                }
            }

            return false;
        }

        private static bool TryHandleForbiddenWord(UnturnedPlayer player, string message, DateTimeOffset now, ChatModerationConfig settings, ChatModerationResult result)
        {
            var lowered = message.ToLowerInvariant();
            // Use pre-cached lowercase forbidden words to avoid O(n*m) allocations
            // Get local reference to avoid race condition during replacement
            var forbiddenWords = _forbiddenWordsLower;
            var matched = forbiddenWords.FirstOrDefault(w => lowered.Contains(w));

            if (matched == null)
            {
                return false;
            }

            result.IsAllowed = false;
            result.WasForbiddenWord = true;
            result.DenyReason = settings.ForbiddenWordWarningMessage ?? "消息包含违禁词，已被拦截。";
            result.BlockReason = $"Forbidden word '{matched}' detected from {player.DisplayName}";

            if (settings.AutoMuteOnForbiddenWord)
            {
                var durationSeconds = Math.Max(settings.AutoMuteDurationSeconds, 0);
                var duration = durationSeconds > 0 ? TimeSpan.FromSeconds(durationSeconds) : (TimeSpan?)null;
                var autoMute = ApplyAutoMute(player.CSteamID, player.DisplayName, duration, "使用违禁词");
                result.AppliedMute = autoMute;
                result.WasAutoMute = true;
                result.DenyReason = BuildMuteMessage(autoMute);
            }

            UpdateLastMessageTime(player.CSteamID, now);
            return true;
        }

        private static bool TryHandleProfanity(UnturnedPlayer player, string originalMessage, DateTimeOffset now, ChatModerationConfig settings, ChatModerationResult result, ref string sanitizedMessage)
        {
            sanitizedMessage = originalMessage;
            var detection = DetectProfanity(originalMessage);
            if (!detection.HasMatch)
            {
                return false;
            }

            result.WasProfanityDetected = true;
            var summary = detection.GetSummary();

            if (settings.ReplaceProfanityWithMask)
            {
                var mask = string.IsNullOrWhiteSpace(settings.ProfanityMask) ? "***" : settings.ProfanityMask;
                var masked = ApplyProfanityMask(originalMessage, detection, mask);
                sanitizedMessage = masked;

                if (!string.Equals(masked, originalMessage, StringComparison.Ordinal))
                {
                    result.SanitizedMessage = masked;
                    result.WasProfanityMasked = true;
                    if (ShouldLogDebug)
                    {
                        result.ModerationLog = $"Profanity masked for {player.DisplayName}: {summary}";
                    }
                }

                return false;
            }

            result.IsAllowed = false;
            result.DenyReason = settings.ProfanityWarningMessage ?? "消息包含不当言辞，已被拦截。";
            result.BlockReason = $"Profanity detected for {player.DisplayName}: {summary}";

            if (settings.AutoMuteOnProfanity)
            {
                var seconds = Math.Max(settings.AutoMuteProfanitySeconds, 0);
                var duration = seconds > 0 ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
                var autoMute = ApplyAutoMute(player.CSteamID, player.DisplayName, duration, "使用不当言辞");
                result.AppliedMute = autoMute;
                result.WasAutoMute = true;
                result.DenyReason = BuildMuteMessage(autoMute);
            }

            UpdateLastMessageTime(player.CSteamID, now);
            return true;
        }

        private static bool TryHandleSpamViolation(UnturnedPlayer player, string message, DateTimeOffset now, ChatModerationConfig settings, ChatModerationResult result)
        {
            if (!settings.EnableSpamDetection || settings.SpamMessageLimit <= 0 || settings.SpamIntervalSeconds <= 0)
            {
                // 即使禁用了区间检测，也可以仅启用"每秒上限"检查
                if (settings.MaxMessagesPerSecond <= 0)
                {
                    return false;
                }
            }

            int recentCount = 0;
            int oneSecondCount = 0;
            try
            {
                if (!MessageTimestamps.TryGetValue(player.CSteamID, out var timestamps))
                {
                    return false;
                }

                // Single loop to count both conditions instead of two LINQ Count() calls
                lock (timestamps)
                {
                    foreach (var t in timestamps)
                    {
                        var seconds = (now - t).TotalSeconds;
                        if (seconds <= settings.SpamIntervalSeconds)
                            recentCount++;
                        if (settings.MaxMessagesPerSecond > 0 && seconds <= 1)
                            oneSecondCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error handling spam violation: {ex.Message}");
                return false;
            }

            var exceedsWindowLimit = recentCount > settings.SpamMessageLimit;
            var exceedsPerSecond = settings.MaxMessagesPerSecond > 0 && oneSecondCount > settings.MaxMessagesPerSecond;

            if (!exceedsWindowLimit && !exceedsPerSecond)
            {
                return false;
            }

            result.IsAllowed = false;
            result.WasSpamDetected = true;
            result.DenyReason = settings.SpamWarningMessage ?? "你说话太频繁了，稍后再试。";
            if (exceedsPerSecond)
            {
                result.BlockReason = $"Spam detected (per second) for {player.DisplayName}: {oneSecondCount}/s";
            }
            else
            {
                result.BlockReason = $"Spam detected for {player.DisplayName}: {recentCount} messages in {settings.SpamIntervalSeconds}s";
            }

            if (settings.AutoMuteDurationSeconds > 0)
            {
                // 使用递增惩罚系统
                var muteDuration = GetEscalatedMuteDuration(player.CSteamID, settings);
                var autoMute = ApplyAutoMute(player.CSteamID, player.DisplayName, muteDuration, "聊天刷屏");
                result.AppliedMute = autoMute;
                result.WasAutoMute = true;
                result.DenyReason = BuildMuteMessage(autoMute);

                // Broadcast spam mute to all players if enabled
                if (settings.BroadcastSpamMutes)
                {
                    BroadcastMuteToAll(player.DisplayName, autoMute);
                }
            }

            UpdateLastMessageTime(player.CSteamID, now);
            return true;
        }

        private static void UpdateLastMessageTime(CSteamID steamId, DateTimeOffset timestamp)
        {
            try
            {
                LastMessageTimes[steamId] = timestamp;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error updating last message time: {ex.Message}");
            }
        }

        private static MuteInfo ApplyAutoMute(CSteamID steamId, string playerName, TimeSpan? duration, string reason)
        {
            var muteInfo = MutePlayer(steamId, playerName, duration, reason, "系统", true);
            if ((_config?.EnableModeration ?? false) && ShouldLogDebug)
            {
                Logger.Log($"🔇 Auto mute applied to {playerName} for {reason} ({muteInfo.GetDurationDescription()})");
            }
            return muteInfo;
        }

        private static void BroadcastMuteToAll(string playerName, MuteInfo muteInfo)
        {
            if (muteInfo == null) return;

            var durationText = muteInfo.GetDurationDescription();

            Rocket.Core.Utils.TaskDispatcher.QueueOnMainThread(() =>
            {
                try
                {
                    // 使用富文本标签实现彩色文字（与 /say 命令一致）
                    var msg = $"<color=#FFD700>玩家 {playerName} 因 {muteInfo.Reason} 被禁言 {durationText}</color>";

                    ChatManager.serverSendMessage(
                        msg,
                        UnityEngine.Color.white,
                        null,
                        null,
                        EChatMode.GLOBAL,
                        null,
                        true // 启用富文本
                    );
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error broadcasting mute message: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 检测重复字符刷屏（如"aaaaaaa"或"!!!!!!!"）
        /// </summary>
        private static bool TryHandleRepeatedCharacters(UnturnedPlayer player, string message, ChatModerationConfig settings, ChatModerationResult result)
        {
            if (string.IsNullOrWhiteSpace(message) || settings.MaxRepeatedCharacters <= 0)
            {
                return false;
            }

            var maxRepeated = 0;
            var currentChar = '\0';
            var currentCount = 0;

            foreach (var ch in message)
            {
                if (ch == currentChar)
                {
                    currentCount++;
                    if (currentCount > maxRepeated)
                    {
                        maxRepeated = currentCount;
                    }
                }
                else
                {
                    currentChar = ch;
                    currentCount = 1;
                }
            }

            if (maxRepeated > settings.MaxRepeatedCharacters)
            {
                result.IsAllowed = false;
                result.WasSpamDetected = true;
                result.DenyReason = "消息包含过多重复字符，已被拦截。";
                result.BlockReason = $"Repeated character spam from {player.DisplayName}: {maxRepeated} consecutive '{currentChar}'";

                if (settings.AutoMuteDurationSeconds > 0)
                {
                    var muteDuration = GetEscalatedMuteDuration(player.CSteamID, settings);
                    var autoMute = ApplyAutoMute(player.CSteamID, player.DisplayName, muteDuration, "重复字符刷屏");
                    result.AppliedMute = autoMute;
                    result.WasAutoMute = true;
                    result.DenyReason = BuildMuteMessage(autoMute);
                }

                return true;
            }

            return false;
        }




        /// <summary>
        /// 检测单人相似消息刷屏（仅检测同一玩家的消息相似度）
        /// </summary>
        private static bool TryHandleSimilarMessages(UnturnedPlayer player, string message, DateTimeOffset now, ChatModerationConfig settings, ChatModerationResult result)
        {
            if (settings.SimilarMessageThreshold <= 0 || settings.SimilarityPercentage <= 0)
            {
                return false;
            }

            int similarCount = 0;

            try
            {
                if (!RecentMessages.TryGetValue(player.CSteamID, out var recentMsgs))
                {
                    return false;
                }

                // Lock the list for thread-safe iteration
                lock (recentMsgs)
                {
                    // 计算与该玩家最近消息的相似度
                    foreach (var recentMsg in recentMsgs)
                    {
                        var similarity = CalculateSimilarity(message, recentMsg);
                        if (similarity >= settings.SimilarityPercentage)
                        {
                            similarCount++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error handling similar messages: {ex.Message}");
                return false;
            }

            if (similarCount >= settings.SimilarMessageThreshold)
            {
                result.IsAllowed = false;
                result.WasSpamDetected = true;
                result.DenyReason = "检测到相似消息刷屏，已被拦截。";
                result.BlockReason = $"Similar message spam from {player.DisplayName}: {similarCount} similar messages";

                if (settings.AutoMuteDurationSeconds > 0)
                {
                    var muteDuration = GetEscalatedMuteDuration(player.CSteamID, settings);
                    var autoMute = ApplyAutoMute(player.CSteamID, player.DisplayName, muteDuration, "相似消息刷屏");
                    result.AppliedMute = autoMute;
                    result.WasAutoMute = true;
                    result.DenyReason = BuildMuteMessage(autoMute);
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// 记录最近的消息用于相似度检测（仅记录单个玩家的消息）
        /// 使用时间窗口和数量限制双重机制防止无限增长
        /// </summary>
        private static void RecordRecentMessage(CSteamID steamId, string message, DateTimeOffset timestamp, ChatModerationConfig settings)
        {
            if (!settings.EnableSimilarMessageDetection || settings.SimilarMessageTimeWindowSeconds <= 0)
            {
                return;
            }

            try
            {
                var messages = RecentMessages.GetOrAdd(steamId, _ => new List<string>());

                lock (messages)
                {
                    messages.Add(message);

                    // 双重清理机制：
                    // 1. 基于时间窗口的清理（虽然我们只存储消息文本，但限制数量）
                    // 2. 基于数量的清理 - 保持合理的历史记录大小
                    var maxMessages = Math.Max(settings.SimilarMessageThreshold * SimilarMessageHistoryMultiplier, MinimumMessageHistorySize);
                    if (messages.Count > maxMessages)
                    {
                        // 移除最旧的消息
                        messages.RemoveRange(0, messages.Count - maxMessages);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error recording recent message: {ex.Message}");
            }
        }

        /// <summary>
        /// 计算两个字符串的相似度（使用Levenshtein距离）
        /// </summary>
        private static double CalculateSimilarity(string str1, string str2)
        {
            if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2))
            {
                return 0;
            }

            // 快速路径：完全相同
            if (str1 == str2)
                return 1.0;

            // 快速路径：长度差异过大 - 不相似
            if (Math.Abs(str1.Length - str2.Length) > Math.Max(str1.Length, str2.Length) * SimilarityLengthThreshold)
                return 0;

            // 快速路径：长度过长，跳过计算以节省性能
            if (str1.Length > SimilarityMaxLength || str2.Length > SimilarityMaxLength)
            {
                // 对于长消息，只比较前缀
                var prefix1 = str1.Substring(0, Math.Min(SimilarityMaxLength, str1.Length));
                var prefix2 = str2.Substring(0, Math.Min(SimilarityMaxLength, str2.Length));
                str1 = prefix1;
                str2 = prefix2;
            }

            // 预先转换为小写
            var lower1 = str1.ToLowerInvariant();
            var lower2 = str2.ToLowerInvariant();

            // 快速路径：小写后相同
            if (lower1 == lower2)
                return 1.0;

            var distance = LevenshteinDistance(lower1, lower2);
            var maxLen = Math.Max(lower1.Length, lower2.Length);

            return 1.0 - (double)distance / maxLen;
        }

        /// <summary>
        /// 计算Levenshtein距离 - 优化版本，使用O(min(n,m))空间和ArrayPool减少GC压力
        /// </summary>
        private static int LevenshteinDistance(string s1, string s2)
        {
            var len1 = s1.Length;
            var len2 = s2.Length;

            // 确保s1是较短的字符串以优化空间
            if (len1 > len2)
            {
                var temp = s1;
                s1 = s2;
                s2 = temp;
                var tempLen = len1;
                len1 = len2;
                len2 = tempLen;
            }

            // Use ArrayPool to reduce GC pressure
            var prevRow = ArrayPool<int>.Shared.Rent(len1 + 1);
            var currRow = ArrayPool<int>.Shared.Rent(len1 + 1);

            try
            {
                // 初始化第一行
                for (var i = 0; i <= len1; i++)
                {
                    prevRow[i] = i;
                }

                // 逐行计算
                for (var j = 1; j <= len2; j++)
                {
                    currRow[0] = j;

                    for (var i = 1; i <= len1; i++)
                    {
                        var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                        currRow[i] = Math.Min(
                            Math.Min(prevRow[i] + 1, currRow[i - 1] + 1),
                            prevRow[i - 1] + cost
                        );
                    }

                    // 交换行
                    var swap = prevRow;
                    prevRow = currRow;
                    currRow = swap;
                }

                return prevRow[len1];
            }
            finally
            {
                ArrayPool<int>.Shared.Return(prevRow);
                ArrayPool<int>.Shared.Return(currRow);
            }
        }
        private static TimeSpan? GetEscalatedMuteDuration(CSteamID steamId, ChatModerationConfig settings)
        {
            if (!settings.EnableEscalatingPenalties || settings.MuteDurationEscalationSeconds == null || settings.MuteDurationEscalationSeconds.Count == 0)
            {
                return settings.AutoMuteDurationSeconds > 0 ? TimeSpan.FromSeconds(settings.AutoMuteDurationSeconds) : (TimeSpan?)null;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                var resetHours = settings.ViolationResetHours > 0 ? settings.ViolationResetHours : 24;
                var resetThreshold = now.AddHours(-resetHours);

                var record = ViolationRecords.GetOrAdd(steamId, _ => new ViolationRecord
                {
                    ViolationCount = 0,
                    FirstViolation = now,
                    LastViolation = now
                });

                lock (record)
                {
                    if (record.FirstViolation < resetThreshold)
                    {
                        // 重置违规记录
                        record.ViolationCount = 1;
                        record.FirstViolation = now;
                        record.LastViolation = now;
                    }
                    else
                    {
                        record.ViolationCount++;
                        record.LastViolation = now;
                    }

                    // 获取对应的禁言时长
                    var escalationIndex = Math.Min(record.ViolationCount - 1, settings.MuteDurationEscalationSeconds.Count - 1);
                    var durationSeconds = settings.MuteDurationEscalationSeconds[escalationIndex];

                    if (ShouldLogDebug)
                    {
                        Logger.Log($"🔇 Escalated mute: violation #{record.ViolationCount}, duration: {durationSeconds}s");
                    }

                    return durationSeconds > 0 ? TimeSpan.FromSeconds(durationSeconds) : (TimeSpan?)null;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error getting escalated mute duration: {ex.Message}");
                return settings.AutoMuteDurationSeconds > 0 ? TimeSpan.FromSeconds(settings.AutoMuteDurationSeconds) : (TimeSpan?)null;
            }
        }
    }

    internal class ChatModerationResult
    {
        public bool IsAllowed { get; set; }
        public bool WasAutoMute { get; set; }
        public bool WasSpamDetected { get; set; }
        public bool WasRateLimited { get; set; }
        public bool WasForbiddenWord { get; set; }
        public bool WasProfanityDetected { get; set; }
        public bool WasProfanityMasked { get; set; }
        public string DenyReason { get; set; }
        public string BlockReason { get; set; }
        public MuteInfo AppliedMute { get; set; }
        public string SanitizedMessage { get; set; }
        public string ModerationLog { get; set; }
    }

    internal class MuteInfo
    {
        public CSteamID SteamId { get; set; }
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
                return "永久";
            }

            var remaining = ExpiresAt.Value - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return "即将结束";
            }

            if (remaining.TotalHours >= 1)
            {
                return $"{Math.Ceiling(remaining.TotalHours)} 小时";
            }

            if (remaining.TotalMinutes >= 1)
            {
                return $"{Math.Ceiling(remaining.TotalMinutes)} 分钟";
            }

            return $"{Math.Ceiling(remaining.TotalSeconds)} 秒";
        }
    }

    internal class ProfanityDetectionResult
    {
        public HashSet<string> MatchedWords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public List<Regex> MatchedPatterns { get; } = new List<Regex>();

        public bool HasMatch => MatchedWords.Count > 0 || MatchedPatterns.Count > 0;

        public string GetSummary()
        {
            var parts = new List<string>();
            if (MatchedWords.Count > 0)
            {
                parts.Add($"words: {string.Join(", ", MatchedWords)}");
            }

            if (MatchedPatterns.Count > 0)
            {
                parts.Add($"patterns: {string.Join(", ", MatchedPatterns.Select(r => r.ToString()))}");
            }

            return parts.Count > 0 ? string.Join("; ", parts) : "unknown";
        }
    }

    // 违规记录类（用于递增惩罚）
    internal class ViolationRecord
    {
        public int ViolationCount { get; set; }
        public DateTimeOffset LastViolation { get; set; }
        public DateTimeOffset FirstViolation { get; set; }
    }
}

