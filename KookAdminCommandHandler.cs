using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Emqo.KookBot_Unturned.KookApi;
using Rocket.Core.Logging;

namespace Emqo.KookBot_Unturned
{
    internal static class KookAdminCommandHandler
    {
        private const string EventUsage = "`/event list`\n`/event status`\n`/event set <事件名> <true|false>`\n`/event <事件名> <on|off>`";
        private const string CommandUsage = "`/command list`\n`/command set <指令名> <true|false>`\n`/command <指令名> <on|off>`";
        private static readonly HashSet<string> RescueCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "event",
            "command",
            "diag"
        };
        private static readonly string[] ConsoleCommandAliases = { "cmd", "console", "exec" };

        internal static bool IsRescueCommand(string command)
        {
            return RescueCommands.Contains(command ?? string.Empty);
        }

        internal static async Task HandleAsync(
            string command,
            string args,
            string id,
            string channelId,
            bool isAdmin,
            Message message,
            KookBot_UnturnedConfiguration config)
        {
            if (message == null)
            {
                Logger.LogError($"❌ Cannot handle /{command}: KOOK message API is not initialized.");
                return;
            }

            switch (command)
            {
                case "event":
                    await HandleEventCommand(args, id, channelId, isAdmin, message, config);
                    break;
                case "command":
                    await HandleCommandSwitchCommand(args, id, channelId, isAdmin, message, config);
                    break;
                case "diag":
                    await HandleDiagCommand(id, channelId, isAdmin, message);
                    break;
            }
        }

        private static async Task HandleEventCommand(string args, string id, string channelId, bool isAdmin, Message message, KookBot_UnturnedConfiguration config)
        {
            if (!isAdmin)
            {
                await CheckAdminPermissionAsync(id, channelId, "event", message, config);
                return;
            }

            var parts = SplitArgs(args);
            if (parts.Length == 0 || IsEventListAction(parts[0]))
            {
                await SendSettingsListAsync("事件同步开关", config?.GetGameToKookSnapshot(), channelId, message);
                return;
            }

            if (TryParseSwitchMutation(parts, out var eventName, out var enabled, out var parseErrorUsage))
            {
                if (!string.IsNullOrEmpty(parseErrorUsage))
                {
                    await message.CreateMessageAsync(10, channelId, BuildWarningCard("参数错误", EventUsage));
                    return;
                }

                if (config?.GetGameToKookSnapshot().ContainsKey(eventName) != true)
                {
                    await message.CreateMessageAsync(10, channelId, BuildErrorCard("未知事件", $"事件 `{eventName}` 不存在。使用 `/event list` 查看可用项。"));
                    return;
                }

                Events.SetEventEnabled(eventName, enabled);
                var card = BuildInfoCard("事件同步已更新", $"`{eventName}` => `{(enabled ? "true" : "false")}`\n\n此修改只影响当前运行中的插件进程，不写回 XML。");
                await message.CreateMessageAsync(10, channelId, card);
                Logger.Log($"🛟 Runtime event switch changed by {id}: {eventName}={enabled}");
                return;
            }

            await message.CreateMessageAsync(10, channelId, BuildWarningCard("用法错误", EventUsage));
        }

        private static async Task HandleCommandSwitchCommand(string args, string id, string channelId, bool isAdmin, Message message, KookBot_UnturnedConfiguration config)
        {
            if (!isAdmin)
            {
                await CheckAdminPermissionAsync(id, channelId, "command", message, config);
                return;
            }

            var parts = SplitArgs(args);
            if (parts.Length == 0 || IsCommandListAction(parts[0]))
            {
                await SendSettingsListAsync("指令开关", config?.GetCommandSnapshot(), channelId, message);
                return;
            }

            if (TryParseSwitchMutation(parts, out var commandName, out var enabled, out var parseErrorUsage))
            {
                commandName = commandName.ToLowerInvariant();
                if (!IsMutableCommandSwitch(commandName))
                {
                    await message.CreateMessageAsync(10, channelId, BuildErrorCard("不能关闭救援入口", $"`/{commandName}` 是运维救援入口，只受管理员权限控制。"));
                    return;
                }

                if (!string.IsNullOrEmpty(parseErrorUsage))
                {
                    await message.CreateMessageAsync(10, channelId, BuildWarningCard("参数错误", CommandUsage));
                    return;
                }

                if (config?.GetCommandSnapshot().ContainsKey(commandName) != true)
                {
                    await message.CreateMessageAsync(10, channelId, BuildErrorCard("未知指令", $"指令 `/{commandName}` 不存在。使用 `/command list` 查看可用项。"));
                    return;
                }

                var changedCommands = SetCommandRuntime(config, commandName, enabled);
                var changedText = string.Join(", ", changedCommands.Select(name => $"`/{name}`"));
                var card = BuildInfoCard("指令开关已更新", $"{changedText} => `{(enabled ? "true" : "false")}`\n\n此修改只影响当前运行中的插件进程，不写回 XML。");
                await message.CreateMessageAsync(10, channelId, card);
                Logger.Log($"🛟 Runtime command switch changed by {id}: {string.Join(",", changedCommands)}={enabled}");
                return;
            }

            await message.CreateMessageAsync(10, channelId, BuildWarningCard("用法错误", CommandUsage));
        }

        private static async Task HandleDiagCommand(string id, string channelId, bool isAdmin, Message message)
        {
            if (!isAdmin)
            {
                await CheckAdminPermissionAsync(id, channelId, "diag", message, null);
                return;
            }

            var body = Events.BuildDiagnosticsReport();
            var card = KookCardFactory.BuildMarkdownCard("🛟", "安全诊断", body, DateTimeOffset.Now, "info");
            await message.CreateMessageAsync(10, channelId, card);
            Logger.Log($"🛟 Diagnostics requested by {id}");
        }

        internal static bool IsMutableCommandSwitch(string commandName)
        {
            return !IsRescueCommand(commandName);
        }

        internal static IReadOnlyList<string> SetCommandRuntime(KookBot_UnturnedConfiguration config, string commandName, bool enabled)
        {
            var names = ConsoleCommandAliases.Contains(commandName, StringComparer.OrdinalIgnoreCase)
                ? ConsoleCommandAliases
                : new[] { commandName };

            foreach (var name in names)
            {
                config.TrySetCommandRuntime(name, enabled);
            }

            return names;
        }

        private static async Task SendSettingsListAsync(string title, IDictionary<string, bool> settings, string channelId, Message message)
        {
            if (settings == null || settings.Count == 0)
            {
                await message.CreateMessageAsync(10, channelId, BuildWarningCard(title, "当前没有可用开关。"));
                return;
            }

            var body = new StringBuilder();
            foreach (var pair in settings.OrderBy(pair => pair.Key))
            {
                body.AppendLine($"• `{pair.Key}`：`{(pair.Value ? "true" : "false")}`");
            }

            await message.CreateMessageAsync(10, channelId, BuildInfoCard(title, body.ToString()));
        }

        private static async Task CheckAdminPermissionAsync(string id, string channelId, string commandName, Message message, KookBot_UnturnedConfiguration config)
        {
            var noPermissionCard = BuildErrorCard("权限不足", $"你没有权限使用 `/{commandName}` 指令。");
            await message.CreateMessageAsync(10, channelId, noPermissionCard);

            if (config?.Debug ?? false)
            {
                Logger.Log($"🚫 User {id} attempts to use /{commandName} but does not have permission");
            }
        }

        internal static string[] SplitArgs(string args)
        {
            return string.IsNullOrWhiteSpace(args)
                ? Array.Empty<string>()
                : args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool IsEventListAction(string action)
        {
            return string.Equals(action, "list", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "status", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCommandListAction(string action)
        {
            return string.Equals(action, "list", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSetAction(string action)
        {
            return string.Equals(action, "set", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryParseSwitchMutation(string[] parts, out string name, out bool enabled, out string errorUsage)
        {
            name = null;
            enabled = false;
            errorUsage = null;

            if (parts == null)
            {
                return false;
            }

            if (parts.Length == 3 && IsSetAction(parts[0]))
            {
                name = parts[1];
                if (!TryParseBoolean(parts[2], out enabled))
                {
                    errorUsage = "`set` 用法：`set <名称> <true|false>`";
                }

                return true;
            }

            if (parts.Length == 2)
            {
                name = parts[0];
                if (!TryParseBoolean(parts[1], out enabled))
                {
                    errorUsage = "简写用法：`<名称> <on|off>`";
                }

                return true;
            }

            return false;
        }

        internal static bool TryParseBoolean(string value, out bool result)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "true":
                case "on":
                case "enable":
                case "enabled":
                case "1":
                case "yes":
                    result = true;
                    return true;
                case "false":
                case "off":
                case "disable":
                case "disabled":
                case "0":
                case "no":
                    result = false;
                    return true;
                default:
                    result = false;
                    return false;
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
    }
}
