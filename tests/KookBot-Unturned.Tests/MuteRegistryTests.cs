using Emqo.KookBot_Unturned;
using Steamworks;
using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class MuteRegistryTests
{
    [Fact]
    public void Mute_applies_default_reason_and_muted_by_values()
    {
        var registry = new MuteRegistry();
        var steamId = new CSteamID(100);

        var info = registry.Mute(steamId, "Alice", null, " ", "", isAuto: true, shouldLogDebug: false);

        Assert.Equal(steamId, info.SteamId);
        Assert.Equal("Alice", info.PlayerName);
        Assert.Equal("System auto-mute", info.Reason);
        Assert.Equal("Unknown", info.MutedBy);
        Assert.True(info.IsAuto);
        Assert.Null(info.ExpiresAt);
        Assert.True(registry.IsMuted(steamId, out var active));
        Assert.Same(info, active);
    }

    [Fact]
    public void Timed_mute_expires_and_is_removed_on_lookup()
    {
        var registry = new MuteRegistry();
        var steamId = new CSteamID(101);

        registry.Mute(steamId, "Bob", TimeSpan.FromMilliseconds(10), "short", "admin", isAuto: false, shouldLogDebug: false);
        Thread.Sleep(30);

        Assert.False(registry.IsMuted(steamId, out _));
        Assert.Empty(registry.GetActiveMutes());
    }

    [Fact]
    public void TryUnmutePlayer_matches_name_case_insensitively_or_steam_id()
    {
        var registry = new MuteRegistry();
        var alice = new CSteamID(102);
        var bob = new CSteamID(103);
        registry.Mute(alice, "Alice", null, "reason", "admin", false, false);
        registry.Mute(bob, "Bob", null, "reason", "admin", false, false);

        Assert.True(registry.TryUnmutePlayer("alice", out var aliceInfo));
        Assert.Equal(alice, aliceInfo.SteamId);
        Assert.False(registry.IsMutedSync(alice));

        Assert.True(registry.TryUnmutePlayer("103", out var bobInfo));
        Assert.Equal(bob, bobInfo.SteamId);
        Assert.False(registry.IsMutedSync(bob));
    }

    [Fact]
    public void UnmuteBySteamId_removes_only_matching_mute()
    {
        var registry = new MuteRegistry();
        var alice = new CSteamID(104);
        var bob = new CSteamID(105);
        registry.Mute(alice, "Alice", null, "reason", "admin", false, false);
        registry.Mute(bob, "Bob", null, "reason", "admin", false, false);

        Assert.True(registry.UnmuteBySteamId(alice, out var removed));
        Assert.Equal("Alice", removed.PlayerName);
        Assert.False(registry.IsMutedSync(alice));
        Assert.True(registry.IsMutedSync(bob));
    }

    [Fact]
    public void Clear_removes_all_mutes()
    {
        var registry = new MuteRegistry();
        registry.Mute(new CSteamID(106), "Alice", null, "reason", "admin", false, false);
        registry.Mute(new CSteamID(107), "Bob", null, "reason", "admin", false, false);

        registry.Clear();

        Assert.Empty(registry.GetActiveMutes());
    }
}
