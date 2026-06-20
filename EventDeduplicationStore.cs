using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Rocket.Core.Logging;

namespace Emqo.KookBot_Unturned
{
    internal static class EventDeduplicationStore
    {
        private const int DeduplicationWindowMs = 1000;
        private const int MaxCacheSize = 1000;
        private const int CacheCleanupIntervalMs = 60000;

        private static readonly ConcurrentDictionary<string, DateTimeOffset> EventCache = new();
        private static long _lastCacheCleanupTicks;

        internal static int Count => EventCache.Count;

        internal static bool ShouldProcess(string eventKey, bool shouldLogDebug)
        {
            var now = DateTimeOffset.UtcNow;
            var nowTicks = now.Ticks;
            if (nowTicks - Interlocked.Read(ref _lastCacheCleanupTicks) > CacheCleanupIntervalMs * TimeSpan.TicksPerMillisecond)
            {
                Cleanup(now, shouldLogDebug);
                Interlocked.Exchange(ref _lastCacheCleanupTicks, nowTicks);
            }

            if (EventCache.TryGetValue(eventKey, out var lastTime))
            {
                var timeSinceLastEvent = (now - lastTime).TotalMilliseconds;
                if (timeSinceLastEvent < DeduplicationWindowMs)
                {
                    if (shouldLogDebug)
                    {
                        Logger.LogWarning($"⚠️ Duplicate event detected: {eventKey} (within {DeduplicationWindowMs}ms), skipping...");
                    }

                    return false;
                }
            }

            EventCache[eventKey] = now;
            return true;
        }

        internal static void Reset()
        {
            EventCache.Clear();
            Interlocked.Exchange(ref _lastCacheCleanupTicks, 0);
        }

        private static void Cleanup(DateTimeOffset now, bool shouldLogDebug)
        {
            try
            {
                var expiredKeys = EventCache
                    .Where(kvp => (now - kvp.Value).TotalMilliseconds > DeduplicationWindowMs * 10)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    EventCache.TryRemove(key, out _);
                }

                if (EventCache.Count <= MaxCacheSize)
                {
                    return;
                }

                var keysToRemove = EventCache
                    .OrderBy(kvp => kvp.Value)
                    .Take(EventCache.Count - MaxCacheSize)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    EventCache.TryRemove(key, out _);
                }

                if (shouldLogDebug)
                {
                    Logger.Log($"🧹 Deduplication cache cleaned: removed {keysToRemove.Count} old entries, current size: {EventCache.Count}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error cleaning deduplication cache: {ex.Message}");
            }
        }
    }
}
