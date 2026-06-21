using System;
using System.Threading;
using System.Threading.Tasks;

namespace Emqo.KookBot_Unturned
{
    internal static class CleanupLoopShutdown
    {
        internal static void CancelWithoutBlocking(CancellationTokenSource cancellationTokenSource, Task cleanupTask)
        {
            if (cancellationTokenSource == null)
            {
                return;
            }

            try
            {
                cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (cleanupTask != null && !cleanupTask.IsCompleted)
            {
                cleanupTask.ContinueWith(
                    _ => cancellationTokenSource.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }

            cancellationTokenSource.Dispose();
        }
    }
}
