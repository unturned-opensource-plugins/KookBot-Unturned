using System;
using System.Collections.Generic;
using System.Linq;
using Emqo.KookBot_Unturned.Detectors;
using Rocket.Core.Logging;
using Steamworks;

namespace Emqo.KookBot_Unturned
{
    internal class MessageDetectorRegistry
    {
        private readonly List<IMessageDetector> _detectors = new();
        private readonly object _lock = new object();

        internal void ResetToDefaults(ChatModerationConfig config, bool shouldLogDebug)
        {
            ShutdownAll();
            Register(new RateLimitDetector(), config, shouldLogDebug);
            Register(new ForbiddenWordDetector(), config, shouldLogDebug);

            if (shouldLogDebug)
            {
                Logger.Log($"Registered {_detectors.Count} default message detectors");
            }
        }

        internal void UpdateConfig(ChatModerationConfig config)
        {
            lock (_lock)
            {
                foreach (var detector in _detectors)
                {
                    try
                    {
                        detector.UpdateConfig(config);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Error updating detector {detector.Name}: {ex.Message}");
                    }
                }
            }
        }

        internal IReadOnlyList<IMessageDetector> Snapshot()
        {
            lock (_lock)
            {
                return _detectors.ToList();
            }
        }

        internal void CleanupPlayerData(CSteamID steamId)
        {
            lock (_lock)
            {
                foreach (var detector in _detectors)
                {
                    try
                    {
                        detector.CleanupPlayerData(steamId);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Error cleaning up detector {detector.Name} data: {ex.Message}");
                    }
                }
            }
        }

        internal bool Register(IMessageDetector detector, ChatModerationConfig config, bool shouldLogDebug)
        {
            if (detector == null)
            {
                return false;
            }

            lock (_lock)
            {
                if (_detectors.Any(d => d.Name == detector.Name))
                {
                    Logger.LogWarning($"Detector {detector.Name} already registered, skipping");
                    return false;
                }

                detector.Initialize(config ?? ChatModerationConfig.CreateDefault());
                _detectors.Add(detector);

                if (shouldLogDebug)
                {
                    Logger.Log($"Registered detector: {detector.Name}");
                }

                return true;
            }
        }

        internal bool Unregister(string name, bool shouldLogDebug)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            lock (_lock)
            {
                var detector = _detectors.FirstOrDefault(d => d.Name == name);
                if (detector == null)
                {
                    return false;
                }

                try
                {
                    detector.Shutdown();
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error shutting down detector {name}: {ex.Message}");
                }

                _detectors.Remove(detector);

                if (shouldLogDebug)
                {
                    Logger.Log($"Unregistered detector: {name}");
                }

                return true;
            }
        }

        internal IReadOnlyList<string> Names()
        {
            lock (_lock)
            {
                return _detectors.Select(d => d.Name).ToList();
            }
        }

        internal void ShutdownAll()
        {
            lock (_lock)
            {
                foreach (var detector in _detectors)
                {
                    try
                    {
                        detector.Shutdown();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Error shutting down detector {detector.Name}: {ex.Message}");
                    }
                }

                _detectors.Clear();
            }
        }
    }
}
