using System;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;
using Emqo.KookBot_Unturned.KookApi;
using Emqo.KookBot_Unturned.Interfaces;
using Emqo.KookBot_Unturned.Utilities;
using Rocket.Core.Logging;
using Rocket.Unturned.Player;
using Rocket.Unturned.Events;
using SDG.Unturned;
using Steamworks;
using Rocket.Unturned;
using System.Collections.Generic;
using System.Linq;
using Rocket.Unturned.Chat;

namespace Emqo.KookBot_Unturned
{
    public static partial class Events
    {
        #region 配置方法

        /// <summary>
        /// 启用/禁用事件转发
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            _isEnabled = enabled;
            if (ShouldLogDebug)
            {
                Logger.Log($"🎯 Events forwarding {(enabled ? "enabled" : "disabled")}");
            }
        }

        /// <summary>
        /// 更改转发频道
        /// </summary>
        public static void SetChannel(string channelId)
        {
            _channelId = channelId;
            if (ShouldLogDebug)
            {
                Logger.Log($"🎯 Events channel changed to: {channelId}");
            }
        }

        /// <summary>
        /// 启用/禁用特定事件
        /// </summary>
        public static void SetEventEnabled(string eventName, bool enabled)
        {
            if (Config?.TrySetGameToKookRuntime(eventName, enabled) == true)
            {
                if (ShouldLogDebug)
                {
                    Logger.Log($"🎯 Event '{eventName}' {(enabled ? "enabled" : "disabled")}");
                }
            }
        }

        /// <summary>
        /// 获取所有事件状态
        /// </summary>
        public static Dictionary<string, bool> GetAllEventsStatus()
        {
            return Config?.GetGameToKookSnapshot() ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        internal static string BuildDiagnosticsReport()
        {
            var config = Config;
            var eventSnapshot = config?.GetGameToKookSnapshot();
            var commandSnapshot = config?.GetCommandSnapshot();
            var eventSummary = eventSnapshot != null
                ? string.Join(", ", eventSnapshot.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={(pair.Value ? "on" : "off")}"))
                : "N/A";
            var commandSummary = commandSnapshot != null
                ? string.Join(", ", commandSnapshot.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={(pair.Value ? "on" : "off")}"))
                : "N/A";
            var send = KookSendQueue.Capture();
            var pvp = PvpEventRuntime.Capture();

            return new System.Text.StringBuilder()
                .AppendLine($"**插件运行时间**：`{KookBot_UnturnedPlugin.GetUptime():hh\\:mm\\:ss}`")
                .AppendLine($"**主线程队列**：`{MainThreadDispatcherGuard.PendingOperations}`")
                .AppendLine($"**KOOK 发送队列**：`{send.Pending}`")
                .AppendLine($"**KOOK 发送计数**：success=`{send.Success}`, canceled=`{send.Canceled}`, dropped=`{send.Dropped}`")
                .AppendLine($"**去重缓存**：`{EventDeduplicationStore.Count}`")
                .AppendLine($"**PvP 计数**：received=`{pvp.Received}`, queued=`{pvp.Queued}`, sent=`{pvp.Sent}`, canceled=`{pvp.Canceled}`, failed=`{pvp.Failed}`")
                .AppendLine($"**PvP 跳过**：disabled=`{pvp.Disabled}`, invalid=`{pvp.InvalidPlayer}`, self=`{pvp.SelfDamageSkipped}`, lowDamage=`{pvp.LowDamageSkipped}`, throttled=`{pvp.Throttled}`, backlogDrop=`{pvp.BacklogDrop}`")
                .AppendLine($"**事件同步开关**：`{eventSummary}`")
                .AppendLine($"**指令开关**：`{commandSummary}`")
                .ToString();
        }

        /// <summary>
        /// 发送自定义事件消息
        /// </summary>
        public static async Task SendCustomEvent(string eventTitle, string eventContent)
        {
            if (!_isEnabled || _message == null) return;

            try
            {
                var card = KookApi.KookCardFactory.BuildMarkdownCard("🎯", eventTitle,
                    string.IsNullOrWhiteSpace(eventContent) ? "_(无详细内容)_" : eventContent,
                    DateTimeOffset.Now,
                    "secondary");
                var sent = await SendBoundedKookMessageAsync(
                    async () => await _message.CreateMessageAsync(10, _channelId, card),
                    "CustomEvent");
                if (sent && ShouldLogDebug)
                {
                    Logger.Log($"📤 Custom event sent to KOOK: {eventTitle}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error sending custom event: {ex.Message}");
            }
        }

        #endregion
    }
}
