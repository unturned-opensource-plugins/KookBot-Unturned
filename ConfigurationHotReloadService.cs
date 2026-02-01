using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Rocket.Core.Logging;

namespace Emqo.KookBot_Unturned
{
    internal static class ConfigurationHotReloadService
    {
        private const int DebounceDelayMs = 750;
        private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private static FileSystemWatcher _watcher;
        private static string _configPath;
        private static CancellationTokenSource _debounceCts;
        private static readonly object _debounceLock = new object();

        public static void Start(string configPath)
        {
            Stop();

            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            {
                Logger.LogWarning($"⚠️ Config hot reload disabled, file not found: {configPath}");
                return;
            }

            _configPath = configPath;
            var directory = Path.GetDirectoryName(configPath);
            var fileName = Path.GetFileName(configPath);

            _watcher = new FileSystemWatcher(directory ?? ".", fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += OnFileChanged;

            Logger.Log("♻️ Configuration hot reload enabled.");
        }

        public static void Stop()
        {
            if (_watcher == null)
            {
                return;
            }

            try
            {
                // Cancel any pending debounced reload
                lock (_debounceLock)
                {
                    _debounceCts?.Cancel();
                    _debounceCts?.Dispose();
                    _debounceCts = null;
                }

                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileChanged;
                _watcher.Created -= OnFileChanged;
                _watcher.Renamed -= OnFileChanged;
                _watcher.Dispose();
                _watcher = null;
                Logger.Log("🛑 Configuration hot reload stopped.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error stopping configuration hot reload: {ex.Message}");
            }
        }

        private static void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            // Fire and forget - don't block the file watcher
            _ = OnFileChangedAsync();
        }

        private static async Task OnFileChangedAsync()
        {
            // Cancel any pending debounced reload before acquiring semaphore
            CancellationToken cancellationToken;
            lock (_debounceLock)
            {
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _debounceCts = new CancellationTokenSource();
                cancellationToken = _debounceCts.Token;
            }

            try
            {
                // Proper debounce: wait before processing
                await Task.Delay(DebounceDelayMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Another change came in, this reload was superseded
                return;
            }

            // Only acquire semaphore after debounce completes
            await _semaphore.WaitAsync();
            try
            {
                // Double-check cancellation after acquiring semaphore
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await ReloadConfigurationAsync();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private static async Task ReloadConfigurationAsync()
        {
            try
            {
                var updated = LoadConfiguration(_configPath);
                if (updated == null)
                {
                    Logger.LogWarning("⚠️ Hot reload skipped, failed to parse configuration.");
                    return;
                }

                updated.ConvertListsToDictionaries();

                var plugin = KookBot_UnturnedPlugin.Instance;
                var current = plugin?.Configuration?.Instance;
                if (plugin == null || current == null)
                {
                    return;
                }

                var botTokenChanged = !string.Equals(current.BotToken, updated.BotToken, StringComparison.Ordinal);
                var channelChanged = !string.Equals(current.ChannelId, updated.ChannelId, StringComparison.Ordinal);

                current.ApplyFrom(updated);
                ChatModerationManager.UpdateConfig(current.Moderation);

                if (channelChanged)
                {
                    Events.UpdateChannel(current.ChannelId);
                }

                if (botTokenChanged)
                {
                    Logger.LogWarning("⚠️ BotToken changed via hot reload. Please restart the plugin to re-establish KOOK connections.");
                }

                Logger.Log("✅ Configuration hot reload applied.");

            }
            catch (Exception ex)
            {
                Logger.LogError($"❌ Failed to hot reload configuration: {ex.Message}");
            }
        }

        private static KookBot_UnturnedConfiguration LoadConfiguration(string path)
        {
            try
            {
                var serializer = new XmlSerializer(typeof(KookBot_UnturnedConfiguration));
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return serializer.Deserialize(stream) as KookBot_UnturnedConfiguration;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to deserialize configuration: {ex.Message}");
                return null;
            }
        }
    }
}

