using System;
using System.Threading;
using System.Threading.Tasks;
using Emqo.KookBot_Unturned.KookApi;
using Emqo.KookBot_Unturned.Monitoring;
using Emqo.KookBot_Unturned.Utilities;
using Rocket.Core.Logging;
using SDG.Unturned;

namespace Emqo.KookBot_Unturned
{
    internal static partial class Commands
    {
        private static async Task HandleStatusCommand(string channelId)
        {
            try
            {
                using var operationCts = new CancellationTokenSource();
                var tcs = new TaskCompletionSource<ServerStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!MainThreadDispatcherGuard.TryQueue(cancellationToken =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled();
                        return;
                    }

                    try
                    {
                        tcs.TrySetResult(ServerMetricsProvider.Capture());
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Error capturing server status: {ex.Message}");
                        tcs.TrySetResult(new ServerStatusSnapshot
                        {
                            CapturedAt = DateTimeOffset.Now,
                            OnlinePlayers = 0,
                            MaxPlayers = Provider.maxPlayers,
                            QueueLength = 0,
                            AveragePing = 0,
                            HighestPing = 0,
                            EstimatedTps = 0,
                            Uptime = KookBot_UnturnedPlugin.GetUptime()
                        });
                    }
                }, operationCts.Token))
                {
                    var busyCard = BuildWarningCard("主线程繁忙", "当前主线程任务积压过多，请稍后再试。");
                    await _message.CreateMessageAsync(10, channelId, busyCard);
                    return;
                }

                ServerStatusSnapshot snapshot;
                try
                {
                    snapshot = await MainThreadDispatcherGuard.WaitAsync(tcs.Task, "Status command", MainThreadOperationTimeout, operationCts);
                }
                catch (TimeoutException)
                {
                    var timeoutCard = BuildWarningCard("获取服务器状态超时", "主线程处理超时（10秒），请稍后再试。");
                    await _message.CreateMessageAsync(10, channelId, timeoutCard);
                    return;
                }

                var serverName = Config?.ServerName ?? "Unturned";

                var card = KookCardFactory.BuildStatusCard(snapshot, serverName);
                if (!string.IsNullOrWhiteSpace(card))
                {
                    await _message.CreateMessageAsync(10, channelId, card);
                }
                else
                {
                    await _message.CreateMessageAsync(9, channelId, BuildStatusFallback(snapshot, serverName));
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to handle /status command: {ex.Message}");
                await _message.CreateMessageAsync(9, channelId, "❌ 无法获取服务器状态，请稍后再试");
            }
        }

    }
}
