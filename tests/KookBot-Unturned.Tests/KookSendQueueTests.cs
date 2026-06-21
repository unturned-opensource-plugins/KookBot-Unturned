using Emqo.KookBot_Unturned;
using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class KookSendQueueTests : IDisposable
{
    public KookSendQueueTests()
    {
        KookSendQueue.ResetCancellation();
        KookSendQueue.ResetMetrics();
        PvpEventRuntime.Reset();
    }

    public void Dispose()
    {
        KookSendQueue.Cancel();
        KookSendQueue.ResetCancellation();
        KookSendQueue.ResetMetrics();
        PvpEventRuntime.Reset();
    }

    [Fact]
    public async Task SendAsync_records_success_and_pvp_sent_metrics()
    {
        var called = false;

        var result = await KookSendQueue.SendAsync(
            () =>
            {
                called = true;
                return Task.CompletedTask;
            },
            "PlayerDamaged",
            shouldLogDebug: false,
            diagnosticsCallback: null);

        var queue = KookSendQueue.Capture();
        var pvp = PvpEventRuntime.Capture();

        Assert.True(result);
        Assert.True(called);
        Assert.Equal(0, queue.Pending);
        Assert.Equal(1, queue.Success);
        Assert.Equal(1, pvp.Queued);
        Assert.Equal(1, pvp.Sent);
        Assert.Equal(0, pvp.Canceled);
    }

    [Fact]
    public async Task SendAsync_after_cancel_does_not_call_operation_and_records_cancellation()
    {
        KookSendQueue.Cancel();
        var called = false;

        var result = await KookSendQueue.SendAsync(
            () =>
            {
                called = true;
                return Task.CompletedTask;
            },
            "PlayerDamaged",
            shouldLogDebug: false,
            diagnosticsCallback: null);

        var queue = KookSendQueue.Capture();
        var pvp = PvpEventRuntime.Capture();

        Assert.False(result);
        Assert.False(called);
        Assert.Equal(0, queue.Pending);
        Assert.Equal(0, queue.Success);
        Assert.Equal(1, queue.Canceled);
        Assert.Equal(1, pvp.Queued);
        Assert.Equal(1, pvp.Canceled);
        Assert.Equal(0, pvp.Sent);
    }

    [Fact]
    public async Task SendAsync_invokes_diagnostics_callback_when_operation_completes()
    {
        var contexts = new List<string>();

        await KookSendQueue.SendAsync(
            () => Task.CompletedTask,
            "ChatMessage",
            shouldLogDebug: false,
            diagnosticsCallback: contexts.Add);

        Assert.Equal(new[] { "ChatMessage" }, contexts);
    }
}
