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
        private static async Task HandleMuteCommand(string nickname, string args, string id, string channelId, bool isAdmin)
        {
            if (!isAdmin)
            {
                await CheckAdminPermissionAsync(id, channelId, "mute");
                return;
            }

            if (string.IsNullOrWhiteSpace(args))
            {
                var usageCard = BuildWarningCard("用法错误", "`/mute <玩家> [分钟] [原因]`");
                await _message.CreateMessageAsync(10, channelId, usageCard);
                return;
            }

            try
            {
                var parts = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    var usageCard = BuildWarningCard("用法错误", "`/mute <玩家> [分钟] [原因]`");
                    await _message.CreateMessageAsync(10, channelId, usageCard);
                    return;
                }

                var playerName = parts[0];
                int? durationMinutes = null;
                string reason = "Admin mute";

                // 解析可选的时长参数
                if (parts.Length > 1 && int.TryParse(parts[1], out var minutes))
                {
                    durationMinutes = minutes;
                    if (parts.Length > 2)
                    {
                        reason = string.Join(" ", parts.Skip(2));
                    }
                }
                else if (parts.Length > 1)
                {
                    reason = string.Join(" ", parts.Skip(1));
                }

                // 在主线程中查找玩家并执行禁言
                using var operationCts = new CancellationTokenSource();
                var tcs = new TaskCompletionSource<(bool success, string message, MuteInfo muteInfo)>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!MainThreadDispatcherGuard.TryQueue(cancellationToken =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled();
                        return;
                    }

                    try
                    {
                        var player = PlayerTool.getPlayer(playerName);
                        if (player == null)
                        {
                            tcs.TrySetResult((false, $"找不到玩家：{playerName}", null));
                            return;
                        }

                        var unturnedPlayer = UnturnedPlayer.FromPlayer(player);
                        if (unturnedPlayer == null)
                        {
                            tcs.TrySetResult((false, $"无法获取玩家信息：{playerName}", null));
                            return;
                        }

                        var duration = durationMinutes.HasValue ? TimeSpan.FromMinutes(durationMinutes.Value) : (TimeSpan?)null;
                        if (ChatModerationManager.TryMutePlayer(unturnedPlayer, duration, reason, nickname, out var muteInfo))
                        {
                            var durationText = muteInfo.GetDurationDescription();
                            tcs.TrySetResult((true, $"已禁言玩家 {unturnedPlayer.DisplayName}（{durationText}）", muteInfo));
                        }
                        else
                        {
                            tcs.TrySetResult((false, $"禁言玩家失败：{playerName}", null));
                        }
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetResult((false, $"错误：{ex.Message}", null));
                    }
                }, operationCts.Token))
                {
                    var busyCard = BuildWarningCard("主线程繁忙", "当前主线程任务积压过多，请稍后再试。");
                    await _message.CreateMessageAsync(10, channelId, busyCard);
                    return;
                }

                (bool success, string message, MuteInfo muteInfo) muteResult;
                try
                {
                    muteResult = await MainThreadDispatcherGuard.WaitAsync(tcs.Task, "Mute command", MainThreadOperationTimeout, operationCts);
                }
                catch (TimeoutException)
                {
                    var timeoutCard = BuildWarningCard("禁言执行超时", $"**玩家**：`{playerName}`\n**错误**：主线程处理超时（10秒）");
                    await _message.CreateMessageAsync(10, channelId, timeoutCard);
                    return;
                }

                if (muteResult.success)
                {
                    var successBody = new StringBuilder()
                        .AppendLine($"**玩家**：{muteResult.muteInfo.PlayerName}")
                        .AppendLine($"**时长**：{muteResult.muteInfo.GetDurationDescription()}")
                        .AppendLine($"**原因**：{muteResult.muteInfo.Reason}")
                        .AppendLine($"**操作者**：{nickname}")
                        .ToString();
                    var successCard = KookCardFactory.BuildMarkdownCard("🚫", "玩家已禁言", successBody, DateTimeOffset.Now, "warning");
                    await _message.CreateMessageAsync(10, channelId, successCard);

                    if (ShouldLogDebug())
                    {
                        Logger.Log($"🚫 {nickname} muted {muteResult.muteInfo.PlayerName} for {muteResult.muteInfo.GetDurationDescription()}");
                    }
                }
                else
                {
                    var errorCard = BuildErrorCard("禁言失败", muteResult.message);
                    await _message.CreateMessageAsync(10, channelId, errorCard);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error executing mute command: {ex.Message}");
                var errorCard = BuildErrorCard("执行错误", $"`{ex.Message}`");
                await _message.CreateMessageAsync(10, channelId, errorCard);
            }
        }

        private static async Task HandleUnmuteCommand(string nickname, string args, string id, string channelId, bool isAdmin)
        {
            if (!isAdmin)
            {
                await CheckAdminPermissionAsync(id, channelId, "unmute");
                return;
            }

            if (string.IsNullOrWhiteSpace(args))
            {
                var usageCard = BuildWarningCard("用法错误", "`/unmute <玩家>`");
                await _message.CreateMessageAsync(10, channelId, usageCard);
                return;
            }

            try
            {
                var playerName = args.Trim();

                // 在主线程中执行解除禁言
                using var operationCts = new CancellationTokenSource();
                var tcs = new TaskCompletionSource<(bool success, string message, MuteInfo muteInfo)>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!MainThreadDispatcherGuard.TryQueue(cancellationToken =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled();
                        return;
                    }

                    try
                    {
                        if (ChatModerationManager.TryUnmutePlayer(playerName, out var muteInfo))
                        {
                            tcs.TrySetResult((true, $"已解除禁言：{muteInfo.PlayerName}", muteInfo));
                        }
                        else
                        {
                            tcs.TrySetResult((false, $"找不到禁言记录：{playerName}", null));
                        }
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetResult((false, $"错误：{ex.Message}", null));
                    }
                }, operationCts.Token))
                {
                    var busyCard = BuildWarningCard("主线程繁忙", "当前主线程任务积压过多，请稍后再试。");
                    await _message.CreateMessageAsync(10, channelId, busyCard);
                    return;
                }

                (bool success, string message, MuteInfo muteInfo) unmuteResult;
                try
                {
                    unmuteResult = await MainThreadDispatcherGuard.WaitAsync(tcs.Task, "Unmute command", MainThreadOperationTimeout, operationCts);
                }
                catch (TimeoutException)
                {
                    var timeoutCard = BuildWarningCard("解除禁言超时", $"**玩家**：`{playerName}`\n**错误**：主线程处理超时（10秒）");
                    await _message.CreateMessageAsync(10, channelId, timeoutCard);
                    return;
                }

                if (unmuteResult.success)
                {
                    var successBody = new StringBuilder()
                        .AppendLine($"**玩家**：{unmuteResult.muteInfo.PlayerName}")
                        .AppendLine($"**原因**：{unmuteResult.muteInfo.Reason}")
                        .AppendLine($"**操作者**：{nickname}")
                        .ToString();
                    var successCard = KookCardFactory.BuildMarkdownCard("✅", "禁言已解除", successBody, DateTimeOffset.Now, "success");
                    await _message.CreateMessageAsync(10, channelId, successCard);

                    if (ShouldLogDebug())
                    {
                        Logger.Log($"✅ {nickname} unmuted {unmuteResult.muteInfo.PlayerName}");
                    }
                }
                else
                {
                    var errorCard = BuildErrorCard("解除禁言失败", unmuteResult.message);
                    await _message.CreateMessageAsync(10, channelId, errorCard);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error executing unmute command: {ex.Message}");
                var errorCard = BuildErrorCard("执行错误", $"`{ex.Message}`");
                await _message.CreateMessageAsync(10, channelId, errorCard);
            }
        }

        private static async Task HandleMutesCommand(string id, string channelId, bool isAdmin)
        {
            if (!isAdmin)
            {
                await CheckAdminPermissionAsync(id, channelId, "mutes");
                return;
            }

            try
            {
                var mutes = ChatModerationManager.GetActiveMutes();

                if (mutes.Count == 0)
                {
                    var emptyCard = BuildInfoCard("禁言列表", "当前没有被禁言的玩家");
                    await _message.CreateMessageAsync(10, channelId, emptyCard);
                    return;
                }

                var mutesList = new StringBuilder();
                mutesList.AppendLine($"**当前禁言数**：{mutes.Count}\n");

                foreach (var mute in mutes.Take(10))
                {
                    mutesList.AppendLine($"• **{mute.PlayerName}**");
                    mutesList.AppendLine($"  时长：{mute.GetDurationDescription()}");
                    mutesList.AppendLine($"  原因：{mute.Reason}");
                    mutesList.AppendLine($"  操作者：{mute.MutedBy}");
                    mutesList.AppendLine();
                }

                if (mutes.Count > 10)
                {
                    mutesList.AppendLine($"... 还有 {mutes.Count - 10} 条记录");
                }

                var listCard = KookCardFactory.BuildMarkdownCard("📋", "禁言列表", mutesList.ToString(), DateTimeOffset.Now, "info");
                await _message.CreateMessageAsync(10, channelId, listCard);

                if (ShouldLogDebug())
                {
                    Logger.Log($"📋 {id} viewed mutes list ({mutes.Count} active)");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error executing mutes command: {ex.Message}");
                var errorCard = BuildErrorCard("执行错误", $"`{ex.Message}`");
                await _message.CreateMessageAsync(10, channelId, errorCard);
            }
        }
    }
}
