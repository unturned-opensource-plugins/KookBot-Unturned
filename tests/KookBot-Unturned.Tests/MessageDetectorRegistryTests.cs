using Emqo.KookBot_Unturned;
using Emqo.KookBot_Unturned.Detectors;
using Rocket.Unturned.Player;
using Steamworks;
using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class MessageDetectorRegistryTests
{
    [Fact]
    public void ResetToDefaults_registers_rate_limit_and_forbidden_word_detectors()
    {
        var registry = new MessageDetectorRegistry();

        registry.ResetToDefaults(ChatModerationConfig.CreateDefault(), shouldLogDebug: false);

        Assert.Equal(new[] { "RateLimit", "ForbiddenWord" }, registry.Names());
        Assert.Equal(2, registry.Snapshot().Count);
    }

    [Fact]
    public void Register_rejects_duplicate_detector_names()
    {
        var registry = new MessageDetectorRegistry();
        var first = new FakeDetector("custom");
        var second = new FakeDetector("custom");

        Assert.True(registry.Register(first, ChatModerationConfig.CreateDefault(), false));
        Assert.False(registry.Register(second, ChatModerationConfig.CreateDefault(), false));
        Assert.Single(registry.Names());
    }

    [Fact]
    public void UpdateConfig_cleanup_and_shutdown_are_forwarded_to_registered_detectors()
    {
        var registry = new MessageDetectorRegistry();
        var detector = new FakeDetector("custom");
        var steamId = new CSteamID(200);
        registry.Register(detector, ChatModerationConfig.CreateDefault(), false);

        registry.UpdateConfig(new ChatModerationConfig { EnableModeration = false });
        registry.CleanupPlayerData(steamId);
        registry.ShutdownAll();

        Assert.Equal(1, detector.InitializeCount);
        Assert.Equal(2, detector.UpdateCount);
        Assert.Equal(1, detector.CleanupCount);
        Assert.Equal(steamId, detector.LastCleanupId);
        Assert.Equal(1, detector.ShutdownCount);
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public void Unregister_shuts_down_matching_detector_and_reports_missing_names()
    {
        var registry = new MessageDetectorRegistry();
        var detector = new FakeDetector("custom");
        registry.Register(detector, ChatModerationConfig.CreateDefault(), false);

        Assert.False(registry.Unregister("missing", false));
        Assert.True(registry.Unregister("custom", false));
        Assert.Equal(1, detector.ShutdownCount);
        Assert.Empty(registry.Names());
    }

    private sealed class FakeDetector : IMessageDetector
    {
        public FakeDetector(string name) => Name = name;
        public string Name { get; }
        public bool IsEnabled { get; private set; } = true;
        public int InitializeCount { get; private set; }
        public int UpdateCount { get; private set; }
        public int ShutdownCount { get; private set; }
        public int CleanupCount { get; private set; }
        public CSteamID LastCleanupId { get; private set; }

        public DetectionResult DetectSync(UnturnedPlayer player, string message) => DetectionResult.Allowed();

        public void Initialize(ChatModerationConfig config)
        {
            InitializeCount++;
            UpdateConfig(config);
        }

        public void UpdateConfig(ChatModerationConfig config)
        {
            UpdateCount++;
            IsEnabled = config?.EnableModeration ?? false;
        }

        public void Shutdown()
        {
            ShutdownCount++;
        }

        public void CleanupPlayerData(CSteamID steamId)
        {
            CleanupCount++;
            LastCleanupId = steamId;
        }
    }
}
