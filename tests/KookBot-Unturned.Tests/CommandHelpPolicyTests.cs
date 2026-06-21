using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class CommandHelpPolicyTests
{
    [Fact]
    public void Admin_help_lists_current_rescue_entry_shortcuts()
    {
        var config = new KookBot_UnturnedConfiguration();
        config.LoadDefaults();

        var help = CommandHelpPolicy.BuildHelpText(config, isAdmin: true);

        Assert.Contains("`/event list` / `/event status`", help);
        Assert.Contains("`/event <事件名> on|off`", help);
        Assert.Contains("`/event set <事件名> <true|false>`", help);
        Assert.Contains("`/command <指令名> on|off`", help);
        Assert.Contains("`/command set <指令名> <true|false>`", help);
    }
}
