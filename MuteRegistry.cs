using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Rocket.Core.Logging;
using Steamworks;

namespace Emqo.KookBot_Unturned
{
    internal class MuteRegistry
    {
        private readonly ConcurrentDictionary<CSteamID, MuteInfo> _mutes = new();

        internal MuteInfo Mute(CSteamID steamId, string playerName, TimeSpan? duration, string reason, string mutedBy, bool isAuto, bool shouldLogDebug)
        {
            var now = DateTimeOffset.UtcNow;
            var info = new MuteInfo
            {
                SteamId = steamId,
                PlayerName = playerName,
                Reason = string.IsNullOrWhiteSpace(reason) ? (isAuto ? "System auto-mute" : "Admin mute") : reason,
                MutedBy = string.IsNullOrWhiteSpace(mutedBy) ? "Unknown" : mutedBy,
                MutedAt = now,
                IsAuto = isAuto
            };

            if (duration.HasValue && duration.Value > TimeSpan.Zero)
            {
                info.ExpiresAt = now.Add(duration.Value);
            }

            try
            {
                _mutes[steamId] = info;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error muting player: {ex.Message}");
            }

            if (shouldLogDebug)
            {
                Logger.Log($"Player {playerName} muted by {info.MutedBy} ({info.GetDurationDescription()}) Reason: {info.Reason}");
            }

            return info;
        }

        internal bool TryUnmutePlayer(string playerNameOrId, out MuteInfo muteInfo)
        {
            muteInfo = null;

            try
            {
                var entry = _mutes.FirstOrDefault(pair =>
                    pair.Value.PlayerName.Equals(playerNameOrId, StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.ToString().Equals(playerNameOrId, StringComparison.OrdinalIgnoreCase));

                if (entry.Value == null)
                {
                    return false;
                }

                muteInfo = entry.Value;
                _mutes.TryRemove(entry.Key, out _);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error unmuting player: {ex.Message}");
                return false;
            }
            finally
            {
                if (muteInfo != null)
                {
                    Logger.Log($"Player {muteInfo.PlayerName} unmuted manually.");
                }
            }
        }

        internal bool UnmuteBySteamId(CSteamID steamId, out MuteInfo muteInfo)
        {
            muteInfo = null;

            try
            {
                if (!_mutes.TryRemove(steamId, out muteInfo))
                {
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error unmuting player by SteamID: {ex.Message}");
                return false;
            }
            finally
            {
                if (muteInfo != null)
                {
                    Logger.Log($"Player {muteInfo.PlayerName} unmuted (SteamID match).");
                }
            }
        }

        internal IReadOnlyCollection<MuteInfo> GetActiveMutes()
        {
            try
            {
                CleanupExpired();
                return _mutes.Values.ToList();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error getting active mutes: {ex.Message}");
                return new List<MuteInfo>();
            }
        }

        internal bool IsMuted(CSteamID steamId, out MuteInfo muteInfo)
        {
            muteInfo = null;
            try
            {
                if (!_mutes.TryGetValue(steamId, out var info))
                {
                    return false;
                }

                if (info.IsExpired)
                {
                    _mutes.TryRemove(steamId, out _);
                    return false;
                }

                muteInfo = info;
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error checking mute status: {ex.Message}");
                return false;
            }
        }

        internal bool IsMutedSync(CSteamID steamId)
        {
            try
            {
                return _mutes.TryGetValue(steamId, out var info) && !info.IsExpired;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in IsMutedSync: {ex.Message}");
                return false;
            }
        }

        internal void CleanupExpired()
        {
            foreach (var pair in _mutes)
            {
                if (pair.Value.IsExpired)
                {
                    _mutes.TryRemove(pair.Key, out _);
                }
            }
        }

        internal void Clear()
        {
            _mutes.Clear();
        }
    }
}
