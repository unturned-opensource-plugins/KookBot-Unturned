using System;
using Steamworks;

namespace Emqo.KookBot_Unturned
{
    internal class MuteInfo
    {
        public CSteamID SteamId { get; set; }
        public string PlayerName { get; set; }
        public string Reason { get; set; }
        public string MutedBy { get; set; }
        public DateTimeOffset MutedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public bool IsAuto { get; set; }

        public bool IsExpired => ExpiresAt.HasValue && DateTimeOffset.UtcNow >= ExpiresAt.Value;

        public TimeSpan? Remaining =>
            ExpiresAt.HasValue ? ExpiresAt.Value - DateTimeOffset.UtcNow : (TimeSpan?)null;

        public string GetDurationDescription()
        {
            if (!ExpiresAt.HasValue)
            {
                return "permanent";
            }

            var remaining = ExpiresAt.Value - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return "expiring soon";
            }

            if (remaining.TotalHours >= 1)
            {
                return $"{Math.Ceiling(remaining.TotalHours)} hours";
            }

            if (remaining.TotalMinutes >= 1)
            {
                return $"{Math.Ceiling(remaining.TotalMinutes)} minutes";
            }

            return $"{Math.Ceiling(remaining.TotalSeconds)} seconds";
        }
    }
}
