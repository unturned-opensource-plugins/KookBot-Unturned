using System;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;
using Emqo.KookBot_Unturned.KookApi;
using Emqo.KookBot_Unturned.Interfaces;
using Emqo.KookBot_Unturned.Utilities;
using Rocket.Core.Logging;
using Rocket.Unturned.Player;
using Rocket.Unturned.Events;
using SDG.Unturned;
using Steamworks;
using Rocket.Unturned;
using System.Collections.Generic;
using System.Linq;
using Rocket.Unturned.Chat;

namespace Emqo.KookBot_Unturned
{
    public static partial class Events
    {

        private static volatile Message _message;
        private static volatile string _channelId;
        private static volatile bool _isEnabled = true;
        private static volatile IConfigurationProvider _configProvider;
        private static readonly string[] FilteredCommandKeywords =
        {
            "home", "tpa", "kit", "warp", "spawn", "back",
            "balance", "pay", "shop", "sell", "buy"
        };

        private const int DiagnosticsLogIntervalMs = 60000;
        private static long _lastDiagnosticsLogTicks;

        // 配置属性快捷访问
        private static KookBot_UnturnedConfiguration Config =>
            _configProvider?.GetConfiguration() ?? KookBot_UnturnedPlugin.Instance?.Configuration?.Instance;

        private static bool ShouldLogDebug => _configProvider?.IsDebugEnabled ?? Config?.Debug ?? false;

        /// <summary>
        /// Safe fire-and-forget wrapper that catches and logs exceptions
        /// </summary>
        private static async void SafeFireAndForget(Task task, string context = "")
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Unhandled exception in fire-and-forget task{(string.IsNullOrEmpty(context) ? "" : $" ({context})")}: {ex.Message}");
            }
        }

        /// <summary>
        /// 验证事件发送的前置条件
        /// </summary>
        private static bool ValidateEventPrerequisites()
        {
            if (!_isEnabled)
            {
                return false;
            }

            if (_message == null)
            {
                Logger.LogWarning("_message is null when trying to send event");
                return false;
            }

            if (string.IsNullOrEmpty(_channelId))
            {
                Logger.LogWarning("_channelId is null or empty when trying to send event");
                return false;
            }

            if (Config == null)
            {
                Logger.LogWarning("Config is null when trying to send event");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 检查事件是否启用 - 统一的配置检查方法
        /// </summary>
        private static bool CheckEventEnabled(string eventName)
        {
            return ValidateEventPrerequisites() && Config.IsGameToKookEnabled(eventName);
        }

        /// <summary>
        /// 检查事件是否应该被处理（去重）
        /// </summary>
        private static bool ShouldProcessEvent(string eventKey)
        {
            return EventDeduplicationStore.ShouldProcess(eventKey, ShouldLogDebug);
        }

        public static void Initialize(Message message, string channelId, IConfigurationProvider configProvider = null)
        {
            _message = message;
            _channelId = channelId;
            _configProvider = configProvider ?? new DefaultConfigurationProvider(() => KookBot_UnturnedPlugin.Instance?.Configuration?.Instance);
            ResetKookSendCancellation();
            ResetRuntimeState();

            // 注册所有事件
            RegisterEvents();
            if (ShouldLogDebug)
            {
                Logger.Log("🎯 Events system initialized and registered");
            }
        }

        public static void UpdateChannel(string channelId)
        {
            _channelId = string.IsNullOrWhiteSpace(channelId)
                ? _configProvider?.ChannelId ?? KookBot_UnturnedPlugin.Instance?.Configuration?.Instance?.ChannelId
                : channelId;

            if (ShouldLogDebug)
            {
                Logger.Log($"🔁 KOOK channel updated to {_channelId}");
            }
        }

        public static void Shutdown()
        {
            // 注销所有事件
            UnregisterEvents();
            CancelKookSends();
            ResetRuntimeState();
            _message = null;
            _channelId = null;
            if (ShouldLogDebug)
            {
                Logger.Log("🔇 Events system shutdown");
            }
        }

        private static bool _eventsRegistered = false;
        private static readonly object _eventsLock = new object();  // 保护 _eventsRegistered

        private static void RegisterEvents()
        {
            lock (_eventsLock)
            {
                if (_eventsRegistered)
                {
                    Logger.LogWarning("⚠️ Events already registered, skipping...");
                    return;
                }

                try
                {
                    // 玩家事件
                    U.Events.OnPlayerConnected += OnPlayerConnected;
                    U.Events.OnPlayerDisconnected += OnPlayerDisconnected;
                    UnturnedPlayerEvents.OnPlayerDeath += OnPlayerDeath;
                    UnturnedPlayerEvents.OnPlayerRevive += OnPlayerRevive;
                    UnturnedPlayerEvents.OnPlayerChatted += OnPlayerChatted;

                    // PvP 事件
                    DamageTool.damagePlayerRequested += OnPlayerDamaged;

                    _eventsRegistered = true;
                    Logger.Log("✅ Events registered successfully");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error registering events: {ex.Message}");
                    _eventsRegistered = false;
                }
            }
        }

        private static void UnregisterEvents()
        {
            lock (_eventsLock)
            {
                if (!_eventsRegistered)
                {
                    return;
                }

                try
                {
                    // 玩家事件
                    U.Events.OnPlayerConnected -= OnPlayerConnected;
                    U.Events.OnPlayerDisconnected -= OnPlayerDisconnected;
                    UnturnedPlayerEvents.OnPlayerDeath -= OnPlayerDeath;
                    UnturnedPlayerEvents.OnPlayerRevive -= OnPlayerRevive;
                    UnturnedPlayerEvents.OnPlayerChatted -= OnPlayerChatted;

                    // PvP 事件
                    DamageTool.damagePlayerRequested -= OnPlayerDamaged;

                    _eventsRegistered = false;
                    Logger.Log("✅ Events unregistered successfully");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error unregistering events: {ex.Message}");
                }
            }
        }
    }
}
