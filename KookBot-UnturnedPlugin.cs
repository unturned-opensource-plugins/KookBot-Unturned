using Emqo.KookBot_Unturned.KookApi;
using Rocket.API.Collections;
using Rocket.Core.Logging;
using Rocket.Core.Plugins;
using System.Threading.Tasks;
using Rocket.Unturned;
using System;
using SDG.Unturned;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Emqo.KookBot_Unturned.Monitoring;
using Emqo.KookBot_Unturned.Updater;
using Emqo.KookBot_Unturned.Interfaces;

namespace Emqo.KookBot_Unturned
{
    public class KookBot_UnturnedPlugin : RocketPlugin<KookBot_UnturnedConfiguration>
    {
        public static KookBot_UnturnedPlugin Instance { get; private set; }
        private KookWebSocketClient _client;
        private Message _kookMessageApi;
        public static string authid = "";
        private string _configFilePath;
        private static DateTimeOffset _startedAt;
        private IConfigurationProvider _configProvider;

        protected override async void Load()
        {
            try
            {
                Logger.Log("🔍 KookBot-Unturned plugin loading...");

                Instance = this;
                _startedAt = DateTimeOffset.UtcNow;
                _configProvider = new DefaultConfigurationProvider(() => Configuration?.Instance);

                if (Configuration.Instance.GameToKookSettings == null || Configuration.Instance.CommandSettings == null)
                {
                    Logger.Log("🔍 Loading default configuration...");
                    Configuration.Instance.LoadDefaults();
                }
                else
                {
                    Logger.Log("🔍 Converting configuration lists to dictionaries...");
                    Configuration.Instance.ConvertListsToDictionaries();
                }

                // Validate critical configuration
                if (string.IsNullOrWhiteSpace(Configuration.Instance.BotToken))
                {
                    Logger.LogError("❌ BotToken is not configured. Plugin cannot start.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(Configuration.Instance.ChannelId))
                {
                    Logger.LogWarning("⚠️ ChannelId is not configured. Game-to-Kook events will not be sent.");
                }

                Logger.Log("🔍 Initializing ChatModerationManager...");
                ChatModerationManager.Initialize(Configuration.Instance, _configProvider);

                Logger.Log("🔍 Creating Kook message API...");
                _kookMessageApi = new Message(Configuration.Instance.BotToken);

                Logger.Log("🔍 Initializing Events system...");
                Events.Initialize(_kookMessageApi, Configuration.Instance.ChannelId, _configProvider);

                // Start auto updater if enabled
                try
                {
                    Logger.Log("🔍 Starting AutoUpdaterService...");
                    AutoUpdaterService.Start();
                }
                catch (Exception ex)
                {
                    Logger.LogError($"AutoUpdater start failed: {ex.Message}");
                }

                Logger.Log("🔍 Getting bot info from Kook API...");
                try
                {
                    authid = await _kookMessageApi.GetMeAsync();
                    Logger.Log($"✅ Got bot info: {authid}");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"❌ Failed to get bot info from Kook API: {ex.Message}");
                    Logger.LogError($"Stack trace: {ex.StackTrace}");
                    return;
                }

                Logger.Log("🔍 Starting WebSocket client...");
                _client = new KookWebSocketClient(Configuration.Instance.BotToken, _configProvider);
                try
                {
                    // 后台启动 WebSocket，不阻塞主线程
                    _ = _client.StartAsync();
                    Logger.Log("✅ WebSocket client starting in background");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"❌ WebSocket client start failed: {ex.Message}");
                    if (ex.InnerException != null)
                    {
                        Logger.LogError($"Inner exception: {ex.InnerException.Message}");
                    }
                }

                Logger.Log("🔍 Initializing commands...");
                Commands.Init(_kookMessageApi);

                Logger.Log("🔍 Starting configuration hot reload service...");
                _configFilePath = ResolveConfigPath();
                ConfigurationHotReloadService.Start(_configFilePath);

                // 发送服务器启动消息到 Kook
                if (Configuration.Instance.IsGameToKookEnabled("ServerStart"))
                {
                    Logger.Log("🔍 Sending server start message to Kook...");
                    try
                    {
                        var card = KookApi.KookCardFactory.BuildLifecycleCard(
                            "🟢 服务器已启动",
                            $"**服务器名称**：`{Configuration.Instance.ServerName ?? "Unturned"}`\n**地图**：`{Provider.map}`\n**最大玩家**：`{Provider.maxPlayers}`\n**启动时间**：`{DateTime.Now:yyyy-MM-dd HH:mm:ss}`",
                            new (string, string)[0],
                            "success"
                        );

                        await _kookMessageApi.CreateMessageAsync(10, Configuration.Instance.ChannelId, card);
                        Logger.Log("✅ Server start message sent to KOOK successfully");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"❌ Failed to send server start message to KOOK: {ex.Message}");
                        if (ex.InnerException != null)
                        {
                            Logger.LogError($"Inner exception: {ex.InnerException.Message}");
                        }
                    }
                }
                else
                {
                    Logger.Log("⚠️ ServerStart event is disabled in config");
                }

                Logger.Log("✅ KookBot-Unturned plugin loaded successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError($"❌ Critical error during plugin load: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Logger.LogError($"Inner exception: {ex.InnerException.Message}");
                }
                Logger.LogError($"Stack trace: {ex.StackTrace}");
            }
        }

        protected override async void Unload()
        {
            try
            {
                if (Configuration.Instance.IsGameToKookEnabled("ServerStart"))
                {
                    try
                    {
                        var card = KookApi.KookCardFactory.BuildLifecycleCard(
                            "🔴 服务器已关闭",
                            $"**关闭时间**：`{DateTime.Now:yyyy-MM-dd HH:mm:ss}`\n**运行时长**：`{GetUptime():hh\\:mm\\:ss}`",
                            new (string, string)[0],
                            "danger"
                        );
                        await _kookMessageApi.CreateMessageAsync(10, Configuration.Instance.ChannelId, card);
                        Logger.Log("✅ Server stop message sent to KOOK successfully");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"❌ Failed to send server stop message to KOOK: {ex.Message}");
                    }
                }

                if (_client != null)
                {
                    await _client.StopAsync();
                }

                ConfigurationHotReloadService.Stop();
                try
                {
                    AutoUpdaterService.Stop();
                }
                catch { }

                Events.Shutdown();
                ChatModerationManager.Shutdown();
                Logger.Log("✅ Plugin unloaded successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError($"❌ Critical error during plugin unload: {ex.Message}");
            }
        }

        public override TranslationList DefaultTranslations => new()
        {
            { "test", "test" },
        };

        private string ResolveConfigPath()
        {
            try
            {
                var candidates = new List<string>();
                var current = System.Environment.CurrentDirectory;
                if (!string.IsNullOrWhiteSpace(current))
                {
                    candidates.Add(Path.Combine(current, "Plugins", "KookBot-Unturned", "KookBot-Unturned.configuration.xml"));
                    candidates.Add(Path.Combine(current, "Rocket", "Plugins", "KookBot-Unturned", "KookBot-Unturned.configuration.xml"));
                }

                var serverRoot = Path.Combine(ReadWrite.PATH, "Servers", Provider.serverID);
                if (System.IO.Directory.Exists(serverRoot))
                {
                    var rocketRoot = Path.Combine(serverRoot, "Rocket");
                    candidates.Add(Path.Combine(rocketRoot, "Plugins", "KookBot-Unturned", "KookBot-Unturned.configuration.xml"));
                }

                foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (System.IO.File.Exists(path))
                    {
                        return path;
                    }
                }

                return candidates.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        public static TimeSpan GetUptime()
        {
            if (_startedAt == default)
            {
                return TimeSpan.Zero;
            }

            return DateTimeOffset.UtcNow - _startedAt;
        }
    }
}
