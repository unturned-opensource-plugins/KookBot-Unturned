using Emqo.KookBot_Unturned;
using Emqo.KookBot_Unturned.KookApi;
using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class KookAdminCommandHandlerTests
{
    [Theory]
    [InlineData("event")]
    [InlineData("command")]
    [InlineData("diag")]
    [InlineData("EVENT")]
    public void IsRescueCommand_recognizes_rescue_commands_case_insensitively(string command)
    {
        Assert.True(KookAdminCommandHandler.IsRescueCommand(command));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("help")]
    [InlineData("cmd")]
    public void IsRescueCommand_rejects_non_rescue_commands(string command)
    {
        Assert.False(KookAdminCommandHandler.IsRescueCommand(command));
    }

    [Theory]
    [InlineData("event")]
    [InlineData("command")]
    [InlineData("diag")]
    public void Rescue_commands_are_not_mutable_command_switches(string command)
    {
        Assert.False(KookAdminCommandHandler.IsMutableCommandSwitch(command));
    }

    [Theory]
    [InlineData("help")]
    [InlineData("status")]
    [InlineData("cmd")]
    [InlineData("console")]
    [InlineData("exec")]
    public void Normal_commands_are_mutable_command_switches(string command)
    {
        Assert.True(KookAdminCommandHandler.IsMutableCommandSwitch(command));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("on", true)]
    [InlineData("enable", true)]
    [InlineData("enabled", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("FALSE", false)]
    [InlineData("off", false)]
    [InlineData("disable", false)]
    [InlineData("disabled", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    public void TryParseBoolean_accepts_supported_runtime_words(string value, bool expected)
    {
        Assert.True(KookAdminCommandHandler.TryParseBoolean(value, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("maybe")]
    public void TryParseBoolean_rejects_unknown_values(string value)
    {
        Assert.False(KookAdminCommandHandler.TryParseBoolean(value, out var actual));
        Assert.False(actual);
    }

    [Fact]
    public void SplitArgs_trims_empty_segments()
    {
        Assert.Equal(new[] { "set", "PlayerDamaged", "off" }, KookAdminCommandHandler.SplitArgs("  set   PlayerDamaged   off  "));
        Assert.Empty(KookAdminCommandHandler.SplitArgs("   "));
        Assert.Empty(KookAdminCommandHandler.SplitArgs(null));
    }

    [Fact]
    public void SetCommandRuntime_updates_all_console_aliases_as_a_group()
    {
        var config = new KookBot_UnturnedConfiguration();
        config.LoadDefaults();

        var changed = KookAdminCommandHandler.SetCommandRuntime(config, "cmd", false);

        Assert.Equal(new[] { "cmd", "console", "exec" }, changed);
        Assert.False(config.IsCommandEnabled("cmd"));
        Assert.False(config.IsCommandEnabled("console"));
        Assert.False(config.IsCommandEnabled("exec"));
    }

    [Fact]
    public void SetCommandRuntime_updates_single_non_console_command()
    {
        var config = new KookBot_UnturnedConfiguration();
        config.LoadDefaults();

        var changed = KookAdminCommandHandler.SetCommandRuntime(config, "status", false);

        Assert.Equal(new[] { "status" }, changed);
        Assert.False(config.IsCommandEnabled("status"));
        Assert.True(config.IsCommandEnabled("help"));
    }

    [Fact]
    public async Task Event_shorthand_updates_runtime_event_switch()
    {
        var config = new KookBot_UnturnedConfiguration();
        config.LoadDefaults();
        Events.RuntimeEvents.Clear();
        var message = new Message();

        await KookAdminCommandHandler.HandleAsync("event", "PlayerDamaged off", "admin", "channel", isAdmin: true, message, config);

        Assert.False(Events.RuntimeEvents["PlayerDamaged"]);
        Assert.Contains("PlayerDamaged", message.Sent.Single().Content.ToString());
    }

    [Fact]
    public async Task Command_shorthand_updates_runtime_command_switch()
    {
        var config = new KookBot_UnturnedConfiguration();
        config.LoadDefaults();
        var message = new Message();

        await KookAdminCommandHandler.HandleAsync("command", "status off", "admin", "channel", isAdmin: true, message, config);

        Assert.False(config.IsCommandEnabled("status"));
        Assert.Contains("status", message.Sent.Single().Content.ToString());
    }

    [Fact]
    public async Task Event_status_alias_lists_event_switches()
    {
        var config = new KookBot_UnturnedConfiguration();
        config.LoadDefaults();
        var message = new Message();

        await KookAdminCommandHandler.HandleAsync("event", "status", "admin", "channel", isAdmin: true, message, config);

        Assert.Contains("PlayerDamaged", message.Sent.Single().Content.ToString());
    }

    [Fact]
    public async Task Event_invalid_boolean_reply_includes_full_event_usage()
    {
        var config = new KookBot_UnturnedConfiguration();
        config.LoadDefaults();
        var message = new Message();

        await KookAdminCommandHandler.HandleAsync("event", "PlayerDamaged maybe", "admin", "channel", isAdmin: true, message, config);

        var content = message.Sent.Single().Content.ToString();
        Assert.Contains("/event set", content);
        Assert.Contains("/event <事件名>", content);
    }
}
