using System.Text;

namespace Emqo.KookBot_Unturned
{
    internal static class CommandHelpPolicy
    {
        public static string BuildHelpText(KookBot_UnturnedConfiguration config, bool isAdmin)
        {
            var helpText = new StringBuilder();
            helpText.Append("**🔹 普通指令：**\n");

            if (config?.IsCommandEnabled("help") == true)
            {
                helpText.Append("`/help` - 显示此帮助信息\n");
            }

            if (config?.IsCommandEnabled("status") == true)
            {
                helpText.Append("`/status` - 查看 TPS/在线/延迟/排队\n");
            }

            if (config?.IsCommandEnabled("list") == true)
            {
                helpText.Append("`/list` - 查看在线玩家列表\n");
            }

            helpText.Append("\n");

            if (!isAdmin)
            {
                helpText.Append("*你没有管理员权限，无法查看管理员指令*");
                return helpText.ToString();
            }

            helpText.Append("**🔸 管理员指令：**\n");

            if (config?.IsCommandEnabled("say") == true)
            {
                helpText.Append("`/say <内容>` - 服务器广播消息\n");
            }

            if (config?.IsCommandEnabled("cmd") == true || config?.IsCommandEnabled("console") == true || config?.IsCommandEnabled("exec") == true)
            {
                helpText.Append("`/cmd <指令>` - 执行控制台指令\n");
            }

            helpText.Append("\n**🔐 禁言管理：**\n");
            helpText.Append("`/mute <玩家> [分钟] [原因]` - 禁言玩家\n");
            helpText.Append("`/unmute <玩家>` - 解除禁言\n");
            helpText.Append("`/mutes` - 查看禁言列表\n");

            helpText.Append("\n**🛟 运维入口：**\n");
            helpText.Append("`/event list` / `/event status` - 查看事件同步开关\n");
            helpText.Append("`/event <事件名> on|off` - 临时调整事件同步\n");
            helpText.Append("`/event set <事件名> <true|false>` - 临时调整事件同步\n");
            helpText.Append("`/command list` - 查看指令开关\n");
            helpText.Append("`/command <指令名> on|off` - 临时调整指令开关\n");
            helpText.Append("`/command set <指令名> <true|false>` - 临时调整指令开关\n");
            helpText.Append("`/diag` - 查看安全诊断信息\n");

            return helpText.ToString();
        }
    }
}
