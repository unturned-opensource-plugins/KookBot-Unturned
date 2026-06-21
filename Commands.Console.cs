using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emqo.KookBot_Unturned.KookApi;
using Emqo.KookBot_Unturned.Utilities;
using Rocket.Core;
using Rocket.Core.Logging;
using SDG.Unturned;
using Steamworks;

namespace Emqo.KookBot_Unturned
{
    internal static partial class Commands
    {
        private static async Task HandleConsoleCommand(string nickname, string args, string id, string channelId, bool isAdmin)
        {
            if (!isAdmin)
            {
                await CheckAdminPermissionAsync(id, channelId, "cmd");
                return;
            }

            if (string.IsNullOrEmpty(args))
            {
                var missingArgsCard = BuildWarningCard("请输入指令", "`/cmd <指令>`");
                await _message.CreateMessageAsync(10, channelId, missingArgsCard);
                return;
            }

            try
            {
                var executed = false;
                var exception = (Exception)null;
                using var operationCts = new CancellationTokenSource();

                // 在主线程执行命令
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!MainThreadDispatcherGuard.TryQueue(cancellationToken =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled();
                        return;
                    }

                    try
                    {
                        // Unturned 控制台命令使用 / 分隔参数
                        // 例如: "give ff 76 5" -> "give ff/76/5"
                        var parts = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        string fullCommand;

                        if (parts.Length <= 1)
                        {
                            // 无参数或只有命令名
                            fullCommand = args;
                        }
                        else
                        {
                            // 命令名 + 第一个参数 + 其余参数用/分隔
                            // give ff 76 5 -> give ff/76/5
                            fullCommand = parts[0] + " " + string.Join("/", parts.Skip(1));
                        }

                        Commander.execute(CSteamID.Nil, fullCommand);
                        executed = true;
                        tcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                        tcs.TrySetResult(false);
                    }
                }, operationCts.Token))
                {
                    var busyCard = BuildWarningCard("主线程繁忙", "当前主线程任务积压过多，请稍后再试。");
                    await _message.CreateMessageAsync(10, channelId, busyCard);
                    return;
                }

                try
                {
                    await MainThreadDispatcherGuard.WaitAsync(tcs.Task, "Console command execution", ConsoleCommandTimeout, operationCts);
                }
                catch (TimeoutException)
                {
                    var timeoutCard = BuildWarningCard("指令执行超时", $"**指令**：`{args}`\n**错误**：执行超时（30秒）");
                    await _message.CreateMessageAsync(10, channelId, timeoutCard);
                    return;
                }

                if (exception != null)
                {
                    var errorCard = BuildErrorCard("指令执行失败", $"**指令**：`{args}`\n**错误**：`{exception.Message}`");
                    await _message.CreateMessageAsync(10, channelId, errorCard);
                    return;
                }

                if (executed)
                {
                    if (ShouldLogDebug())
                    {
                        Logger.Log($"🔧 Console command executed by {id}: {args}");
                    }

                    var successCard = KookCardFactory.BuildMarkdownCard(
                        "✅",
                        "指令执行成功",
                        $"**指令**：`{args}`",
                        DateTimeOffset.Now,
                        "success"
                    );
                    await _message.CreateMessageAsync(10, channelId, successCard);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error executing console command: {ex.Message}");
                var failureCard = BuildErrorCard("执行错误", $"**指令**：`{args}`\n**错误**：`{ex.Message}`");
                await _message.CreateMessageAsync(10, channelId, failureCard);
            }
        }

    }
}
