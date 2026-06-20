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

        private static bool ShouldLogDebug()
        {
            return Config?.Debug ?? false;
        }
    }
}
