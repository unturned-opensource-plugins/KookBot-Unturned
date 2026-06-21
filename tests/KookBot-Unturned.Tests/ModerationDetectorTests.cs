using Emqo.KookBot_Unturned;
using Emqo.KookBot_Unturned.Detectors;
using Rocket.Unturned.Player;
using Steamworks;
using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class ModerationDetectorTests
{
    [Fact]
    public void DetectionResult_Allowed_returns_fresh_instances()
    {
        var first = DetectionResult.Allowed();
        first.IsViolation = true;

        var second = DetectionResult.Allowed();

        Assert.NotSame(first, second);
        Assert.False(second.IsViolation);
    }

    [Fact]
    public void ForbiddenWordDetector_detects_case_insensitive_substrings_and_auto_mute_duration()
    {
        var detector = new ForbiddenWordDetector();
        detector.Initialize(new ChatModerationConfig
        {
            EnableForbiddenWordFilter = true,
            ForbiddenWords = new List<string> { " BAD ", "ignored" },
            ForbiddenWordWarningMessage = "blocked",
            AutoMuteOnForbiddenWord = true,
            AutoMuteForbiddenWordSeconds = 42
        });

        var result = detector.DetectSync(Player(1), "this is bad content");

        Assert.True(detector.IsEnabled);
        Assert.True(result.IsViolation);
        Assert.Equal("ForbiddenWord", result.DetectorName);
        Assert.Equal("blocked", result.Reason);
        Assert.True(result.ShouldAutoMute);
        Assert.Equal(TimeSpan.FromSeconds(42), result.AutoMuteDuration);
    }

    [Fact]
    public void ForbiddenWordDetector_allows_when_disabled_empty_or_no_words()
    {
        var detector = new ForbiddenWordDetector();
        detector.Initialize(new ChatModerationConfig
        {
            EnableForbiddenWordFilter = false,
            ForbiddenWords = new List<string> { "bad" }
        });

        Assert.False(detector.DetectSync(Player(1), "bad").IsViolation);

        detector.UpdateConfig(new ChatModerationConfig
        {
            EnableForbiddenWordFilter = true,
            ForbiddenWords = new List<string>()
        });

        Assert.False(detector.DetectSync(Player(1), "bad").IsViolation);
        Assert.False(detector.DetectSync(null, "bad").IsViolation);
        Assert.False(detector.DetectSync(Player(1), " ").IsViolation);
    }

    [Fact]
    public void ForbiddenWordDetector_uses_default_warning_and_mute_seconds()
    {
        var detector = new ForbiddenWordDetector();
        detector.Initialize(new ChatModerationConfig
        {
            EnableForbiddenWordFilter = true,
            ForbiddenWords = new List<string> { "bad" },
            ForbiddenWordWarningMessage = " ",
            AutoMuteOnForbiddenWord = true,
            AutoMuteForbiddenWordSeconds = -1
        });

        var result = detector.DetectSync(Player(1), "bad");

        Assert.True(result.IsViolation);
        Assert.Equal("消息包含违禁词，已被拦截。", result.Reason);
        Assert.Equal(TimeSpan.FromSeconds(300), result.AutoMuteDuration);
    }

    [Fact]
    public void RateLimitDetector_allows_first_message_then_blocks_fast_repeat()
    {
        var detector = new RateLimitDetector();
        detector.Initialize(new ChatModerationConfig
        {
            EnableRateLimitDetection = true,
            MinimumSecondsBetweenMessages = 60,
            RateLimitWarningMessage = "slow down"
        });
        var player = Player(2);

        Assert.False(detector.DetectSync(player, "one").IsViolation);
        var second = detector.DetectSync(player, "two");

        Assert.True(second.IsViolation);
        Assert.Equal("RateLimit", second.DetectorName);
        Assert.Equal("slow down", second.Reason);
        Assert.False(second.ShouldAutoMute);
    }

    [Fact]
    public void RateLimitDetector_cleanup_player_data_resets_that_player_only()
    {
        var detector = new RateLimitDetector();
        detector.Initialize(new ChatModerationConfig
        {
            EnableRateLimitDetection = true,
            MinimumSecondsBetweenMessages = 60
        });
        var first = Player(3);
        var second = Player(4);

        detector.DetectSync(first, "one");
        detector.DetectSync(second, "one");
        detector.CleanupPlayerData(first.CSteamID);

        Assert.False(detector.DetectSync(first, "after cleanup").IsViolation);
        Assert.True(detector.DetectSync(second, "still limited").IsViolation);
    }

    [Fact]
    public void RateLimitDetector_disables_on_null_config_and_shutdown_clears_state()
    {
        var detector = new RateLimitDetector();
        detector.Initialize(new ChatModerationConfig
        {
            EnableRateLimitDetection = true,
            MinimumSecondsBetweenMessages = 60
        });
        var player = Player(5);

        detector.DetectSync(player, "one");
        detector.UpdateConfig(null);
        Assert.False(detector.IsEnabled);
        Assert.False(detector.DetectSync(player, "two").IsViolation);

        detector.UpdateConfig(new ChatModerationConfig { EnableRateLimitDetection = true, MinimumSecondsBetweenMessages = 60 });
        detector.DetectSync(player, "three");
        detector.Shutdown();
        Assert.False(detector.DetectSync(player, "four").IsViolation);
    }

    private static UnturnedPlayer Player(ulong id) => new()
    {
        CSteamID = new CSteamID(id),
        DisplayName = $"Player{id}",
        CharacterName = $"Character{id}"
    };
}
