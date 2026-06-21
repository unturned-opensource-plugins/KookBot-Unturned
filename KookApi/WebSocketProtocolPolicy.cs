using System;
using Emqo.KookBot_Unturned.Utilities;

namespace Emqo.KookBot_Unturned.KookApi
{
    internal static class WebSocketProtocolPolicy
    {
        internal const string DefaultAuthorId = "000000";
        internal const string DefaultNickname = "KOOK用户";
        internal const int MinimumHeartbeatIntervalMs = 1000;

        internal static int CalculateHeartbeatIntervalMs(int baseIntervalSeconds, int randomOffsetMs)
        {
            var interval = checked(baseIntervalSeconds * 1000 + randomOffsetMs);
            return interval < MinimumHeartbeatIntervalMs ? MinimumHeartbeatIntervalMs : interval;
        }

        internal static int CalculateInitialReconnectDelayMs(int failedAttemptNumber)
        {
            if (failedAttemptNumber <= 0)
            {
                return 1000;
            }

            return (int)Math.Pow(2, failedAttemptNumber) * 1000;
        }

        internal static int CalculateNextInfiniteRetryDelayMs(int currentDelayMs, int maxDelayMs)
        {
            if (currentDelayMs <= 0)
            {
                return Math.Min(1000, maxDelayMs);
            }

            if (maxDelayMs <= 0)
            {
                return currentDelayMs;
            }

            return Math.Min(currentDelayMs * 2, maxDelayMs);
        }

        internal static bool TryGetNumericMessageType(object rawType, out long messageType)
        {
            switch (rawType)
            {
                case int intValue:
                    messageType = intValue;
                    return true;
                case long longValue:
                    messageType = longValue;
                    return true;
                case short shortValue:
                    messageType = shortValue;
                    return true;
                case byte byteValue:
                    messageType = byteValue;
                    return true;
                default:
                    messageType = 0;
                    return false;
            }
        }

        internal static IncomingTextDecision DecideIncomingText(
            string authorId,
            string nickname,
            string channelId,
            string content,
            string botAuthId,
            KookBot_UnturnedConfiguration config,
            int maxMessageLength)
        {
            authorId = string.IsNullOrWhiteSpace(authorId) ? DefaultAuthorId : authorId;
            nickname = string.IsNullOrWhiteSpace(nickname) ? DefaultNickname : nickname;

            if (config == null ||
                !config.KookToGame ||
                string.Equals(authorId, botAuthId, StringComparison.Ordinal) ||
                !string.Equals(channelId, config.ChannelId, StringComparison.Ordinal))
            {
                return IncomingTextDecision.Ignore();
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return IncomingTextDecision.Ignore();
            }

            if (content.StartsWith("/", StringComparison.Ordinal))
            {
                return IncomingTextDecision.Command(nickname, content);
            }

            var wasTruncated = false;
            if (maxMessageLength > 0 && content.Length > maxMessageLength)
            {
                content = content.Substring(0, maxMessageLength) + "...";
                wasTruncated = true;
            }

            var sanitizedNickname = StringUtils.SanitizeRichText(nickname);
            var sanitizedContent = StringUtils.SanitizeRichText(content);
            var formatted = config.EnableSync
                ? $"{config.MessagePrefix} {sanitizedNickname}: {sanitizedContent}"
                : $"{sanitizedNickname}: {sanitizedContent}";

            return IncomingTextDecision.Forward(sanitizedNickname, sanitizedContent, formatted, wasTruncated);
        }
    }

    internal sealed class IncomingTextDecision
    {
        private IncomingTextDecision() { }

        internal IncomingTextAction Action { get; private set; }
        internal string Nickname { get; private set; }
        internal string Content { get; private set; }
        internal string FormattedMessage { get; private set; }
        internal bool WasTruncated { get; private set; }

        internal static IncomingTextDecision Ignore() => new IncomingTextDecision { Action = IncomingTextAction.Ignore };

        internal static IncomingTextDecision Command(string nickname, string content) => new IncomingTextDecision
        {
            Action = IncomingTextAction.Command,
            Nickname = nickname,
            Content = content
        };

        internal static IncomingTextDecision Forward(string nickname, string content, string formattedMessage, bool wasTruncated) => new IncomingTextDecision
        {
            Action = IncomingTextAction.ForwardToGame,
            Nickname = nickname,
            Content = content,
            FormattedMessage = formattedMessage,
            WasTruncated = wasTruncated
        };
    }

    internal enum IncomingTextAction
    {
        Ignore,
        Command,
        ForwardToGame
    }
}
