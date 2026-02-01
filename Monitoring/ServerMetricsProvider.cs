using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SDG.Unturned;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace Emqo.KookBot_Unturned.Monitoring
{
    internal class ServerStatusSnapshot
    {
        public DateTimeOffset CapturedAt { get; set; }
        public int OnlinePlayers { get; set; }
        public int MaxPlayers { get; set; }
        public int QueueLength { get; set; }
        public double AveragePing { get; set; }
        public double HighestPing { get; set; }
        public int EstimatedTps { get; set; }
        public TimeSpan Uptime { get; set; }
    }

    internal static class ServerMetricsProvider
    {
        // Cache reflection results for performance - thread-safe with ConcurrentDictionary
        private static readonly ConcurrentDictionary<string, PropertyInfo> PropertyCache = new();
        private static readonly ConcurrentDictionary<string, FieldInfo> FieldCache = new();
        private static readonly Type ProviderType = typeof(Provider);
        private static readonly Type SteamPlayerType = typeof(SteamPlayer);

        public static ServerStatusSnapshot Capture()
        {
            try
            {
                var players = Provider.clients ?? new List<SteamPlayer>();
                var (avgPing, maxPing) = TryComputePing(players);

                return new ServerStatusSnapshot
                {
                    CapturedAt = DateTimeOffset.Now,
                    OnlinePlayers = players.Count,
                    MaxPlayers = Provider.maxPlayers,
                    QueueLength = TryGetQueueLength(),
                    AveragePing = avgPing,
                    HighestPing = maxPing,
                    EstimatedTps = EstimateTps(),
                    Uptime = KookBot_UnturnedPlugin.GetUptime()
                };
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to capture server metrics: {ex.Message}");
                return new ServerStatusSnapshot
                {
                    CapturedAt = DateTimeOffset.Now,
                    OnlinePlayers = Provider.clients?.Count ?? 0,
                    MaxPlayers = Provider.maxPlayers,
                    QueueLength = 0,
                    AveragePing = 0,
                    HighestPing = 0,
                    EstimatedTps = EstimateTps(),
                    Uptime = KookBot_UnturnedPlugin.GetUptime()
                };
            }
        }

        private static (double avg, double max) TryComputePing(IReadOnlyList<SteamPlayer> players)
        {
            if (players == null || players.Count == 0)
            {
                return (0, 0);
            }

            double total = 0;
            double highest = 0;
            int count = 0;

            foreach (var player in players)
            {
                if (player == null) continue;

                var ping = TryGetNumericMember(player, "ping", "Ping", "latency", "Latency", "lastPing");
                if (ping <= 0)
                {
                    continue;
                }

                total += ping;
                highest = Math.Max(highest, ping);
                count++;
            }

            return count == 0 ? (0, highest) : (total / count, highest);
        }

        private static double TryGetNumericMember(object target, params string[] names)
        {
            if (target == null || names == null || names.Length == 0)
            {
                return 0;
            }

            var type = target.GetType();
            foreach (var name in names)
            {
                var cacheKey = $"{type.FullName}.{name}";

                // Use GetOrAdd for thread-safe caching
                var property = PropertyCache.GetOrAdd(cacheKey, _ =>
                    type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

                if (property != null && TryConvertToDouble(property.GetValue(target), out var value))
                {
                    return value;
                }

                // Use GetOrAdd for thread-safe field caching
                var field = FieldCache.GetOrAdd(cacheKey, _ =>
                    type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

                if (field != null && TryConvertToDouble(field.GetValue(target), out value))
                {
                    return value;
                }
            }

            return 0;
        }

        private static bool TryConvertToDouble(object value, out double result)
        {
            if (value == null)
            {
                result = 0;
                return false;
            }

            switch (value)
            {
                case byte b:
                    result = b;
                    return true;
                case short s:
                    result = s;
                    return true;
                case int i:
                    result = i;
                    return true;
                case long l:
                    result = l;
                    return true;
                case float f:
                    result = f;
                    return true;
                case double d:
                    result = d;
                    return true;
                default:
                    if (double.TryParse(value.ToString(), out var parsed))
                    {
                        result = parsed;
                        return true;
                    }
                    result = 0;
                    return false;
            }
        }

        private static int TryGetQueueLength()
        {
            try
            {
                var providerType = typeof(Provider);
                var collection = TryGetCollection(providerType, "pending") ??
                                 TryGetCollection(providerType, "queue") ??
                                 TryGetCollection(providerType, "pendingPlayers");

                return collection?.Count ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static ICollection TryGetCollection(Type type, string memberName)
        {
            if (type == null || string.IsNullOrWhiteSpace(memberName))
            {
                return null;
            }

            var field = type.GetField(memberName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field?.GetValue(null) is ICollection fieldCollection)
            {
                return fieldCollection;
            }

            var property = type.GetProperty(memberName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return property?.GetValue(null) as ICollection;
        }

        private static int EstimateTps()
        {
            var delta = Mathf.Approximately(Time.smoothDeltaTime, 0f)
                ? Time.deltaTime
                : Time.smoothDeltaTime;

            if (delta <= 0f)
            {
                return 0;
            }

            var tps = Mathf.RoundToInt(1f / delta);
            return Mathf.Clamp(tps, 1, 240);
        }
    }
}

