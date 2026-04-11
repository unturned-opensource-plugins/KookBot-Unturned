using System;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;
using Emqo.KookBot_Unturned.KookApi;
using Emqo.KookBot_Unturned.Interfaces;
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
    public class Events
    {
        private static volatile Message _message;
        private static volatile string _channelId;
        private static volatile bool _isEnabled = true;
        private static volatile IConfigurationProvider _configProvider;
        private static readonly string[] CommandPrefixes = { "/", "!", "@", ".", "#", "$" };
        private static readonly string[] FilteredCommandKeywords =
        {
            "home", "tpa", "kit", "warp", "spawn", "back",
            "balance", "pay", "shop", "sell", "buy"
        };

        // 去重缓存：记录最近的事件以防止重复（使用 ConcurrentDictionary 确保线程安全）
        private static readonly ConcurrentDictionary<string, DateTimeOffset> _eventDeduplicationCache = new();
        private const int DeduplicationWindowMs = 1000; // 1秒内的重复事件会被过滤
        private const int MaxCacheSize = 1000; // 最大缓存条目数，防止无限增长
        private const int CacheCleanupIntervalMs = 60000; // 每60秒清理一次过期缓存
        private static long _lastCacheCleanupTicks = 0;
        private const int MaxConcurrentKookSends = 4;
        private const int MaxPendingKookSends = 128;
        private static readonly SemaphoreSlim _kookSendSemaphore = new(MaxConcurrentKookSends, MaxConcurrentKookSends);
        private static int _pendingKookSendCount;
        private static CancellationTokenSource _kookSendCancellationTokenSource = new CancellationTokenSource();
        private const int DiagnosticsLogIntervalMs = 60000;
        private static long _lastDiagnosticsLogTicks;
        private static long _kookSendSuccessCount;
        private static long _kookSendCanceledCount;
        private static long _kookSendDroppedCount;
        private static long _pvpEventsReceivedCount;
        private static long _pvpEventsDisabledCount;
        private static long _pvpEventsInvalidPlayerCount;
        private static long _pvpEventsSelfDamageSkippedCount;
        private static long _pvpEventsLowDamageSkippedCount;
        private static long _pvpEventsQueuedCount;
        private static long _pvpEventsSentCount;
        private static long _pvpEventsFailedCount;
        private static long _pvpEventsBacklogDropCount;

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
            var now = DateTimeOffset.UtcNow;

            // 定期清理过期的缓存条目，防止无限增长
            var nowTicks = now.Ticks;
            if (nowTicks - Interlocked.Read(ref _lastCacheCleanupTicks) > CacheCleanupIntervalMs * 10000)
            {
                CleanupDeduplicationCache(now);
                Interlocked.Exchange(ref _lastCacheCleanupTicks, nowTicks);
            }

            if (_eventDeduplicationCache.TryGetValue(eventKey, out var lastTime))
            {
                var timeSinceLastEvent = (now - lastTime).TotalMilliseconds;
                if (timeSinceLastEvent < DeduplicationWindowMs)
                {
                    if (ShouldLogDebug)
                    {
                        Logger.LogWarning($"⚠️ Duplicate event detected: {eventKey} (within {DeduplicationWindowMs}ms), skipping...");
                    }
                    return false;
                }
            }

            _eventDeduplicationCache[eventKey] = now;
            return true;
        }

        /// <summary>
        /// 清理过期的去重缓存条目
        /// </summary>
        private static void CleanupDeduplicationCache(DateTimeOffset now)
        {
            try
            {
                // 使用 ConcurrentDictionary 的线程安全方法清理过期条目
                var expiredKeys = _eventDeduplicationCache
                    .Where(kvp => (now - kvp.Value).TotalMilliseconds > DeduplicationWindowMs * 10)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    _eventDeduplicationCache.TryRemove(key, out _);
                }

                // 如果缓存仍然过大，移除最旧的条目
                if (_eventDeduplicationCache.Count > MaxCacheSize)
                {
                    var keysToRemove = _eventDeduplicationCache
                        .OrderBy(kvp => kvp.Value)
                        .Take(_eventDeduplicationCache.Count - MaxCacheSize)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in keysToRemove)
                    {
                        _eventDeduplicationCache.TryRemove(key, out _);
                    }

                    if (ShouldLogDebug)
                    {
                        Logger.Log($"🧹 Deduplication cache cleaned: removed {keysToRemove.Count} old entries, current size: {_eventDeduplicationCache.Count}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error cleaning deduplication cache: {ex.Message}");
            }
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

        #region 玩家事件

        private static void OnPlayerConnected(UnturnedPlayer player)
        {
            // 检查事件是否启用
            if (!CheckEventEnabled("PlayerJoined"))
                return;

            // 去重检查
            var eventKey = $"PlayerJoined_{player.CSteamID}";
            if (!ShouldProcessEvent(eventKey))
                return;

            try
            {
                // 在同步上下文中获取所有需要的数据
                var playerName = GetPlayerName(player);
                var steamId = player.CSteamID.ToString();
                var currentPlayers = Provider.clients.Count;
                var maxPlayers = Provider.maxPlayers;

                // Fire and forget with exception handling
                SafeFireAndForget(OnPlayerConnectedAsync(playerName, steamId, currentPlayers, maxPlayers), "PlayerConnected");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in OnPlayerConnected: {ex.Message}");
            }
        }

        private static async Task OnPlayerConnectedAsync(string playerName, string steamId, int currentPlayers, int maxPlayers)
        {
            try
            {
                if (!ValidateEventPrerequisites()) return;

                var card = KookApi.KookCardFactory.BuildLifecycleCard(
                    "🟢",
                    "玩家加入服务器",
                    playerName,
                    steamId,
                    currentPlayers,
                    maxPlayers,
                    DateTimeOffset.Now,
                    "success");

                var sent = await SendBoundedKookMessageAsync(
                    async () => await _message.CreateMessageAsync(10, _channelId, card),
                    "PlayerConnected");
                if (sent && ShouldLogDebug)
                {
                    Logger.Log($"📤 Player join event sent to KOOK: {playerName}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error sending player connect event: {ex.Message}");
            }
        }

        private static void OnPlayerDisconnected(UnturnedPlayer player)
        {
            // 清理玩家数据，防止内存泄漏
            ChatModerationManager.CleanupPlayerData(player.CSteamID);

            // 检查事件是否启用
            if (!CheckEventEnabled("PlayerLeft"))
                return;

            // 去重检查
            var eventKey = $"PlayerLeft_{player.CSteamID}";
            if (!ShouldProcessEvent(eventKey))
                return;

            try
            {
                // 在同步上下文中获取所有需要的数据
                var playerName = GetPlayerName(player);
                var steamId = player.CSteamID.ToString();
                var currentPlayers = Provider.clients.Count - 1; // 减1因为玩家还没完全断开
                var maxPlayers = Provider.maxPlayers;

                // Fire and forget with exception handling
                SafeFireAndForget(OnPlayerDisconnectedAsync(playerName, steamId, currentPlayers, maxPlayers), "PlayerDisconnected");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in OnPlayerDisconnected: {ex.Message}");
            }
        }

        private static async Task OnPlayerDisconnectedAsync(string playerName, string steamId, int currentPlayers, int maxPlayers)
        {
            try
            {
                if (!ValidateEventPrerequisites()) return;

                var card = KookApi.KookCardFactory.BuildLifecycleCard(
                    "🔴",
                    "玩家离开服务器",
                    playerName,
                    steamId,
                    currentPlayers,
                    maxPlayers,
                    DateTimeOffset.Now,
                    "warning");

                var sent = await SendBoundedKookMessageAsync(
                    async () => await _message.CreateMessageAsync(10, _channelId, card),
                    "PlayerDisconnected");
                if (sent && ShouldLogDebug)
                {
                    Logger.Log($"📤 Player disconnect event sent to KOOK: {playerName}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error sending player disconnect event: {ex.Message}");
            }
        }

        private static void OnPlayerDeath(UnturnedPlayer player, EDeathCause cause, ELimb limb, CSteamID murderer)
        {
            try
            {
                // 检查事件是否启用
                if (!CheckEventEnabled("PlayerDeath"))
                {
                    if (ShouldLogDebug)
                    {
                        Logger.Log("⚠️ PlayerDeath event is disabled in config");
                    }
                    return;
                }

                // 去重检查
                var eventKey = $"PlayerDeath_{player.CSteamID}_{DateTimeOffset.UtcNow.Ticks / 10000000}"; // 按秒分组
                if (!ShouldProcessEvent(eventKey))
                {
                    if (ShouldLogDebug)
                    {
                        Logger.Log($"⚠️ PlayerDeath event filtered by deduplication: {eventKey}");
                    }
                    return;
                }

                if (ShouldLogDebug)
                {
                    Logger.Log($"🔍 PlayerDeath event triggered for {player.DisplayName}, cause: {cause}");
                }

                // Fire and forget with exception handling
                SafeFireAndForget(OnPlayerDeathAsync(player, cause, limb, murderer), "PlayerDeath");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in OnPlayerDeath: {ex.Message}");
            }
        }

        private static async Task OnPlayerDeathAsync(UnturnedPlayer player, EDeathCause cause, ELimb limb, CSteamID murderer)
        {
            try
            {
                if (ShouldLogDebug)
                {
                    Logger.Log($"🔍 OnPlayerDeathAsync started for {player?.DisplayName}");
                }

                // 添加玩家空值检查
                if (player == null)
                {
                    Logger.LogWarning("Player is null in OnPlayerDeath event");
                    return;
                }

                // 安全获取玩家名称
                string playerName = GetPlayerName(player);

                string killerName = null;

                // 如果是被其他玩家杀死
                if (murderer != CSteamID.Nil && murderer != player.CSteamID)
                {
                    killerName = GetKillerName(murderer);
                }

                // 添加更多空值检查
                if (_message == null)
                {
                    Logger.LogWarning("_message is null when trying to send death event");
                    return;
                }

                if (string.IsNullOrEmpty(_channelId))
                {
                    Logger.LogWarning("_channelId is null or empty when trying to send death event");
                    return;
                }

                if (ShouldLogDebug)
                {
                    Logger.Log($"🔍 Building death card for {playerName}, cause: {cause}");
                }

                var fields = new List<(string, string)>
                {
                    ("玩家", playerName),
                    ("死因", GetDeathCauseText(cause)),
                    ("部位", GetLimbText(limb))
                };

                if (!string.IsNullOrWhiteSpace(killerName))
                {
                    fields.Add(("凶手", killerName));
                }

                var deathCard = KookApi.KookCardFactory.BuildGenericEventCard("💀", "玩家死亡", fields, DateTimeOffset.Now, "danger");

                if (ShouldLogDebug)
                {
                    Logger.Log($"🔍 Sending death card to KOOK for {playerName}");
                }

                var sent = await SendBoundedKookMessageAsync(
                    async () => await _message.CreateMessageAsync(10, _channelId, deathCard),
                    "PlayerDeath");

                if (sent && ShouldLogDebug)
                {
                    Logger.Log($"📤 Player death event sent to KOOK: {playerName} - {cause}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error sending player death event: {ex.Message}");
                Logger.LogError($"Stack trace: {ex.StackTrace}");
            }
        }

        private static void OnPlayerRevive(UnturnedPlayer player, UnityEngine.Vector3 position, byte angle)
        {
            // 检查事件是否启用
            if (!CheckEventEnabled("PlayerRevive"))
                return;

            // 去重检查
            var eventKey = $"PlayerRevive_{player.CSteamID}_{DateTimeOffset.UtcNow.Ticks / 10000000}"; // 按秒分组
            if (!ShouldProcessEvent(eventKey))
                return;

            try
            {
                // 在同步上下文中获取所有需要的数据
                var playerName = GetPlayerName(player);
                var shouldExposeCoordinates = Config?.ExposeReviveCoordinates ?? false;

                // Fire and forget with exception handling
                SafeFireAndForget(OnPlayerReviveAsync(playerName, position, shouldExposeCoordinates), "PlayerRevive");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in OnPlayerRevive: {ex.Message}");
            }
        }

        private static async Task OnPlayerReviveAsync(string playerName, UnityEngine.Vector3 position, bool shouldExposeCoordinates)
        {
            try
            {
                if (!ValidateEventPrerequisites()) return;

                var reviveFields = new List<(string, string)>
                {
                    ("玩家", playerName)
                };
                // Security: Only expose coordinates if explicitly enabled in config
                if (shouldExposeCoordinates)
                {
                    reviveFields.Add(("坐标", $"{Math.Round(position.x, 1)}, {Math.Round(position.y, 1)}, {Math.Round(position.z, 1)}"));
                }

                var reviveCard = KookApi.KookCardFactory.BuildGenericEventCard("🔄", "玩家复活", reviveFields, DateTimeOffset.Now, "success");
                var sent = await SendBoundedKookMessageAsync(
                    async () => await _message.CreateMessageAsync(10, _channelId, reviveCard),
                    "PlayerRevive");
                if (sent && ShouldLogDebug)
                {
                    Logger.Log($"📤 Player revive event sent to KOOK: {playerName}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error sending player revive event: {ex.Message}");
            }
        }

        private static void OnPlayerChatted(UnturnedPlayer player, ref UnityEngine.Color color, string message, EChatMode chatMode, ref bool cancel)
        {
            try
            {
                // 同步检测消息是否违规（禁言、违禁词、速率限制等）
                var moderationResult = ChatModerationManager.EvaluateMessageSync(player, message);
                if (!moderationResult.IsAllowed)
                {
                    cancel = true;

                    // 通知玩家
                    if (!string.IsNullOrEmpty(moderationResult.DenyReason))
                    {
                        UnturnedChat.Say(player, moderationResult.DenyReason, UnityEngine.Color.red);
                    }

                    if (ShouldLogDebug)
                    {
                        Logger.Log($"🚫 Message blocked: {GetPlayerName(player)} - {moderationResult.BlockReason}");
                    }
                    return;
                }

                // Get player name in sync context
                var playerName = GetPlayerName(player);

                // Async forward to Kook (non-blocking)
                SafeFireAndForget(ForwardChatToKookAsync(player, playerName, message, chatMode), "ChatForward");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in OnPlayerChatted: {ex.Message}");
                cancel = true;
            }
        }

        private static async Task ForwardChatToKookAsync(UnturnedPlayer player, string playerName, string message, EChatMode chatMode)
        {
            try
            {
                // Check if event is enabled
                if (!_isEnabled || _message == null || Config == null || !Config.IsGameToKookEnabled("ChatMessage"))
                {
                    return;
                }

                await SendChatMessageAsync(playerName, message, chatMode, isFiltered: false);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error forwarding chat to Kook: {ex.Message}");
            }
        }

        private static async Task SendAutoMuteNotificationAsync(string playerName, MuteInfo appliedMute)
        {
            try
            {
                if (!ValidateEventPrerequisites()) return;

                var durationText = appliedMute.GetDurationDescription();

                var muteFields = new List<(string, string)>
                {
                    ("玩家", playerName),
                    ("原因", appliedMute.Reason),
                    ("时长", durationText)
                };

                var muteCard = KookApi.KookCardFactory.BuildGenericEventCard("🚫", "自动禁言", muteFields, DateTimeOffset.Now, "warning");
                await SendBoundedKookMessageAsync(
                    async () => await _message.CreateMessageAsync(10, _channelId, muteCard),
                    "AutoMuteNotification");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error notifying KOOK about auto mute: {ex.Message}");
            }
        }

        // 优化：预定义命令前缀字符集，用于快速检查
        private static readonly HashSet<char> CommandPrefixChars = new() { '/', '!', '@', '.', '#', '$' };

        private static async Task SendChatMessageAsync(string playerName, string processedMessage, EChatMode chatMode, bool isFiltered = false)
        {
            try
            {
                if (!ValidateEventPrerequisites()) return;
                if (chatMode != EChatMode.GLOBAL && chatMode != EChatMode.LOCAL) return;

                // 过滤指令信息 - 检查消息是否以常见指令前缀开头
                if (!isFiltered && !string.IsNullOrEmpty(processedMessage))
                {
                    // 优化：使用单个字符检查而不是多次 StartsWith 调用
                    if (processedMessage.Length > 0 && CommandPrefixChars.Contains(processedMessage[0]))
                    {
                        if (ShouldLogDebug)
                        {
                            Logger.Log($"🚫 Command message filtered, not sent to KOOK: {processedMessage}");
                        }
                        return;
                    }

                    // 检查特定指令（根据你的服务器指令调整）
                    string lowerMessage = processedMessage.ToLower().Trim();
                    foreach (var command in FilteredCommandKeywords)
                    {
                        // 优化: 避免在循环中创建新字符串
                        if (lowerMessage == command || lowerMessage.StartsWith(command + " "))
                        {
                            if (ShouldLogDebug)
                            {
                                Logger.Log($"🚫 Command message filtered, not sent to KOOK: {processedMessage}");
                            }
                            return;
                        }
                    }
                }

                var chatModeText = chatMode == EChatMode.GLOBAL ? "全局" : "区域";
                var statusTag = isFiltered ? " 🚫" : "";
                var card = KookApi.KookCardFactory.BuildChatMessageCard(chatModeText, playerName, processedMessage, DateTimeOffset.Now, statusTag);

                var sent = await SendBoundedKookMessageAsync(
                    async () => await _message.CreateMessageAsync(10, _channelId, card),
                    "ChatMessage");
                if (sent && ShouldLogDebug)
                {
                    Logger.Log($"📤 Chat message sent to KOOK: [{chatMode}] {playerName}: {processedMessage}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error sending chat event: {ex.Message}");
            }
        }

        #endregion

        #region PvP 事件

        private static void OnPlayerDamaged(ref DamagePlayerParameters parameters, ref bool shouldAllow)
        {
            Interlocked.Increment(ref _pvpEventsReceivedCount);

            // 检查事件是否启用
            if (!shouldAllow || !CheckEventEnabled("PlayerDamaged"))
            {
                Interlocked.Increment(ref _pvpEventsDisabledCount);
                MaybeLogDiagnostics("PlayerDamaged:disabled");
                return;
            }

            try
            {
                var copiedParameters = parameters; // 复制结构体

                // 在同步上下文中提前获取所有玩家数据，避免异步执行时玩家对象被销毁
                if (copiedParameters.player?.channel?.owner?.playerID == null)
                {
                    Interlocked.Increment(ref _pvpEventsInvalidPlayerCount);
                    MaybeLogDiagnostics("PlayerDamaged:invalid-player");
                    return;
                }

                var victim = UnturnedPlayer.FromCSteamID(copiedParameters.player.channel.owner.playerID.steamID);
                var attacker = UnturnedPlayer.FromCSteamID(copiedParameters.killer);

                if (victim == null || attacker == null)
                {
                    Interlocked.Increment(ref _pvpEventsInvalidPlayerCount);
                    MaybeLogDiagnostics("PlayerDamaged:lookup-null");
                    return;
                }

                if (victim.CSteamID == attacker.CSteamID)
                {
                    Interlocked.Increment(ref _pvpEventsSelfDamageSkippedCount);
                    MaybeLogDiagnostics("PlayerDamaged:self");
                    return;
                }

                if (copiedParameters.damage < 50 && victim.Health > copiedParameters.damage)
                {
                    Interlocked.Increment(ref _pvpEventsLowDamageSkippedCount);
                    MaybeLogDiagnostics("PlayerDamaged:low-damage");
                    return;
                }

                // 在同步上下文中获取所有需要的数据
                var victimName = GetPlayerName(victim);
                var attackerName = GetPlayerName(attacker);
                var victimHealth = victim.Health;
                var limbText = GetLimbText(copiedParameters.limb);

                // Fire and forget with exception handling
                Interlocked.Increment(ref _pvpEventsQueuedCount);
                SafeFireAndForget(OnPlayerDamagedAsync(copiedParameters, victimName, attackerName, victimHealth, limbText), "PlayerDamaged");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _pvpEventsFailedCount);
                Logger.LogError($"Error in OnPlayerDamaged: {ex.Message}");
                MaybeLogDiagnostics("PlayerDamaged:error");
            }
        }

        private static async Task OnPlayerDamagedAsync(DamagePlayerParameters copiedParameters, string victimName, string attackerName, float victimHealth, string limbText)
        {
            try
            {
                if (copiedParameters.killer == CSteamID.Nil) return;

                // Validate prerequisites
                if (!ValidateEventPrerequisites()) return;

                var pvpFields = new List<(string, string)>
                {
                    ("攻击者", attackerName),
                    ("受害者", victimName),
                    ("伤害", $"{copiedParameters.damage}"),
                    ("部位", limbText)
                };

                if (victimHealth <= copiedParameters.damage)
                {
                    pvpFields.Add(("结果", "致命一击"));
                }

                var pvpCard = KookApi.KookCardFactory.BuildGenericEventCard("⚔️", "PvP 事件", pvpFields, DateTimeOffset.Now, "warning");
                var sent = await SendBoundedKookMessageAsync(
                    async () => await _message.CreateMessageAsync(10, _channelId, pvpCard),
                    "PlayerDamaged");
                if (sent && ShouldLogDebug)
                {
                    Logger.Log($"📤 PvP event sent to KOOK: {attackerName} -> {victimName} ({copiedParameters.damage} damage)");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _pvpEventsFailedCount);
                Logger.LogError($"Error sending PvP event: {ex.Message}");
                if (ShouldLogDebug)
                {
                    Logger.LogError($"Stack trace: {ex.StackTrace}");
                }

                MaybeLogDiagnostics("PlayerDamaged:send-error");
            }
        }

        #endregion

        #region 辅助方法

        private static string GetDeathCauseText(EDeathCause cause)
        {
            return cause switch
            {
                EDeathCause.BLEEDING => "失血过多",
                EDeathCause.BONES => "骨折致死",
                EDeathCause.FOOD => "饥饿",
                EDeathCause.WATER => "脱水",
                EDeathCause.GUN => "枪击",
                EDeathCause.MELEE => "近战武器",
                EDeathCause.ZOMBIE => "僵尸攻击",
                EDeathCause.ANIMAL => "动物攻击",
                EDeathCause.SUICIDE => "自杀",
                EDeathCause.KILL => "被管理员处决",
                EDeathCause.INFECTION => "感染",
                EDeathCause.PUNCH => "拳击",
                EDeathCause.BREATH => "窒息",
                EDeathCause.ROADKILL => "车祸",
                EDeathCause.VEHICLE => "载具事故",
                EDeathCause.GRENADE => "手榴弹",
                EDeathCause.BURNING => "燃烧",
                EDeathCause.FREEZING => "冻死",
                EDeathCause.SENTRY => "哨戒炮",
                EDeathCause.ACID => "酸液",
                EDeathCause.BOULDER => "巨石",
                EDeathCause.BURNER => "燃烧器",
                EDeathCause.SPIT => "酸液攻击",
                EDeathCause.CHARGE => "冲撞",
                EDeathCause.SPLASH => "溅射伤害",
                EDeathCause.LANDMINE => "地雷",
                EDeathCause.ARENA => "竞技场",
                _ => cause.ToString()
            };
        }

        private static string GetLimbText(ELimb limb)
        {
            return limb switch
            {
                ELimb.LEFT_ARM => "左臂",
                ELimb.LEFT_HAND => "左手",
                ELimb.LEFT_LEG => "左腿",
                ELimb.LEFT_FOOT => "左脚",
                ELimb.RIGHT_ARM => "右臂",
                ELimb.RIGHT_HAND => "右手",
                ELimb.RIGHT_LEG => "右腿",
                ELimb.RIGHT_FOOT => "右脚",
                ELimb.LEFT_BACK => "左后背",
                ELimb.LEFT_FRONT => "左前胸",
                ELimb.RIGHT_BACK => "右后背",
                ELimb.RIGHT_FRONT => "右前胸",
                ELimb.SPINE => "脊椎",
                ELimb.SKULL => "头部",
                _ => limb.ToString()
            };
        }

        /// <summary>
        /// 安全获取玩家名称 - 从多个来源尝试获取
        /// </summary>
        private static string GetPlayerName(UnturnedPlayer player)
        {
            if (player == null)
                return "Unknown Player";

            try
            {
                if (!string.IsNullOrEmpty(player.CharacterName))
                    return player.CharacterName;
                if (!string.IsNullOrEmpty(player.DisplayName))
                    return player.DisplayName;
                if (player.SteamName != null)
                    return player.SteamName;
                return player.CSteamID.ToString();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Failed to get player name: {ex.Message}");
                return player.CSteamID.ToString();
            }
        }

        /// <summary>
        /// 安全获取凶手名称
        /// </summary>
        private static string GetKillerName(CSteamID murderer)
        {
            try
            {
                var killer = UnturnedPlayer.FromCSteamID(murderer);
                if (killer != null)
                {
                    return GetPlayerName(killer);
                }
            }
            catch
            {
                // 对于环境死亡（如窒息），凶手信息可能不可用，静默处理
            }
            return null;
        }

        #endregion

        #region 配置方法

        /// <summary>
        /// 启用/禁用事件转发
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            _isEnabled = enabled;
            if (ShouldLogDebug)
            {
                Logger.Log($"🎯 Events forwarding {(enabled ? "enabled" : "disabled")}");
            }
        }

        /// <summary>
        /// 更改转发频道
        /// </summary>
        public static void SetChannel(string channelId)
        {
            _channelId = channelId;
            if (ShouldLogDebug)
            {
                Logger.Log($"🎯 Events channel changed to: {channelId}");
            }
        }

        /// <summary>
        /// 启用/禁用特定事件
        /// </summary>
        public static void SetEventEnabled(string eventName, bool enabled)
        {
            if (Config?.GameToKook != null)
            {
                Config.GameToKook[eventName] = enabled;
                if (ShouldLogDebug)
                {
                    Logger.Log($"🎯 Event '{eventName}' {(enabled ? "enabled" : "disabled")}");
                }
            }
        }

        /// <summary>
        /// 获取所有事件状态
        /// </summary>
        public static Dictionary<string, bool> GetAllEventsStatus()
        {
            return Config?.GameToKook ?? new Dictionary<string, bool>();
        }

        /// <summary>
        /// 发送自定义事件消息
        /// </summary>
        public static async Task SendCustomEvent(string eventTitle, string eventContent)
        {
            if (!_isEnabled || _message == null) return;

            try
            {
                var card = KookApi.KookCardFactory.BuildMarkdownCard("🎯", eventTitle,
                    string.IsNullOrWhiteSpace(eventContent) ? "_(无详细内容)_" : eventContent,
                    DateTimeOffset.Now,
                    "secondary");
                var sent = await SendBoundedKookMessageAsync(
                    async () => await _message.CreateMessageAsync(10, _channelId, card),
                    "CustomEvent");
                if (sent && ShouldLogDebug)
                {
                    Logger.Log($"📤 Custom event sent to KOOK: {eventTitle}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error sending custom event: {ex.Message}");
            }
        }

        #endregion

        private static async Task<bool> SendBoundedKookMessageAsync(Func<Task> sendOperation, string context)
        {
            if (sendOperation == null)
            {
                throw new ArgumentNullException(nameof(sendOperation));
            }

            if (!TryReserveKookSendSlot(context))
            {
                return false;
            }

            bool semaphoreAcquired = false;
            var cancellationTokenSource = _kookSendCancellationTokenSource;

            try
            {
                if (cancellationTokenSource == null || cancellationTokenSource.IsCancellationRequested)
                {
                    return false;
                }

                await _kookSendSemaphore.WaitAsync(cancellationTokenSource.Token);
                semaphoreAcquired = true;

                if (cancellationTokenSource.IsCancellationRequested)
                {
                    return false;
                }

                await sendOperation();
                Interlocked.Increment(ref _kookSendSuccessCount);
                if (IsPvpContext(context))
                {
                    Interlocked.Increment(ref _pvpEventsSentCount);
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _kookSendCanceledCount);
                if (ShouldLogDebug)
                {
                    Logger.Log($"ℹ️ KOOK send canceled during shutdown: {context}");
                }

                return false;
            }
            finally
            {
                if (semaphoreAcquired)
                {
                    _kookSendSemaphore.Release();
                }

                Interlocked.Decrement(ref _pendingKookSendCount);
                MaybeLogDiagnostics(context);
            }
        }

        private static bool TryReserveKookSendSlot(string context)
        {
            while (true)
            {
                var current = Volatile.Read(ref _pendingKookSendCount);
                if (current >= MaxPendingKookSends)
                {
                    Interlocked.Increment(ref _kookSendDroppedCount);
                    if (IsPvpContext(context))
                    {
                        Interlocked.Increment(ref _pvpEventsBacklogDropCount);
                    }

                    Logger.LogWarning($"⚠️ KOOK send backlog reached {current}, dropping event: {context}");
                    MaybeLogDiagnostics($"{context}:backlog-drop");
                    return false;
                }

                if (Interlocked.CompareExchange(ref _pendingKookSendCount, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        private static void CancelKookSends()
        {
            _kookSendCancellationTokenSource?.Cancel();
        }

        private static void ResetKookSendCancellation()
        {
            var replacement = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _kookSendCancellationTokenSource, replacement);
            previous?.Cancel();
            previous?.Dispose();
        }

        private static bool IsPvpContext(string context)
        {
            return string.Equals(context, "PlayerDamaged", StringComparison.Ordinal);
        }

        private static void ResetRuntimeState()
        {
            _eventDeduplicationCache.Clear();
            Interlocked.Exchange(ref _lastCacheCleanupTicks, 0);
            Interlocked.Exchange(ref _lastDiagnosticsLogTicks, 0);
            Interlocked.Exchange(ref _kookSendSuccessCount, 0);
            Interlocked.Exchange(ref _kookSendCanceledCount, 0);
            Interlocked.Exchange(ref _kookSendDroppedCount, 0);
            Interlocked.Exchange(ref _pvpEventsReceivedCount, 0);
            Interlocked.Exchange(ref _pvpEventsDisabledCount, 0);
            Interlocked.Exchange(ref _pvpEventsInvalidPlayerCount, 0);
            Interlocked.Exchange(ref _pvpEventsSelfDamageSkippedCount, 0);
            Interlocked.Exchange(ref _pvpEventsLowDamageSkippedCount, 0);
            Interlocked.Exchange(ref _pvpEventsQueuedCount, 0);
            Interlocked.Exchange(ref _pvpEventsSentCount, 0);
            Interlocked.Exchange(ref _pvpEventsFailedCount, 0);
            Interlocked.Exchange(ref _pvpEventsBacklogDropCount, 0);
        }

        private static void MaybeLogDiagnostics(string reason)
        {
            if (!ShouldLogDebug)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var nowTicks = now.Ticks;
            var previousTicks = Interlocked.Read(ref _lastDiagnosticsLogTicks);
            if (previousTicks != 0 && nowTicks - previousTicks < DiagnosticsLogIntervalMs * TimeSpan.TicksPerMillisecond)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastDiagnosticsLogTicks, nowTicks, previousTicks) != previousTicks)
            {
                return;
            }

            Logger.Log(
                $"[DEBUG] Events diagnostics ({reason}): pendingSends={Volatile.Read(ref _pendingKookSendCount)}, " +
                $"sendSuccess={Volatile.Read(ref _kookSendSuccessCount)}, sendCanceled={Volatile.Read(ref _kookSendCanceledCount)}, " +
                $"sendDropped={Volatile.Read(ref _kookSendDroppedCount)}, dedupeCache={_eventDeduplicationCache.Count}, " +
                $"pvpReceived={Volatile.Read(ref _pvpEventsReceivedCount)}, pvpQueued={Volatile.Read(ref _pvpEventsQueuedCount)}, " +
                $"pvpSent={Volatile.Read(ref _pvpEventsSentCount)}, pvpFailed={Volatile.Read(ref _pvpEventsFailedCount)}, " +
                $"pvpBacklogDrop={Volatile.Read(ref _pvpEventsBacklogDropCount)}, pvpDisabled={Volatile.Read(ref _pvpEventsDisabledCount)}, " +
                $"pvpInvalid={Volatile.Read(ref _pvpEventsInvalidPlayerCount)}, pvpSelf={Volatile.Read(ref _pvpEventsSelfDamageSkippedCount)}, " +
                $"pvpLowDamage={Volatile.Read(ref _pvpEventsLowDamageSkippedCount)}");
        }
    }
}
