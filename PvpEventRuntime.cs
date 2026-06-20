using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Rocket.Core.Logging;

namespace Emqo.KookBot_Unturned
{
    internal static class PvpEventRuntime
    {
        internal const float MinDamageToReport = 50f;
        private const int PairThrottleMs = 15000;
        private const int MaxThrottleCacheSize = 2048;
        private const int ErrorLogIntervalMs = 60000;

        private static readonly ConcurrentDictionary<string, DateTimeOffset> LastEventByPair = new();
        private static long _lastErrorLogTicks;

        private static long _received;
        private static long _disabled;
        private static long _invalidPlayer;
        private static long _selfDamageSkipped;
        private static long _lowDamageSkipped;
        private static long _throttled;
        private static long _queued;
        private static long _sent;
        private static long _canceled;
        private static long _failed;
        private static long _backlogDrop;

        internal static void Received() => Interlocked.Increment(ref _received);
        internal static void Disabled() => Interlocked.Increment(ref _disabled);
        internal static void InvalidPlayer() => Interlocked.Increment(ref _invalidPlayer);
        internal static void SelfDamageSkipped() => Interlocked.Increment(ref _selfDamageSkipped);
        internal static void LowDamageSkipped() => Interlocked.Increment(ref _lowDamageSkipped);
        internal static void Throttled() => Interlocked.Increment(ref _throttled);
        internal static void Queued() => Interlocked.Increment(ref _queued);
        internal static void Sent() => Interlocked.Increment(ref _sent);
        internal static void Canceled() => Interlocked.Increment(ref _canceled);
        internal static void Failed() => Interlocked.Increment(ref _failed);
        internal static void BacklogDrop() => Interlocked.Increment(ref _backlogDrop);

        internal static bool ShouldSend(string key, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return true;
            }

            if (LastEventByPair.TryGetValue(key, out var lastSent) &&
                (now - lastSent).TotalMilliseconds < PairThrottleMs)
            {
                return false;
            }

            LastEventByPair[key] = now;

            if (LastEventByPair.Count > MaxThrottleCacheSize)
            {
                foreach (var staleKey in LastEventByPair
                    .Where(pair => (now - pair.Value).TotalMilliseconds > PairThrottleMs * 4)
                    .Select(pair => pair.Key)
                    .Take(256)
                    .ToList())
                {
                    LastEventByPair.TryRemove(staleKey, out _);
                }
            }

            return true;
        }

        internal static void LogErrorRateLimited(string message, Exception ex, bool shouldLogDebug)
        {
            var nowTicks = DateTimeOffset.UtcNow.Ticks;
            var previousTicks = Interlocked.Read(ref _lastErrorLogTicks);
            if (previousTicks != 0 && nowTicks - previousTicks < ErrorLogIntervalMs * TimeSpan.TicksPerMillisecond)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastErrorLogTicks, nowTicks, previousTicks) != previousTicks)
            {
                return;
            }

            Logger.LogError(message);
            if (shouldLogDebug && ex?.StackTrace != null)
            {
                Logger.LogError($"Stack trace: {ex.StackTrace}");
            }
        }

        internal static void Reset()
        {
            Interlocked.Exchange(ref _received, 0);
            Interlocked.Exchange(ref _disabled, 0);
            Interlocked.Exchange(ref _invalidPlayer, 0);
            Interlocked.Exchange(ref _selfDamageSkipped, 0);
            Interlocked.Exchange(ref _lowDamageSkipped, 0);
            Interlocked.Exchange(ref _throttled, 0);
            Interlocked.Exchange(ref _queued, 0);
            Interlocked.Exchange(ref _sent, 0);
            Interlocked.Exchange(ref _canceled, 0);
            Interlocked.Exchange(ref _failed, 0);
            Interlocked.Exchange(ref _backlogDrop, 0);
            Interlocked.Exchange(ref _lastErrorLogTicks, 0);
            LastEventByPair.Clear();
        }

        internal static Snapshot Capture() => new Snapshot
        {
            Received = Volatile.Read(ref _received),
            Disabled = Volatile.Read(ref _disabled),
            InvalidPlayer = Volatile.Read(ref _invalidPlayer),
            SelfDamageSkipped = Volatile.Read(ref _selfDamageSkipped),
            LowDamageSkipped = Volatile.Read(ref _lowDamageSkipped),
            Throttled = Volatile.Read(ref _throttled),
            Queued = Volatile.Read(ref _queued),
            Sent = Volatile.Read(ref _sent),
            Canceled = Volatile.Read(ref _canceled),
            Failed = Volatile.Read(ref _failed),
            BacklogDrop = Volatile.Read(ref _backlogDrop)
        };

        internal class Snapshot
        {
            public long Received { get; set; }
            public long Disabled { get; set; }
            public long InvalidPlayer { get; set; }
            public long SelfDamageSkipped { get; set; }
            public long LowDamageSkipped { get; set; }
            public long Throttled { get; set; }
            public long Queued { get; set; }
            public long Sent { get; set; }
            public long Canceled { get; set; }
            public long Failed { get; set; }
            public long BacklogDrop { get; set; }
        }
    }
}
