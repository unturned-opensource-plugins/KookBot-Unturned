using Emqo.KookBot_Unturned.Utilities;
using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class MainThreadDispatcherGuardTests : IDisposable
{
    public void Dispose()
    {
        MainThreadDispatcherGuard.ResetQueueOnMainThreadForTests();
    }

    [Fact]
    public void TryQueue_releases_pending_slot_when_enqueue_throws_synchronously()
    {
        MainThreadDispatcherGuard.SetQueueOnMainThreadForTests(_ => throw new InvalidOperationException("dispatcher down"));

        var queued = MainThreadDispatcherGuard.TryQueue(() => { });

        Assert.False(queued);
        Assert.Equal(0, MainThreadDispatcherGuard.PendingOperations);
    }
}
