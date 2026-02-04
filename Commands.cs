using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emqo.KookBot_Unturned.KookApi;
using Emqo.KookBot_Unturned.Monitoring;
using Rocket.Core.Logging;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using Rocket.Core;
using SDG.Unturned;
using Steamworks;

namespace Emqo.KookBot_Unturned
{
    internal static class Commands
    {
        private const int MaxCommandOutputLength = 900;  // Maximum length for command output
        private static Message _message;

        // 配置属性快捷访问
        private static KookBot_UnturnedConfiguration Config =>
            KookBot_UnturnedPlugin.Instance?.Configuration?.Instance;

        public static void Init(Message message)
        {
            _message = message;
        }

        public static async Task ExecuteAsync(string nickname, string content, string id, string channelId)
        {
            if (string.IsNullOrWhiteSpace(content) || content.Length <= 1)
            {
                return;
            }

            var raw = content.Substring(1).Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var parts = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return;
            }

            var command = parts[0].ToLowerInvariant();
            var args = parts.Length > 1 ? raw.Substring(command.Length).Trim() : string.Empty;
            var isAdmin = IsAdminUser(id);

            // 检查指令是否被禁用
            if (Config != null && !Config.IsCommandEnabled(command))
            {
                Logger.Log($"🚫 Command /{command} is disabled, request from {id}");
                var disabledBody = new StringBuilder()
                    .AppendLine($"**指令**：`/{command}`")
                    .AppendLine("**状态**：已被禁用")
                    .ToString();
                var disabledCard = BuildErrorCard("指令已被禁用", disabledBody);
                await _message.CreateMessageAsync(10, channelId, disabledCard);
                return;
            }

            switch (command)
            {
                case "say":
                    await HandleSayCommand(nickname, args, id, channelId, isAdmin);
                    break;

                case "help":
                    await HandleHelpCommand(id, channelId, isAdmin);
                    break;

                case "status":
                    await HandleStatusCommand(channelId);
                    break;

                case "mute":
                    await HandleMuteCommand(nickname, args, id, channelId, isAdmin);
                    break;

                case "unmute":
                    await HandleUnmuteCommand(nickname, args, id, channelId, isAdmin);
                    break;

                case "mutes":
                    await HandleMutesCommand(id, channelId, isAdmin);
                    break;

                case "cmd":
                case "console":
                case "exec":
                    await HandleConsoleCommand(nickname, args, id, channelId, isAdmin);
                    break;

                default:
                    Logger.Log($"⚠️ Unknown Commands: /{command} from {id}");
                    var unknownBody = new StringBuilder()
                        .AppendLine($"**指令**：`/{command}`")
                        .AppendLine("**状态**：未知指令")
                        .AppendLine("使用 `/help` 查看可用指令")
                        .ToString();
                    var unknownCard = BuildWarningCard("未知指令", unknownBody);
                    await _message.CreateMessageAsync(10, channelId, unknownCard);
                    break;
            }
        }

        private static async Task HandleSayCommand(string nickname, string args, string id, string channelId, bool isAdmin)
        {
            if (!isAdmin)
            {
                await CheckAdminPermissionAsync(id, channelId, "say");
                return;
            }

            if (!string.IsNullOrEmpty(args))
            {
                // 防止富文本注入
                args = SanitizeRichText(args);

                Rocket.Core.Utils.TaskDispatcher.QueueOnMainThread(() =>
                {
                    var msg =
                        "<color=#FF3B3B>[Admin]</color>" +
                        $"<color=#FFD700>{nickname}</color>" +
                        $"<color=#FFFFFF>: {args}</color>";

                    SDG.Unturned.ChatManager.serverSendMessage(
                        msg,
                        UnityEngine.Color.white,
                        null,
                        null,
                        SDG.Unturned.EChatMode.GLOBAL,
                        null,
                        true // 启用 Rich Text
                    );
                });

                if (ShouldLogDebug())
                {
                    Logger.Log($"💬 /say executed by {id}: {args}");
                }

                var broadcastCard = KookCardFactory.BuildGenericEventCard(
                    "📣",
                    "服务器广播",
                    new List<(string, string)>
                    {
                ("管理员", nickname),
                ("内容", args)
                    },
                    DateTimeOffset.Now,
                    "primary"
                );
                await _message.CreateMessageAsync(10, channelId, broadcastCard);
            }
            else
            {
                var missingArgsCard = KookCardFactory.BuildMarkdownCard(
                    "⚠️",
                    "请输入内容",
                    "`/say <内容>`",
                    DateTimeOffset.Now,
                    "warning"
                );
                await _message.CreateMessageAsync(10, channelId, missingArgsCard);
            }
        }


        private static async Task HandleHelpCommand(string id, string channelId, bool isAdmin)
        {
            var helpText = new StringBuilder();
            helpText.Append("**🔹 普通指令：**\n");

            // 只显示已启用的普通指令
            if (Config.IsCommandEnabled("help"))
                helpText.Append("`/help` - 显示此帮助信息\n");
            if (Config.IsCommandEnabled("status"))
                helpText.Append("`/status` - 查看 TPS/在线/延迟/排队\n");

            helpText.Append("\n");

            if (isAdmin)
            {
                helpText.Append("**🔸 管理员指令：**\n");

                // 只显示已启用的管理员指令
                if (Config.IsCommandEnabled("say"))
                    helpText.Append("`/say <内容>` - 服务器广播消息\n");
                if (Config.IsCommandEnabled("cmd") || Config.IsCommandEnabled("console") || Config.IsCommandEnabled("exec"))
                    helpText.Append("`/cmd <指令>` - 执行控制台指令\n");

                helpText.Append("\n**🔐 禁言管理：**\n");
                helpText.Append("`/mute <玩家> [分钟] [原因]` - 禁言玩家\n");
                helpText.Append("`/unmute <玩家>` - 解除禁言\n");
                helpText.Append("`/mutes` - 查看禁言列表\n");
            }
            else
            {
                helpText.Append("*你没有管理员权限，无法查看管理员指令*");
            }

            var helpCard = KookCardFactory.BuildMarkdownCard("📚", "可用指令列表", helpText.ToString(), DateTimeOffset.Now, "info");
            await _message.CreateMessageAsync(10, channelId, helpCard);
            if (ShouldLogDebug())
            {
                Logger.Log($"❓ {id} asked for help (Admin: {isAdmin})");
            }
        }

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
                // Add timeout protection (30 seconds)
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    try
                    {
                        var output = await CommandOutputInterceptor.CaptureAsync(async () =>
                        {
                            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            Rocket.Core.Utils.TaskDispatcher.QueueOnMainThread(() =>
                            {
                                try
                                {
                                    Commander.execute(CSteamID.Nil, args);
                                    tcs.TrySetResult(true);
                                }
                                catch (Exception execEx)
                                {
                                    tcs.TrySetException(execEx);
                                }
                            });

                            // Wait with timeout using Task.WhenAny
                            var delayTask = Task.Delay(Timeout.Infinite, cts.Token);
                            var completedTask = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);

                            if (completedTask == delayTask)
                            {
                                throw new OperationCanceledException("Command execution timeout");
                            }

                            await tcs.Task.ConfigureAwait(false);
                        });

                        if (ShouldLogDebug())
                        {
                            Logger.Log($"🔧 Console command executed by {id}: {args}");
                        }

                        var successBody = BuildCommandResultBody(args, output);
                        var successCard = KookCardFactory.BuildMarkdownCard(
                            "🛠️",
                            "指令执行成功",
                            successBody,
                            DateTimeOffset.Now,
                            "success"
                        );

                        await _message.CreateMessageAsync(10, channelId, successCard);
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.LogWarning($"Console command timeout for {id}: {args}");
                        var timeoutBody = new StringBuilder()
                            .AppendLine($"**指令**：`{args}`")
                            .AppendLine("**错误**：`指令执行超时（30秒）`")
                            .ToString();
                        var timeoutCard = KookCardFactory.BuildMarkdownCard(
                            "⏱️",
                            "指令执行超时",
                            timeoutBody,
                            DateTimeOffset.Now,
                            "warning"
                        );
                        await _message.CreateMessageAsync(10, channelId, timeoutCard);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error executing console command: {ex.Message}");
                var failureBody = new StringBuilder()
                    .AppendLine($"**指令**：`{args}`")
                    .AppendLine($"**错误**：`{ex.Message}`")
                    .ToString();
                var failureCard = KookCardFactory.BuildMarkdownCard(
                    "❌",
                    "指令执行失败",
                    failureBody,
                    DateTimeOffset.Now,
                    "danger"
                );
                await _message.CreateMessageAsync(10, channelId, failureCard);
            }
        }

        private static async Task HandleStatusCommand(string channelId)
        {
            try
            {
                var snapshot = ServerMetricsProvider.Capture();
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
                var muteResult = await Task.Run(() =>
                {
                    var tcs = new TaskCompletionSource<(bool success, string message, MuteInfo muteInfo)>();
                    Rocket.Core.Utils.TaskDispatcher.QueueOnMainThread(() =>
                    {
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
                    });

                    return tcs.Task.Result;
                });

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
                var unmuteResult = await Task.Run(() =>
                {
                    var tcs = new TaskCompletionSource<(bool success, string message, MuteInfo muteInfo)>();
                    Rocket.Core.Utils.TaskDispatcher.QueueOnMainThread(() =>
                    {
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
                    });

                    return tcs.Task.Result;
                });

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
                // 在主线程中获取禁言列表
                var mutes = await Task.Run(() =>
                {
                    var tcs = new TaskCompletionSource<IReadOnlyCollection<MuteInfo>>();
                    Rocket.Core.Utils.TaskDispatcher.QueueOnMainThread(() =>
                    {
                        try
                        {
                            var activeMutes = ChatModerationManager.GetActiveMutes();
                            tcs.TrySetResult(activeMutes);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"Error getting mutes: {ex.Message}");
                            tcs.TrySetResult(new List<MuteInfo>());
                        }
                    });

                    return tcs.Task.Result;
                });

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

        private static string BuildStatusFallback(ServerStatusSnapshot snapshot, string serverName)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"📊 **{serverName} 服务器状态**");
            builder.AppendLine($"- TPS：`{snapshot.EstimatedTps}`");
            builder.AppendLine($"- 在线：`{snapshot.OnlinePlayers}/{snapshot.MaxPlayers}`");
            builder.AppendLine($"- 排队：`{snapshot.QueueLength}`");
            builder.AppendLine($"- 运行时间：`{snapshot.Uptime}`");
            builder.AppendLine($"- 更新时间：`{snapshot.CapturedAt:HH:mm:ss}`");
            return builder.ToString();
        }

        private static bool IsAdminUser(string userId)
        {
            return Config?.Admin?.Contains(userId) ?? false;
        }

        private static async Task CheckAdminPermissionAsync(string id, string channelId, string commandName)
        {
            var noPermissionCard = BuildErrorCard("权限不足", $"你没有权限使用 `/{commandName}` 指令。");
            await _message.CreateMessageAsync(10, channelId, noPermissionCard);

            if (ShouldLogDebug())
            {
                Logger.Log($"🚫 User {id} attempts to use /{commandName} but does not have permission");
            }
        }

        private static string BuildErrorCard(string title, string body)
        {
            return KookCardFactory.BuildMarkdownCard("❌", title, body, DateTimeOffset.Now, "danger");
        }

        private static string BuildWarningCard(string title, string body)
        {
            return KookCardFactory.BuildMarkdownCard("⚠️", title, body, DateTimeOffset.Now, "warning");
        }

        private static string BuildInfoCard(string title, string body)
        {
            return KookCardFactory.BuildMarkdownCard("ℹ️", title, body, DateTimeOffset.Now, "info");
        }

        private static string BuildSuccessCard(string title, string body)
        {
            return KookCardFactory.BuildMarkdownCard("✅", title, body, DateTimeOffset.Now, "success");
        }

        private static string BuildCommandResultBody(string args, string output)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"**指令**：`{args}`");

            if (!string.IsNullOrWhiteSpace(output))
            {
                builder.AppendLine("**输出：**");
                builder.AppendLine("```");
                builder.AppendLine(SanitizeCommandOutput(output));
                builder.AppendLine("```");
            }
            else
            {
                builder.AppendLine("**输出**：`(无)`");
            }

            return builder.ToString();
        }

        private static string SanitizeRichText(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }

            // 统一的富文本清理 - 替换所有可能的注入字符
            return input
                .Replace("<", "＜")
                .Replace(">", "＞")
                .Replace("[", "［")
                .Replace("]", "］")
                .Replace("\\", "＼");
        }

        private static string SanitizeCommandOutput(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var trimmed = raw.Trim();

            // Security: Truncate output to prevent information disclosure
            if (trimmed.Length > MaxCommandOutputLength)
            {
                Logger.LogWarning($"⚠️ Command output exceeded max length ({trimmed.Length} > {MaxCommandOutputLength}), truncating");
                trimmed = trimmed.Substring(0, MaxCommandOutputLength) + "...";
            }

            return trimmed.Replace("```", "`\u200b``");
        }

        private static bool ShouldLogDebug()
        {
            return Config?.Debug ?? false;
        }
    }
}