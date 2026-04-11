using System;
using System.Threading;
using System.Threading.Tasks;

namespace Emqo.KookBot_Unturned.Utilities
{
    internal static class MainThreadDispatcherGuard
    {
        private const int MaxPendingOperations = 64;
        private static int _pendingOperations;

        internal static int PendingOperations => Volatile.Read(ref _pendingOperations);

        internal static bool TryQueue(Action action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            return TryQueue(_ => action(), CancellationToken.None);
        }

        internal static bool TryQueue(Action<CancellationToken> action, CancellationToken cancellationToken)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            while (true)
            {
                var current = Volatile.Read(ref _pendingOperations);
                if (current >= MaxPendingOperations)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _pendingOperations, current + 1, current) == current)
                {
                    Rocket.Core.Utils.TaskDispatcher.QueueOnMainThread(() =>
                    {
                        try
                        {
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                action(cancellationToken);
                            }
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _pendingOperations);
                        }
                    });

                    return true;
                }
            }
        }

        internal static async Task<T> WaitAsync<T>(
            Task<T> task,
            string operationName,
            TimeSpan timeout,
            CancellationTokenSource operationCancellationTokenSource = null)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            using var timeoutCts = new CancellationTokenSource();
            var timeoutTask = Task.Delay(timeout, timeoutCts.Token);
            var completedTask = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
            if (completedTask != task)
            {
                operationCancellationTokenSource?.Cancel();
                throw new TimeoutException($"{operationName} timed out after {timeout.TotalSeconds:0} seconds.");
            }

            timeoutCts.Cancel();
            return await task.ConfigureAwait(false);
        }
    }
}
