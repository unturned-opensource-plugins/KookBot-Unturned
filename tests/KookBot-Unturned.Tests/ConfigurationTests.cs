using Emqo.KookBot_Unturned;
using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class ConfigurationTests
{
    [Fact]
    public void LoadDefaults_initializes_events_commands_and_moderation()
    {
        var config = new KookBot_UnturnedConfiguration();

        config.LoadDefaults();

        Assert.True(config.IsGameToKookEnabled("PlayerDamaged"));
        Assert.True(config.IsCommandEnabled("status"));
        Assert.True(config.IsCommandEnabled("cmd"));
        Assert.True(config.IsCommandEnabled("console"));
        Assert.True(config.IsCommandEnabled("exec"));
        Assert.NotNull(config.Moderation);
        Assert.True(config.Moderation.EnableModeration);
    }

    [Fact]
    public void ChatModerationConfig_does_not_expose_removed_auto_mute_duration_tombstone()
    {
        Assert.Null(typeof(ChatModerationConfig).GetProperty("AutoMuteDurationSeconds"));
    }

    [Fact]
    public void ChatModerationConfig_does_not_expose_removed_broadcast_spam_mutes_tombstone()
    {
        Assert.Null(typeof(ChatModerationConfig).GetProperty("BroadcastSpamMutes"));
    }

    [Fact]
    public void ConvertListsToDictionaries_normalizes_console_aliases_as_one_safety_group()
    {
        var config = new KookBot_UnturnedConfiguration
        {
            GameToKookSettings = new List<SettingItem>(),
            CommandSettings = new List<SettingItem>
            {
                new("cmd", false),
                new("console", true),
                new("exec", true),
            }
        };

        config.ConvertListsToDictionaries();

        Assert.False(config.IsCommandEnabled("cmd"));
        Assert.False(config.IsCommandEnabled("console"));
        Assert.False(config.IsCommandEnabled("exec"));
        Assert.All(new[] { "cmd", "console", "exec" }, alias =>
            Assert.Contains(config.CommandSettings, item => item.Key == alias && item.Value == false));
    }

    [Fact]
    public void TrySetGameToKookRuntime_updates_known_event_and_rejects_unknown_event()
    {
        var config = new KookBot_UnturnedConfiguration();
        config.LoadDefaults();

        Assert.True(config.TrySetGameToKookRuntime("PlayerDamaged", false));
        Assert.False(config.IsGameToKookEnabled("PlayerDamaged"));
        Assert.False(config.TrySetGameToKookRuntime("NotARealEvent", true));
    }

    [Fact]
    public void Snapshots_are_copies_not_mutable_live_state()
    {
        var config = new KookBot_UnturnedConfiguration();
        config.LoadDefaults();

        var snapshot = config.GetGameToKookSnapshot();
        snapshot["PlayerDamaged"] = false;

        Assert.True(config.IsGameToKookEnabled("PlayerDamaged"));
    }

    [Fact]
    public void ApplyFrom_copies_settings_without_sharing_snapshot_dictionaries()
    {
        var source = new KookBot_UnturnedConfiguration();
        source.LoadDefaults();
        source.TrySetGameToKookRuntime("PlayerDamaged", false);

        var target = new KookBot_UnturnedConfiguration();
        target.ApplyFrom(source);
        source.TrySetGameToKookRuntime("PlayerDamaged", true);

        Assert.False(target.IsGameToKookEnabled("PlayerDamaged"));
        Assert.True(source.IsGameToKookEnabled("PlayerDamaged"));
    }
}
