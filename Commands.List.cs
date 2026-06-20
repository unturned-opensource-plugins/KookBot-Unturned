using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emqo.KookBot_Unturned.KookApi;
using Emqo.KookBot_Unturned.Monitoring;
using Emqo.KookBot_Unturned.Utilities;
using Rocket.Core.Logging;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using Rocket.Core;
using SDG.Unturned;
using Steamworks;

namespace Emqo.KookBot_Unturned
{
    internal static partial class Commands
    {
        private static async Task HandleListCommand(string channelId)
        {
            try
            {
                // 在主线程中获取玩家列表
                using var operationCts = new CancellationTokenSource();
                var tcs = new TaskCompletionSource<List<(string, string)>>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!MainThreadDispatcherGuard.TryQueue(cancellationToken =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled();
                        return;
                    }

                    try
                    {
                        var playerList = new List<(string, string)>();
                        var allClients = Provider.clients?.ToList();

                        if (allClients != null)
                        {
                            foreach (var client in allClients)
                            {
                                if (client != null && client.player != null)
                                {
                                    var unturnedPlayer = UnturnedPlayer.FromPlayer(client.player);
                                    if (unturnedPlayer != null)
                                    {
                                        playerList.Add((unturnedPlayer.DisplayName, unturnedPlayer.CSteamID.ToString()));
                                    }
                                }
                            }
                        }

                        tcs.TrySetResult(playerList);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Error getting player list: {ex.Message}");
                        tcs.TrySetResult(new List<(string, string)>());
                    }
                }, operationCts.Token))
                {
                    var busyCard = BuildWarningCard("主线程繁忙", "当前主线程任务积压过多，请稍后再试。");
                    await _message.CreateMessageAsync(10, channelId, busyCard);
                    return;
                }

                List<(string, string)> players;
                try
                {
                    players = await MainThreadDispatcherGuard.WaitAsync(tcs.Task, "List players command", MainThreadOperationTimeout, operationCts);
                }
                catch (TimeoutException)
                {
                    var timeoutCard = BuildWarningCard("获取玩家列表超时", "主线程处理超时（10秒），请稍后再试。");
                    await _message.CreateMessageAsync(10, channelId, timeoutCard);
                    return;
                }

                if (players.Count == 0)
                {
                    var emptyCard = BuildInfoCard("玩家列表", "当前没有在线玩家");
                    await _message.CreateMessageAsync(10, channelId, emptyCard);
                    return;
                }

                var playerListText = new StringBuilder();
                playerListText.AppendLine($"**当前在线玩家**：{players.Count}\n");

                foreach (var (name, steamId) in players.Take(20))
                {
                    playerListText.AppendLine($"• **{name}** `{steamId}`");
                }

                if (players.Count > 20)
                {
                    playerListText.AppendLine($"\n... 还有 {players.Count - 20} 名玩家");
                }

                var listCard = KookCardFactory.BuildMarkdownCard("👥", "在线玩家列表", playerListText.ToString(), DateTimeOffset.Now, "info");
                await _message.CreateMessageAsync(10, channelId, listCard);

                if (ShouldLogDebug())
                {
                    Logger.Log($"👥 Player list requested ({players.Count} online)");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error executing list command: {ex.Message}");
                var errorCard = BuildErrorCard("执行错误", $"`{ex.Message}`");
                await _message.CreateMessageAsync(10, channelId, errorCard);
            }
        }
    }
}
