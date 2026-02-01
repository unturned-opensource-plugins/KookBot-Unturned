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
            Instance = this;
            _startedAt = DateTimeOffset.UtcNow;
            _configProvider = new DefaultConfigurationProvider(() => Configuration?.Instance);

            if (Configuration.Instance.GameToKookSettings == null || Configuration.Instance.CommandSettings == null)
            {
                Configuration.Instance.LoadDefaults();
            }
            else
            {
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

            ChatModerationManager.Initialize(Configuration.Instance, _configProvider);
            _kookMessageApi = new Message(Configuration.Instance.BotToken);

            Events.Initialize(_kookMessageApi, Configuration.Instance.ChannelId, _configProvider);

            // 发送服务器启动消息到 Kook
            if (Configuration.Instance.IsGameToKookEnabled("ServerStart"))
            {
                try
                {
                    var fields = new List<(string, string)>
                    {
                        ("服务器名称", Configuration.Instance.ServerName ?? "Unturned"),
                        ("在线人数", $"{Provider.clients.Count}/{Provider.maxPlayers}"),
                        ("启动时间", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                    };
                    var card = KookCardFactory.BuildGenericEventCard(
                        "🟢",
                        "服务器已启动",
                        fields,
                        DateTimeOffset.Now,
                        "success");
                    await _kookMessageApi.CreateMessageAsync(10, Configuration.Instance.ChannelId, card);
                    Logger.Log("📤 Server start event sent to KOOK");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"❌ Failed to send server start message to KOOK: {ex.Message}");
                }
            }

            // Start auto updater if enabled
            try
            {
                AutoUpdaterService.Start();
            }
            catch (Exception ex)
            {
                Logger.LogError($"AutoUpdater start failed: {ex.Message}");
            }

            authid = await _kookMessageApi.GetMeAsync();

            _client = new KookWebSocketClient(Configuration.Instance.BotToken, _configProvider);
            try
            {
                await _client.StartAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError($"❌ WebSocket client start failed: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Logger.LogError($"Inner exception: {ex.InnerException.Message}");
                }
            }
            Commands.Init(_kookMessageApi);

            _configFilePath = ResolveConfigPath();
            ConfigurationHotReloadService.Start(_configFilePath);
            if (Configuration.Instance.IsGameToKookEnabled("ServerStart")) {
                try
                {
                    var card = KookApi.KookCardFactory.BuildLifecycleCard(
                        $"{Configuration.Instance.ServerName} 已启动",
                        $"🟢 {Provider.serverName} 服务器已上线",
                        new[]
                        {
                            ("地图", $"`{Provider.map}`"),
                            ("最大玩家", $"`{Provider.maxPlayers}`"),
                            ("启动时间", $"`{DateTime.Now:yyyy-MM-dd HH:mm:ss}`")
                        },
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
                            $"{Configuration.Instance.ServerName} 已关闭",
                            $"🔴 服务器已下线",
                            new[]
                            {
                                ("关闭时间", $"`{DateTime.Now:yyyy-MM-dd HH:mm:ss}`"),
                                ("运行时长", $"`{GetUptime():hh\\:mm\\:ss}`")
                            },
                            "danger"
                        );
                        await _kookMessageApi.CreateMessageAsync(10, Configuration.Instance.ChannelId, card);
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
                Logger.Log("📤 Server stop event sent to KOOK");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error during plugin unload: {ex.Message}");
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