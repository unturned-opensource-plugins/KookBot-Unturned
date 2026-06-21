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
using Emqo.KookBot_Unturned.Utilities;

namespace Emqo.KookBot_Unturned
{
    internal static class ChatModerationManager
    {
        private static readonly MuteRegistry Mutes = new();
        private static CancellationTokenSource _cleanupCancellationTokenSource;
        private static Task _cleanupTask;
        private static readonly object _lifecycleLock = new();
        private static long _initializeGeneration;

        // Cleanup throttling
        private const int CleanupIntervalSeconds = 60;

        private static ChatModerationConfig _config;
        private static IConfigurationProvider _configProvider;

        private static readonly MessageDetectorRegistry Detectors = new();

        private static bool ShouldLogDebug => _configProvider?.IsDebugEnabled ?? KookBot_UnturnedPlugin.Instance?.Configuration?.Instance?.Debug ?? false;

        internal static void Initialize(KookBot_UnturnedConfiguration configuration, IConfigurationProvider configProvider = null)
        {
            lock (_lifecycleLock)
            {
                _configProvider = configProvider ?? new DefaultConfigurationProvider(() => KookBot_UnturnedPlugin.Instance?.Configuration?.Instance);

                if (configuration?.Moderation == null)
                {
                    configuration.Moderation = ChatModerationConfig.CreateDefault();
                }

                configuration.Moderation.ApplyDefaultsIfNeeded();
                _config = configuration.Moderation;

                StopCleanupLoopLocked();
                Detectors.ResetToDefaults(_config, ShouldLogDebug);
                StartCleanupLoopLocked();

                var generation = Interlocked.Increment(ref _initializeGeneration);
                if (ShouldLogDebug)
                {
                    Logger.Log($"Chat moderation initialized (generation {generation}, detectors: {Detectors.Names().Count})");
                }
            }
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

            Detectors.UpdateConfig(_config);
        }

        internal static void Shutdown()
        {
            try
            {
                lock (_lifecycleLock)
                {
                    StopCleanupLoopLocked();
                    Detectors.ShutdownAll();
                    Mutes.Clear();
                    _config = null;
                    _configProvider = null;

                    if (ShouldLogDebug)
                    {
                        Logger.Log("Chat moderation shutdown completed");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error during ChatModerationManager shutdown: {ex.Message}");
            }
        }

        /// <summary>
        /// 同步检测消息是否违规（用于在聊天事件中同步拦截）
        /// </summary>
        internal static ChatModerationResult EvaluateMessageSync(UnturnedPlayer player, string message)
        {
            return EvaluateMessageCore(player, message);
        }

        private static ChatModerationResult EvaluateMessageCore(UnturnedPlayer player, string message)
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
            foreach (var detector in Detectors.Snapshot())
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
                            if (settings.BroadcastAutoMutes)
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
            return Mutes.Mute(steamId, playerName, duration, reason, mutedBy, isAuto, ShouldLogDebug);
        }

        internal static bool TryUnmutePlayer(string playerNameOrId, out MuteInfo muteInfo)
        {
            return Mutes.TryUnmutePlayer(playerNameOrId, out muteInfo);
        }

        internal static bool UnmuteBySteamId(CSteamID steamId, out MuteInfo muteInfo)
        {
            return Mutes.UnmuteBySteamId(steamId, out muteInfo);
        }

        internal static IReadOnlyCollection<MuteInfo> GetActiveMutes()
        {
            return Mutes.GetActiveMutes();
        }

        internal static bool IsMuted(CSteamID steamId, out MuteInfo muteInfo)
        {
            return Mutes.IsMuted(steamId, out muteInfo);
        }

        /// <summary>
        /// Fast synchronous mute check for main thread use.
        /// Thread-safe with the mute registry.
        /// </summary>
        internal static bool IsMutedSync(CSteamID steamId)
        {
            return Mutes.IsMutedSync(steamId);
        }

        /// <summary>
        /// Clean up player data when they disconnect to prevent memory leaks.
        /// Note: mute records are NOT cleaned - mutes should persist even after disconnect.
        /// </summary>
        internal static void CleanupPlayerData(CSteamID steamId)
        {
            Detectors.CleanupPlayerData(steamId);
            // Mute records intentionally persist across disconnects.
        }

        #region Detector Management

        internal static void RegisterDetector(IMessageDetector detector)
        {
            Detectors.Register(detector, _config ?? ChatModerationConfig.CreateDefault(), ShouldLogDebug);
        }

        internal static bool UnregisterDetector(string name)
        {
            return Detectors.Unregister(name, ShouldLogDebug);
        }

        internal static IReadOnlyList<string> GetRegisteredDetectorNames()
        {
            return Detectors.Names();
        }

        #endregion

        private static void NotifyPlayerMute(UnturnedPlayer player, MuteInfo muteInfo)
        {
            if (player == null || muteInfo == null)
            {
                return;
            }

            if (!MainThreadDispatcherGuard.TryQueue(() =>
            {
                var message = BuildMuteMessage(muteInfo);
                UnturnedChat.Say(player, message, UnityEngine.Color.red);
            }))
            {
                Logger.LogWarning($"⚠️ Main thread dispatcher is saturated, skip mute notification for {player.DisplayName}");
            }
        }

        private static Task CleanupExpiredMutesAsync()
        {
            Mutes.CleanupExpired();
            return Task.CompletedTask;
        }

        private static async Task StartPeriodicCleanupAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (ShouldLogDebug)
                {
                    Logger.Log($"Chat moderation cleanup loop started (generation {Volatile.Read(ref _initializeGeneration)})");
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(CleanupIntervalSeconds), cancellationToken);

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
            finally
            {
                if (ShouldLogDebug)
                {
                    Logger.Log($"Chat moderation cleanup loop stopped (generation {Volatile.Read(ref _initializeGeneration)})");
                }
            }
        }

        private static void StartCleanupLoopLocked()
        {
            _cleanupCancellationTokenSource = new CancellationTokenSource();
            _cleanupTask = StartPeriodicCleanupAsync(_cleanupCancellationTokenSource.Token);
        }

        private static void StopCleanupLoopLocked()
        {
            var cancellationTokenSource = _cleanupCancellationTokenSource;
            var cleanupTask = _cleanupTask;

            _cleanupCancellationTokenSource = null;
            _cleanupTask = null;

            CleanupLoopShutdown.CancelWithoutBlocking(cancellationTokenSource, cleanupTask);
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

            if (!MainThreadDispatcherGuard.TryQueue(() =>
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
            }))
            {
                Logger.LogWarning($"⚠️ Main thread dispatcher is saturated, skip mute broadcast for {playerName}");
            }
        }
    }


}
