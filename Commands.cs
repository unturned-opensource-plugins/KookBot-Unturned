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

        private static Message _message;
        private static readonly TimeSpan MainThreadOperationTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ConsoleCommandTimeout = TimeSpan.FromSeconds(30);
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

            // 救援入口不受 CommandSettings 控制，避免事故中误关运维入口。
            if (KookAdminCommandHandler.IsRescueCommand(command))
            {
                await KookAdminCommandHandler.HandleAsync(command, args, id, channelId, isAdmin, _message, Config);
                return;
            }

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

                case "list":
                    await HandleListCommand(channelId);
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
    }
}
