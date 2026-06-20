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
                args = StringUtils.SanitizeRichText(args);

                if (!MainThreadDispatcherGuard.TryQueue(() =>
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
                }))
                {
                    var busyCard = BuildWarningCard("主线程繁忙", "当前主线程任务积压过多，请稍后再试。");
                    await _message.CreateMessageAsync(10, channelId, busyCard);
                    return;
                }

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
            if (Config.IsCommandEnabled("list"))
                helpText.Append("`/list` - 查看在线玩家列表\n");

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

                helpText.Append("\n**🛟 运维入口：**\n");
                helpText.Append("`/event list` - 查看事件同步开关\n");
                helpText.Append("`/event set <事件名> <true|false>` - 临时调整事件同步\n");
                helpText.Append("`/command list` - 查看指令开关\n");
                helpText.Append("`/command set <指令名> <true|false>` - 临时调整指令开关\n");
                helpText.Append("`/diag` - 查看安全诊断信息\n");
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
    }
}
