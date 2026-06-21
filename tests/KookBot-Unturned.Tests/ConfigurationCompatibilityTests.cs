using System.Xml.Serialization;
using Emqo.KookBot_Unturned;
using Emqo.KookBot_Unturned.Interfaces;
using Rocket.Core.Logging;
using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class ConfigurationCompatibilityTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "kookbot-tests", Guid.NewGuid().ToString("N"));

    public ConfigurationCompatibilityTests()
    {
        Directory.CreateDirectory(_tempDir);
        Logger.Clear();
    }

    public void Dispose()
    {
        ConfigurationHotReloadService.Stop();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        Logger.Clear();
    }

    [Fact]
    public void Old_xml_missing_settings_is_upgraded_with_default_switches_and_moderation()
    {
        var path = WriteConfig("""
            <?xml version="1.0" encoding="utf-8"?>
            <KookBot_UnturnedConfiguration>
              <ServerName>Legacy</ServerName>
              <BotToken>token</BotToken>
              <ChannelId>channel</ChannelId>
              <EnableSync>true</EnableSync>
              <MessagePrefix>[U]</MessagePrefix>
              <KookToGame>true</KookToGame>
            </KookBot_UnturnedConfiguration>
            """);

        var config = ConfigurationHotReloadService.LoadConfiguration(path);
        config.ConvertListsToDictionaries();

        Assert.Equal("Legacy", config.ServerName);
        Assert.True(config.IsGameToKookEnabled("PlayerJoined"));
        Assert.True(config.IsGameToKookEnabled("PlayerDamaged"));
        Assert.True(config.IsCommandEnabled("help"));
        Assert.True(config.IsCommandEnabled("cmd"));
        Assert.NotNull(config.Moderation);
        Assert.True(config.Moderation.EnableModeration);
    }

    [Fact]
    public void Duplicate_settings_are_collapsed_with_last_value_winning()
    {
        var config = new KookBot_UnturnedConfiguration
        {
            GameToKookSettings = new List<SettingItem>
            {
                new("PlayerDamaged", true),
                new("PlayerDamaged", false)
            },
            CommandSettings = new List<SettingItem>
            {
                new("status", true),
                new("status", false)
            }
        };

        config.ConvertListsToDictionaries();

        Assert.False(config.IsGameToKookEnabled("PlayerDamaged"));
        Assert.False(config.IsCommandEnabled("status"));
        Assert.Single(config.GameToKookSettings, x => x.Key == "PlayerDamaged");
        Assert.Single(config.CommandSettings, x => x.Key == "status");
    }

    [Fact]
    public void Invalid_xml_returns_null_and_does_not_throw()
    {
        var path = WriteConfig("<KookBot_UnturnedConfiguration><BotToken>half");

        var config = ConfigurationHotReloadService.LoadConfiguration(path);

        Assert.Null(config);
        Assert.Contains(Logger.Messages, message => message.Contains("Failed to deserialize configuration"));
    }

    [Fact]
    public void Serialized_current_configuration_round_trips_and_keeps_alias_group()
    {
        var original = new KookBot_UnturnedConfiguration();
        original.LoadDefaults();
        original.TrySetCommandRuntime("cmd", false);
        var path = Path.Combine(_tempDir, "roundtrip.xml");

        using (var stream = File.Create(path))
        {
            new XmlSerializer(typeof(KookBot_UnturnedConfiguration)).Serialize(stream, original);
        }

        var loaded = ConfigurationHotReloadService.LoadConfiguration(path);
        loaded.ConvertListsToDictionaries();

        Assert.False(loaded.IsCommandEnabled("cmd"));
        Assert.False(loaded.IsCommandEnabled("console"));
        Assert.False(loaded.IsCommandEnabled("exec"));
        Assert.Equal(original.CommandSettings.Count, loaded.CommandSettings.Count);
    }

    [Fact]
    public void ApplyFrom_preserves_target_when_source_is_null()
    {
        var config = new KookBot_UnturnedConfiguration();
        config.LoadDefaults();
        config.TrySetGameToKookRuntime("PlayerDamaged", false);

        config.ApplyFrom(null);

        Assert.False(config.IsGameToKookEnabled("PlayerDamaged"));
    }

    [Fact]
    public void DefaultConfigurationProvider_is_null_safe()
    {
        var provider = new DefaultConfigurationProvider(() => null);

        Assert.Null(provider.GetConfiguration());
        Assert.False(provider.IsDebugEnabled);
        Assert.Null(provider.BotToken);
        Assert.Null(provider.ChannelId);
        Assert.Null(provider.ModerationConfig);
        Assert.False(provider.IsGameToKookEnabled("PlayerJoined"));
        Assert.False(provider.IsCommandEnabled("help"));
    }

    [Fact]
    public void DefaultConfigurationProvider_reflects_latest_configuration_instance()
    {
        var current = new KookBot_UnturnedConfiguration();
        current.LoadDefaults();
        var provider = new DefaultConfigurationProvider(() => current);

        Assert.True(provider.IsGameToKookEnabled("PlayerDamaged"));

        current = new KookBot_UnturnedConfiguration();
        current.LoadDefaults();
        current.TrySetGameToKookRuntime("PlayerDamaged", false);

        Assert.False(provider.IsGameToKookEnabled("PlayerDamaged"));
    }

    private string WriteConfig(string contents)
    {
        var path = Path.Combine(_tempDir, "KookBot-Unturned.configuration.xml");
        File.WriteAllText(path, contents);
        return path;
    }
}
