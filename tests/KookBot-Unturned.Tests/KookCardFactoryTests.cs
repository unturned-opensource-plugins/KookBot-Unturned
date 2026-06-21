using Emqo.KookBot_Unturned.KookApi;
using Emqo.KookBot_Unturned.Monitoring;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class KookCardFactoryTests
{
    [Fact]
    public void BuildStatusCard_returns_empty_string_for_null_snapshot()
    {
        Assert.Equal(string.Empty, KookCardFactory.BuildStatusCard(null, "server"));
    }

    [Fact]
    public void BuildStatusCard_uses_safe_server_name_and_tps_fallbacks()
    {
        var json = KookCardFactory.BuildStatusCard(new ServerStatusSnapshot
        {
            CapturedAt = new DateTimeOffset(2026, 6, 21, 20, 0, 0, TimeSpan.Zero),
            OnlinePlayers = 3,
            MaxPlayers = 24,
            QueueLength = 2,
            EstimatedTps = 0,
            Uptime = TimeSpan.Zero
        }, "   ");

        var card = FirstCard(json);

        Assert.Equal("warning", (string)card["theme"]);
        Assert.Equal("Unturned 状态面板", HeaderText(card));
        var body = SectionText(card, 1);
        Assert.Contains("**TPS**：`N/A`", body);
        Assert.Contains("**在线人数**：`3/24`", body);
        Assert.Contains("**排队人数**：`2`", body);
        Assert.Contains("**运行时间**：`N/A`", body);
    }

    [Fact]
    public void BuildChatMessageCard_uses_unknown_player_empty_message_and_global_scope_fallbacks()
    {
        var json = KookCardFactory.BuildChatMessageCard(null, " ", "", DateTimeOffset.Parse("2026-06-21T20:01:02+00:00"));
        var card = FirstCard(json);

        Assert.Equal("info", (string)card["theme"]);
        Assert.Equal("💬 游戏聊天 (GLOBAL)", HeaderText(card));
        Assert.Contains("**未知玩家**：（空消息）", SectionText(card, 1));
    }

    [Fact]
    public void BuildGenericEventCard_filters_empty_fields_and_keeps_context()
    {
        var json = KookCardFactory.BuildGenericEventCard(
            "⚔️",
            "PvP",
            new[] { ("Attacker", "Alice"), ("", "ignored"), ("Victim", "") },
            DateTimeOffset.Parse("2026-06-21T20:01:02+00:00"),
            "danger");
        var card = FirstCard(json);

        Assert.Equal("danger", (string)card["theme"]);
        Assert.Equal("⚔️ PvP", HeaderText(card));
        Assert.Contains("**Attacker**：`Alice`", SectionText(card, 1));
        Assert.DoesNotContain("ignored", SectionText(card, 1));
        Assert.Equal("context", (string)card["modules"]![2]!["type"]);
    }

    [Fact]
    public void BuildMarkdownCard_omits_blank_section_but_keeps_context()
    {
        var json = KookCardFactory.BuildMarkdownCard("🛟", "安全诊断", " ", DateTimeOffset.UtcNow, "info");
        var modules = (JArray)FirstCard(json)["modules"]!;

        Assert.Equal(2, modules.Count);
        Assert.Equal("header", (string)modules[0]!["type"]);
        Assert.Equal("context", (string)modules[1]!["type"]);
    }

    [Fact]
    public void Cards_are_valid_json_arrays_with_card_root()
    {
        var json = KookCardFactory.BuildLifecycleCard("标题", "描述", new[] { ("字段", "值") }, null);
        var root = JArray.Parse(json);

        Assert.Single(root);
        Assert.Equal("card", (string)root[0]!["type"]);
        Assert.Equal("secondary", (string)root[0]!["theme"]);
        Assert.Equal("lg", (string)root[0]!["size"]);
    }

    private static JObject FirstCard(string json) => (JObject)JArray.Parse(json)[0]!;

    private static string HeaderText(JObject card) =>
        (string)card["modules"]![0]!["text"]!["content"]!;

    private static string SectionText(JObject card, int moduleIndex) =>
        (string)card["modules"]![moduleIndex]!["text"]!["content"]!;
}
