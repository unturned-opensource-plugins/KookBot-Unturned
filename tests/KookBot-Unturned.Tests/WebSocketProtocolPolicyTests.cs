using Emqo.KookBot_Unturned;
using Emqo.KookBot_Unturned.KookApi;
using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class WebSocketProtocolPolicyTests
{
    [Theory]
    [InlineData(30, 0, 30000)]
    [InlineData(30, 2000, 32000)]
    [InlineData(30, -2000, 28000)]
    [InlineData(0, -9999, 1000)]
    public void CalculateHeartbeatIntervalMs_applies_offset_and_minimum(int baseSeconds, int offsetMs, int expected)
    {
        Assert.Equal(expected, WebSocketProtocolPolicy.CalculateHeartbeatIntervalMs(baseSeconds, offsetMs));
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1, 2000)]
    [InlineData(2, 4000)]
    public void CalculateInitialReconnectDelayMs_matches_existing_exponential_schedule(int failedAttemptNumber, int expected)
    {
        Assert.Equal(expected, WebSocketProtocolPolicy.CalculateInitialReconnectDelayMs(failedAttemptNumber));
    }

    [Theory]
    [InlineData(1000, 60000, 2000)]
    [InlineData(32000, 60000, 60000)]
    [InlineData(60000, 60000, 60000)]
    [InlineData(0, 60000, 1000)]
    public void CalculateNextInfiniteRetryDelayMs_doubles_until_cap(int currentDelayMs, int maxDelayMs, int expected)
    {
        Assert.Equal(expected, WebSocketProtocolPolicy.CalculateNextInfiniteRetryDelayMs(currentDelayMs, maxDelayMs));
    }

    [Theory]
    [InlineData(9, 9L)]
    [InlineData((long)10, 10L)]
    [InlineData((short)11, 11L)]
    [InlineData((byte)12, 12L)]
    public void TryGetNumericMessageType_accepts_integral_runtime_types(object raw, long expected)
    {
        Assert.True(WebSocketProtocolPolicy.TryGetNumericMessageType(raw, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("9")]
    [InlineData(9.0)]
    public void TryGetNumericMessageType_rejects_non_integral_types(object raw)
    {
        Assert.False(WebSocketProtocolPolicy.TryGetNumericMessageType(raw, out var actual));
        Assert.Equal(0, actual);
    }

    [Fact]
    public void DecideIncomingText_ignores_bot_disabled_wrong_channel_null_config_and_blank_content()
    {
        var config = Config();

        Assert.Equal(IncomingTextAction.Ignore, WebSocketProtocolPolicy.DecideIncomingText("bot", "nick", "channel", "hello", "bot", config, 500).Action);
        Assert.Equal(IncomingTextAction.Ignore, WebSocketProtocolPolicy.DecideIncomingText("user", "nick", "channel", "hello", "bot", null, 500).Action);
        config.KookToGame = false;
        Assert.Equal(IncomingTextAction.Ignore, WebSocketProtocolPolicy.DecideIncomingText("user", "nick", "channel", "hello", "bot", config, 500).Action);
        config.KookToGame = true;
        Assert.Equal(IncomingTextAction.Ignore, WebSocketProtocolPolicy.DecideIncomingText("user", "nick", "other", "hello", "bot", config, 500).Action);
        Assert.Equal(IncomingTextAction.Ignore, WebSocketProtocolPolicy.DecideIncomingText("user", "nick", "channel", null, "bot", config, 500).Action);
        Assert.Equal(IncomingTextAction.Ignore, WebSocketProtocolPolicy.DecideIncomingText("user", "nick", "channel", "   ", "bot", config, 500).Action);
    }

    [Fact]
    public void DecideIncomingText_routes_commands_without_sanitizing_command_text()
    {
        var decision = WebSocketProtocolPolicy.DecideIncomingText("user", "<nick>", "channel", "/status <x>", "bot", Config(), 500);

        Assert.Equal(IncomingTextAction.Command, decision.Action);
        Assert.Equal("<nick>", decision.Nickname);
        Assert.Equal("/status <x>", decision.Content);
        Assert.Null(decision.FormattedMessage);
    }

    [Fact]
    public void DecideIncomingText_forwards_sanitized_prefixed_message()
    {
        var decision = WebSocketProtocolPolicy.DecideIncomingText("user", "<Nick>", "channel", "hello [x]\\", "bot", Config(), 500);

        Assert.Equal(IncomingTextAction.ForwardToGame, decision.Action);
        Assert.Equal("＜Nick＞", decision.Nickname);
        Assert.Equal("hello ［x］＼", decision.Content);
        Assert.Equal("[U] ＜Nick＞: hello ［x］＼", decision.FormattedMessage);
        Assert.False(decision.WasTruncated);
    }

    [Fact]
    public void DecideIncomingText_truncates_before_sanitizing_and_can_disable_prefix()
    {
        var config = Config();
        config.EnableSync = false;

        var decision = WebSocketProtocolPolicy.DecideIncomingText("user", null, "channel", "abcdef", "bot", config, 3);

        Assert.Equal(IncomingTextAction.ForwardToGame, decision.Action);
        Assert.Equal(WebSocketProtocolPolicy.DefaultNickname, decision.Nickname);
        Assert.Equal("abc...", decision.Content);
        Assert.Equal("KOOK用户: abc...", decision.FormattedMessage);
        Assert.True(decision.WasTruncated);
    }

    private static KookBot_UnturnedConfiguration Config() => new KookBot_UnturnedConfiguration
    {
        ChannelId = "channel",
        KookToGame = true,
        EnableSync = true,
        MessagePrefix = "[U]"
    };
}
