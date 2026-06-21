using Emqo.KookBot_Unturned;
using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class PvpEventRuntimeTests : IDisposable
{
    public PvpEventRuntimeTests()
    {
        PvpEventRuntime.Reset();
    }

    public void Dispose()
    {
        PvpEventRuntime.Reset();
    }

    [Fact]
    public void ShouldSend_throttles_same_pair_inside_window()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(PvpEventRuntime.ShouldSend("attacker:victim", now));
        Assert.False(PvpEventRuntime.ShouldSend("attacker:victim", now.AddSeconds(1)));
        Assert.True(PvpEventRuntime.ShouldSend("attacker:victim", now.AddSeconds(16)));
    }

    [Fact]
    public void ShouldSend_allows_empty_key_without_throttling()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(PvpEventRuntime.ShouldSend("", now));
        Assert.True(PvpEventRuntime.ShouldSend("", now));
    }

    [Fact]
    public void Counters_are_resettable_and_captured_consistently()
    {
        PvpEventRuntime.Received();
        PvpEventRuntime.Disabled();
        PvpEventRuntime.InvalidPlayer();
        PvpEventRuntime.SelfDamageSkipped();
        PvpEventRuntime.LowDamageSkipped();
        PvpEventRuntime.Throttled();
        PvpEventRuntime.Queued();
        PvpEventRuntime.Sent();
        PvpEventRuntime.Canceled();
        PvpEventRuntime.Failed();
        PvpEventRuntime.BacklogDrop();

        var snapshot = PvpEventRuntime.Capture();

        Assert.Equal(1, snapshot.Received);
        Assert.Equal(1, snapshot.Disabled);
        Assert.Equal(1, snapshot.InvalidPlayer);
        Assert.Equal(1, snapshot.SelfDamageSkipped);
        Assert.Equal(1, snapshot.LowDamageSkipped);
        Assert.Equal(1, snapshot.Throttled);
        Assert.Equal(1, snapshot.Queued);
        Assert.Equal(1, snapshot.Sent);
        Assert.Equal(1, snapshot.Canceled);
        Assert.Equal(1, snapshot.Failed);
        Assert.Equal(1, snapshot.BacklogDrop);

        PvpEventRuntime.Reset();
        Assert.Equal(0, PvpEventRuntime.Capture().Received);
    }
}
