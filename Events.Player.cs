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

        #endregion
    }
}
