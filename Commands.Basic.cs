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
            var helpText = CommandHelpPolicy.BuildHelpText(Config, isAdmin);

            var helpCard = KookCardFactory.BuildMarkdownCard("📚", "可用指令列表", helpText, DateTimeOffset.Now, "info");
            await _message.CreateMessageAsync(10, channelId, helpCard);
            if (ShouldLogDebug())
            {
                Logger.Log($"❓ {id} asked for help (Admin: {isAdmin})");
            }
        }
    }
}
