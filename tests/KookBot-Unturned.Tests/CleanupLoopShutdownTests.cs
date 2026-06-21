using System.Diagnostics;
using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class CleanupLoopShutdownTests
{
    [Fact]
    public void CancelWithoutBlocking_returns_without_waiting_for_incomplete_cleanup_task()
    {
        using var cancellation = new CancellationTokenSource();
        var cleanup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        CleanupLoopShutdown.CancelWithoutBlocking(cancellation, cleanup.Task);

        stopwatch.Stop();
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(stopwatch.ElapsedMilliseconds < 200);
    }
}
