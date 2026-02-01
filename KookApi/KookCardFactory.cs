using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Emqo.KookBot_Unturned.Monitoring;

namespace Emqo.KookBot_Unturned.KookApi
{
    internal static class KookCardFactory
    {
        public static string BuildStatusCard(ServerStatusSnapshot snapshot, string serverName)
        {
            if (snapshot == null)
            {
                return string.Empty;
            }

            var bodyLines = new[]
            {
                $"**TPS**：`{(snapshot.EstimatedTps <= 0 ? "N/A" : snapshot.EstimatedTps.ToString())}`",
                $"**在线人数**：`{snapshot.OnlinePlayers}/{snapshot.MaxPlayers}`",
                $"**排队人数**：`{snapshot.QueueLength}`",
                $"**运行时间**：`{FormatTimespan(snapshot.Uptime)}`"
            };

            var card = CreateCard(
                $"{serverName ?? "Unturned"} 状态面板",
                snapshot.EstimatedTps >= 30 ? "success" : "warning",
                string.Join("\n", bodyLines),
                $"最后刷新：`{snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss}`"
            );

            return JsonConvert.SerializeObject(card);
        }

        public static string BuildLifecycleCard(string title, string description, IEnumerable<(string Label, string Value)> fields, string theme = "primary")
        {
            var fieldLines = fields?
                .Select(pair => $"**{pair.Label}**：{pair.Value}")
                .ToArray() ?? Array.Empty<string>();

            var modules = new List<object>
            {
                new {
                    type = "header",
                    text = new { type = "plain-text", content = title }
                }
            };

            if (!string.IsNullOrWhiteSpace(description))
            {
                modules.Add(new
                {
                    type = "section",
                    text = new { type = "kmarkdown", content = description }
                });
            }

            if (fieldLines.Length > 0)
            {
                modules.Add(new
                {
                    type = "section",
                    text = new { type = "kmarkdown", content = string.Join("\n", fieldLines) }
                });
            }

            return JsonConvert.SerializeObject(CreateCardFromModules(modules, theme));
        }

        public static string BuildChatMessageCard(string chatScope, string playerName, string content, DateTimeOffset timestamp, string statusTag = "")
        {
            playerName = string.IsNullOrWhiteSpace(playerName) ? "未知玩家" : playerName;
            content = string.IsNullOrWhiteSpace(content) ? "（空消息）" : content;

            var modules = new List<object>
            {
                new
                {
                    type = "header",
                    text = new { type = "plain-text", content = $"💬 游戏聊天 ({chatScope})" }
                },
                new
                {
                    type = "section",
                    text = new { type = "kmarkdown", content = $"**{playerName}**：{content}" }
                },
                new
                {
                    type = "context",
                    elements = new object[]
                    {
                        new { type = "kmarkdown", content = $"发送时间：`{timestamp:HH:mm:ss}`" }
                    }
                }
            };

            return JsonConvert.SerializeObject(CreateCardFromModules(modules, "info"));
        }

        public static string BuildLifecycleCard(string titleEmoji, string title, string playerName, string steamId, int currentPlayers, int maxPlayers, DateTimeOffset timestamp, string theme)
        {
            var modules = new List<object>
            {
                new
                {
                    type = "header",
                    text = new { type = "plain-text", content = $"{titleEmoji} {title}" }
                },
                new
                {
                    type = "section",
                    text = new { type = "kmarkdown", content = $"**玩家**：`{playerName}`\n**Steam ID**：`{steamId}`\n**在线人数**：`{currentPlayers}/{maxPlayers}`" }
                },
                new
                {
                    type = "context",
                    elements = new object[]
                    {
                        new { type = "kmarkdown", content = $"时间：`{timestamp:yyyy-MM-dd HH:mm:ss}`" }
                    }
                }
            };

            return JsonConvert.SerializeObject(CreateCardFromModules(modules, theme));
        }

        public static string BuildGenericEventCard(string emoji, string title, IEnumerable<(string Label, string Value)> fields, DateTimeOffset timestamp, string theme = "secondary")
        {
            var modules = new List<object>
            {
                new
                {
                    type = "header",
                    text = new { type = "plain-text", content = $"{emoji} {title}" }
                }
            };

            var fieldLines = fields?
                .Where(field => !string.IsNullOrWhiteSpace(field.Label) && !string.IsNullOrWhiteSpace(field.Value))
                .Select(field => $"**{field.Label}**：`{field.Value}`")
                .ToArray();

            if (fieldLines is { Length: > 0 })
            {
                modules.Add(new
                {
                    type = "section",
                    text = new { type = "kmarkdown", content = string.Join("\n", fieldLines) }
                });
            }

            modules.Add(new
            {
                type = "context",
                elements = new object[]
                {
                    new { type = "kmarkdown", content = $"时间：`{timestamp:yyyy-MM-dd HH:mm:ss}`" }
                }
            });

            return JsonConvert.SerializeObject(CreateCardFromModules(modules, theme));
        }

        public static string BuildMarkdownCard(string emoji, string title, string markdownBody, DateTimeOffset timestamp, string theme = "secondary")
        {
            var modules = new List<object>
            {
                new
                {
                    type = "header",
                    text = new { type = "plain-text", content = $"{emoji} {title}" }
                }
            };

            if (!string.IsNullOrWhiteSpace(markdownBody))
            {
                modules.Add(new
                {
                    type = "section",
                    text = new { type = "kmarkdown", content = markdownBody }
                });
            }

            modules.Add(new
            {
                type = "context",
                elements = new object[]
                {
                    new { type = "kmarkdown", content = $"时间：`{timestamp:yyyy-MM-dd HH:mm:ss}`" }
                }
            });

            return JsonConvert.SerializeObject(CreateCardFromModules(modules, theme));
        }

        private static string FormatTimespan(TimeSpan uptime)
        {
            if (uptime <= TimeSpan.Zero)
            {
                return "N/A";
            }

            if (uptime.TotalHours >= 24)
            {
                return $"{(int)uptime.TotalDays} 天 {uptime.Hours} 小时";
            }

            if (uptime.TotalHours >= 1)
            {
                return $"{(int)uptime.TotalHours} 小时 {uptime.Minutes} 分";
            }

            return $"{uptime.Minutes} 分 {uptime.Seconds} 秒";
        }

        private static object[] CreateCard(string title, string theme, string body, string footer)
        {
            var modules = new List<object>
            {
                new
                {
                    type = "header",
                    text = new { type = "plain-text", content = title }
                }
            };

            if (!string.IsNullOrWhiteSpace(body))
            {
                modules.Add(new
                {
                    type = "section",
                    text = new { type = "kmarkdown", content = body }
                });
            }

            if (!string.IsNullOrWhiteSpace(footer))
            {
                modules.Add(new
                {
                    type = "context",
                    elements = new object[]
                    {
                        new
                        {
                            type = "kmarkdown",
                            content = footer
                        }
                    }
                });
            }

            return CreateCardFromModules(modules, theme);
        }

        private static object[] CreateCardFromModules(List<object> modules, string theme)
        {
            return new[]
            {
                new
                {
                    type = "card",
                    theme = theme ?? "secondary",
                    size = "lg",
                    modules = modules.ToArray()
                }
            };
        }
    }
}
