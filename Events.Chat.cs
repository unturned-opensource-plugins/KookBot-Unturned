using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rocket.Core.Logging;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;

namespace Emqo.KookBot_Unturned
{
    public partial class Events
    {
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

    }
}
