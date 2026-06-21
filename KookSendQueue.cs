using System;
using System.Threading;
using System.Threading.Tasks;
using Rocket.Core.Logging;

namespace Emqo.KookBot_Unturned
{
    internal static class KookSendQueue
    {
        private const int MaxConcurrentSends = 4;
        private const int MaxPendingSends = 128;

        private static readonly SemaphoreSlim SendSemaphore = new(MaxConcurrentSends, MaxConcurrentSends);
        private static int _pendingCount;
        private static long _successCount;
        private static long _canceledCount;
        private static long _droppedCount;
        private static CancellationTokenSource _sendCancellationTokenSource = new CancellationTokenSource();

        internal static async Task<bool> SendAsync(Func<Task> sendOperation, string context, bool shouldLogDebug, Action<string> diagnosticsCallback)
        {
            if (sendOperation == null)
            {
                throw new ArgumentNullException(nameof(sendOperation));
            }

            var isPvpContext = IsPvpContext(context);
            if (!TryReserveSlot(context, diagnosticsCallback))
            {
                return false;
            }
            if (isPvpContext)
            {
                PvpEventRuntime.Queued();
            }

            bool semaphoreAcquired = false;
            var cancellationTokenSource = _sendCancellationTokenSource;

            try
            {
                if (cancellationTokenSource == null || cancellationTokenSource.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _canceledCount);
                    if (isPvpContext)
                    {
                        PvpEventRuntime.Canceled();
                    }

                    return false;
                }

                await SendSemaphore.WaitAsync(cancellationTokenSource.Token);
                semaphoreAcquired = true;

                if (cancellationTokenSource.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _canceledCount);
                    if (isPvpContext)
                    {
                        PvpEventRuntime.Canceled();
                    }

                    return false;
                }

                await sendOperation();
                Interlocked.Increment(ref _successCount);
                if (isPvpContext)
                {
                    PvpEventRuntime.Sent();
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _canceledCount);
                if (isPvpContext)
                {
                    PvpEventRuntime.Canceled();
                }
                if (shouldLogDebug)
                {
                    Logger.Log($"ℹ️ KOOK send canceled during shutdown: {context}");
                }

                return false;
            }
            finally
            {
                if (semaphoreAcquired)
                {
                    SendSemaphore.Release();
                }

                Interlocked.Decrement(ref _pendingCount);
                diagnosticsCallback?.Invoke(context);
            }
        }

        internal static void Cancel()
        {
            _sendCancellationTokenSource?.Cancel();
        }

        internal static void ResetCancellation()
        {
            var replacement = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _sendCancellationTokenSource, replacement);
            previous?.Cancel();
            previous?.Dispose();
        }

        internal static void ResetMetrics()
        {
            Interlocked.Exchange(ref _successCount, 0);
            Interlocked.Exchange(ref _canceledCount, 0);
            Interlocked.Exchange(ref _droppedCount, 0);
        }

        internal static Snapshot Capture() => new Snapshot
        {
            Pending = Volatile.Read(ref _pendingCount),
            Success = Volatile.Read(ref _successCount),
            Canceled = Volatile.Read(ref _canceledCount),
            Dropped = Volatile.Read(ref _droppedCount)
        };

        private static bool TryReserveSlot(string context, Action<string> diagnosticsCallback)
        {
            while (true)
            {
                var current = Volatile.Read(ref _pendingCount);
                if (current >= MaxPendingSends)
                {
                    Interlocked.Increment(ref _droppedCount);
                    if (IsPvpContext(context))
                    {
                        PvpEventRuntime.BacklogDrop();
                    }

                    Logger.LogWarning($"⚠️ KOOK send backlog reached {current}, dropping event: {context}");
                    diagnosticsCallback?.Invoke($"{context}:backlog-drop");
                    return false;
                }

                if (Interlocked.CompareExchange(ref _pendingCount, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        private static bool IsPvpContext(string context)
        {
            return string.Equals(context, "PlayerDamaged", StringComparison.Ordinal);
        }

        internal class Snapshot
        {
            public int Pending { get; set; }
            public long Success { get; set; }
            public long Canceled { get; set; }
            public long Dropped { get; set; }
        }
    }
}
