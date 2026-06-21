using Emqo.KookBot_Unturned;
using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class LifecycleEventPolicyTests
{
    [Fact]
    public void ShouldSendServerStop_uses_ServerStop_switch_not_ServerStart()
    {
        var config = new KookBot_UnturnedConfiguration();
        config.LoadDefaults();
        config.TrySetGameToKookRuntime("ServerStart", false);
        config.TrySetGameToKookRuntime("ServerStop", true);

        Assert.True(LifecycleEventPolicy.ShouldSendServerStop(config, isFullyLoaded: true, hasMessageApi: true));

        config.TrySetGameToKookRuntime("ServerStart", true);
        config.TrySetGameToKookRuntime("ServerStop", false);

        Assert.False(LifecycleEventPolicy.ShouldSendServerStop(config, isFullyLoaded: true, hasMessageApi: true));
    }
}
