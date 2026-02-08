using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rocket.Core.Logging;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using Emqo.KookBot_Unturned.Interfaces;
using Emqo.KookBot_Unturned.Detectors;

namespace Emqo.KookBot_Unturned
{
    internal static class ChatModerationManager
    {
        // Thread-safe concurrent dictionaries - no semaphore needed
        private static readonly ConcurrentDictionary<CSteamID, MuteInfo> MuteMap = new();
        private static CancellationTokenSource _cleanupCancellationTokenSource;
        private static Task _cleanupTask;

        // Cleanup throttling
        private static DateTimeOffset _lastCleanupTime = DateTimeOffset.MinValue;
        private const int CleanupIntervalSeconds = 60;

        private static ChatModerationConfig _config;
        private static IConfigurationProvider _configProvider;

        // Message detectors
        private static readonly List<IMessageDetector> _detectors = new();
        private static readonly object _detectorsLock = new();

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

            // Register default detectors
            RegisterDefaultDetectors();

            // Start cleanup task for expired mutes
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

            // Update all detectors with new config
            lock (_detectorsLock)
            {
                foreach (var detector in _detectors)
                {
                    try
                    {
                        detector.UpdateConfig(_config);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Error updating detector {detector.Name}: {ex.Message}");
                    }
                }
            }
        }

        internal static void Shutdown()
        {
            try
            {
                _cleanupCancellationTokenSource?.Cancel();
                _cleanupCancellationTokenSource?.Dispose();

                if (_cleanupTask != null && !_cleanupTask.IsCompleted)
                {
                    try
                    {
                        _cleanupTask.Wait(TimeSpan.FromSeconds(1));
                    }
                    catch (AggregateException)
                    {
                        // Ignore cleanup task exceptions during shutdown
                    }
                }

                // Shutdown all detectors
                lock (_detectorsLock)
                {
                    foreach (var detector in _detectors)
                    {
                        try
                        {
                            detector.Shutdown();
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"Error shutting down detector {detector.Name}: {ex.Message}");
                        }
                    }
                    _detectors.Clear();
                }

                MuteMap.Clear();
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

            if (!settings.EnableModeration)
            {
                return result;
            }

            // Check if player is muted
            if (IsMuted(player.CSteamID, out var muteInfo))
            {
                result.IsAllowed = false;
                result.DenyReason = BuildMuteMessage(muteInfo);
                result.AppliedMute = muteInfo;
                return result;
            }

            // Run all detectors
            List<IMessageDetector> detectorsSnapshot;
            lock (_detectorsLock)
            {
                detectorsSnapshot = new List<IMessageDetector>(_detectors);
            }

            foreach (var detector in detectorsSnapshot)
            {
                if (!detector.IsEnabled)
                {
                    continue;
                }

                try
                {
                    var detectionResult = await detector.DetectAsync(player, message);
                    if (detectionResult.IsViolation)
                    {
                        result.IsAllowed = false;
                        result.DenyReason = detectionResult.Reason;
                        result.BlockReason = $"[{detectionResult.DetectorName}] {detectionResult.Reason}";

                        // Apply auto-mute if requested
                        if (detectionResult.ShouldAutoMute && detectionResult.AutoMuteDuration.HasValue)
                        {
                            var appliedMute = ApplyAutoMute(
                                player.CSteamID,
                                player.DisplayName ?? player.CharacterName ?? player.CSteamID.ToString(),
                                detectionResult.AutoMuteDuration,
                                detectionResult.Reason
                            );
                            result.WasAutoMute = true;
                            result.AppliedMute = appliedMute;

                            // Broadcast if enabled
                            if (settings.BroadcastSpamMutes)
                            {
                                BroadcastMuteToAll(player.DisplayName ?? player.CharacterName, appliedMute);
                            }
                        }

                        return result;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error in detector {detector.Name}: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// 同步检测消息是否违规（用于在聊天事件中同步拦截）
        /// </summary>
        internal static ChatModerationResult EvaluateMessageSync(UnturnedPlayer player, string message)
        {
            var result = new ChatModerationResult { IsAllowed = true };

            if (player == null || string.IsNullOrEmpty(message))
            {
                return result;
            }

            var settings = _config ?? ChatModerationConfig.CreateDefault();

            if (!settings.EnableModeration)
            {
                return result;
            }

            // Check if player is muted
            if (IsMuted(player.CSteamID, out var muteInfo))
            {
                result.IsAllowed = false;
                result.DenyReason = BuildMuteMessage(muteInfo);
                result.AppliedMute = muteInfo;
                return result;
            }

            // Run all detectors synchronously
            List<IMessageDetector> detectorsSnapshot;
            lock (_detectorsLock)
            {
                detectorsSnapshot = new List<IMessageDetector>(_detectors);
            }

            foreach (var detector in detectorsSnapshot)
            {
                if (!detector.IsEnabled)
                {
                    continue;
                }

                try
                {
                    var detectionResult = detector.DetectSync(player, message);
                    if (detectionResult.IsViolation)
                    {
                        result.IsAllowed = false;
                        result.DenyReason = detectionResult.Reason;
                        result.BlockReason = $"[{detectionResult.DetectorName}] {detectionResult.Reason}";

                        // Apply auto-mute if requested
                        if (detectionResult.ShouldAutoMute && detectionResult.AutoMuteDuration.HasValue)
                        {
                            var appliedMute = ApplyAutoMute(
                                player.CSteamID,
                                player.DisplayName ?? player.CharacterName ?? player.CSteamID.ToString(),
                                detectionResult.AutoMuteDuration,
                                detectionResult.Reason
                            );
                            result.WasAutoMute = true;
                            result.AppliedMute = appliedMute;

                            // Broadcast if enabled
                            if (settings.BroadcastSpamMutes)
                            {
                                BroadcastMuteToAll(player.DisplayName ?? player.CharacterName, appliedMute);
                            }
                        }

                        return result;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error in detector {detector.Name}: {ex.Message}");
                }
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
                Reason = string.IsNullOrWhiteSpace(reason) ? (isAuto ? "System auto-mute" : "Admin mute") : reason,
                MutedBy = string.IsNullOrWhiteSpace(mutedBy) ? "Unknown" : mutedBy,
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
                Logger.Log($"Player {playerName} muted by {info.MutedBy} ({info.GetDurationDescription()}) Reason: {info.Reason}");
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
                    Logger.Log($"Player {muteInfo.PlayerName} unmuted manually.");
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
                    Logger.Log($"Player {muteInfo.PlayerName} unmuted (SteamID match).");
                }
            }
        }

        internal static IReadOnlyCollection<MuteInfo> GetActiveMutes()
        {
            try
            {
                // Clean up expired mutes
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
        /// Clean up player data when they disconnect to prevent memory leaks.
        /// Note: MuteMap is NOT cleaned - mutes should persist even after disconnect.
        /// </summary>
        internal static void CleanupPlayerData(CSteamID steamId)
        {
            // Clean up detector data
            lock (_detectorsLock)
            {
                foreach (var detector in _detectors)
                {
                    try
                    {
                        detector.CleanupPlayerData(steamId);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Error cleaning up detector {detector.Name} data: {ex.Message}");
                    }
                }
            }
            // MuteMap intentionally persists across disconnects
        }

        #region Detector Management

        /// <summary>
        /// Register default detectors (RateLimitDetector, ForbiddenWordDetector).
        /// </summary>
        private static void RegisterDefaultDetectors()
        {
            RegisterDetector(new RateLimitDetector());
            RegisterDetector(new ForbiddenWordDetector());

            if (ShouldLogDebug)
            {
                Logger.Log($"Registered {_detectors.Count} default message detectors");
            }
        }

        /// <summary>
        /// Register a message detector.
        /// </summary>
        /// <param name="detector">The detector to register.</param>
        internal static void RegisterDetector(IMessageDetector detector)
        {
            if (detector == null)
            {
                return;
            }

            lock (_detectorsLock)
            {
                // Check if detector with same name already exists
                if (_detectors.Any(d => d.Name == detector.Name))
                {
                    Logger.LogWarning($"Detector {detector.Name} already registered, skipping");
                    return;
                }

                detector.Initialize(_config ?? ChatModerationConfig.CreateDefault());
                _detectors.Add(detector);

                if (ShouldLogDebug)
                {
                    Logger.Log($"Registered detector: {detector.Name}");
                }
            }
        }

        /// <summary>
        /// Unregister a message detector by name.
        /// </summary>
        /// <param name="name">The name of the detector to unregister.</param>
        /// <returns>True if the detector was found and removed.</returns>
        internal static bool UnregisterDetector(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            lock (_detectorsLock)
            {
                var detector = _detectors.FirstOrDefault(d => d.Name == name);
                if (detector == null)
                {
                    return false;
                }

                try
                {
                    detector.Shutdown();
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error shutting down detector {name}: {ex.Message}");
                }

                _detectors.Remove(detector);

                if (ShouldLogDebug)
                {
                    Logger.Log($"Unregistered detector: {name}");
                }

                return true;
            }
        }

        /// <summary>
        /// Get a list of registered detector names.
        /// </summary>
        internal static IReadOnlyList<string> GetRegisteredDetectorNames()
        {
            lock (_detectorsLock)
            {
                return _detectors.Select(d => d.Name).ToList();
            }
        }

        #endregion

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

        private static async Task StartPeriodicCleanupAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);

                        if (!cancellationToken.IsCancellationRequested)
                        {
                            await CleanupExpiredMutesAsync();
                        }
                    }
                    catch (OperationCanceledException)
                    {
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

        private static string BuildMuteMessage(MuteInfo muteInfo)
        {
            if (muteInfo == null)
            {
                return "You have been muted.";
            }

            var durationText = muteInfo.GetDurationDescription();
            var reasonText = muteInfo.Reason ?? "None";

            if (muteInfo.ExpiresAt.HasValue)
            {
                return $"You have been muted for {durationText}, reason: {reasonText}";
            }

            return $"You have been permanently muted, reason: {reasonText}";
        }

        internal static MuteInfo ApplyAutoMute(CSteamID steamId, string playerName, TimeSpan? duration, string reason)
        {
            var muteInfo = MutePlayer(steamId, playerName, duration, reason, "System", true);
            if ((_config?.EnableModeration ?? false) && ShouldLogDebug)
            {
                Logger.Log($"Auto mute applied to {playerName} for {reason} ({muteInfo.GetDurationDescription()})");
            }
            return muteInfo;
        }

        internal static void BroadcastMuteToAll(string playerName, MuteInfo muteInfo)
        {
            if (muteInfo == null) return;

            var durationText = muteInfo.GetDurationDescription();

            Rocket.Core.Utils.TaskDispatcher.QueueOnMainThread(() =>
            {
                try
                {
                    var msg = $"<color=#FFD700>Player {playerName} has been muted for {muteInfo.Reason} ({durationText})</color>";

                    ChatManager.serverSendMessage(
                        msg,
                        UnityEngine.Color.white,
                        null,
                        null,
                        EChatMode.GLOBAL,
                        null,
                        true
                    );
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error broadcasting mute message: {ex.Message}");
                }
            });
        }
    }

    internal class ChatModerationResult
    {
        public bool IsAllowed { get; set; }
        public bool WasAutoMute { get; set; }
        public string DenyReason { get; set; }
        public string BlockReason { get; set; }
        public MuteInfo AppliedMute { get; set; }
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
