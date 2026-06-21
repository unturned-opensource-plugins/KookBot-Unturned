using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class RuntimeSourcePolicyTests
{
    [Fact]
    public void Chat_moderation_shutdown_does_not_block_on_cleanup_task()
    {
        var contents = File.ReadAllText(FindRepoFile("ChatModerationManager.cs"));

        Assert.DoesNotContain(".Wait(", contents, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Detectors/IMessageDetector.cs")]
    [InlineData("ChatModerationManager.cs")]
    public void Live_chat_moderation_does_not_expose_pseudo_async_detector_contract(string relativePath)
    {
        var contents = File.ReadAllText(FindRepoFile(relativePath));

        Assert.DoesNotContain("DetectAsync", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter().GetResult()", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_sources_do_not_reintroduce_pseudo_async_detector_contract()
    {
        var root = FindRepoRoot();
        var productionSources = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

        foreach (var path in productionSources)
        {
            var contents = File.ReadAllText(path);
            Assert.DoesNotContain("DetectAsync", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("GetAwaiter().GetResult()", contents, StringComparison.Ordinal);
        }
    }

    private static string FindRepoFile(string relativePath)
    {
        return Path.Combine(FindRepoRoot(), relativePath);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "KookBot-Unturned.sln");
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find repo root from {AppContext.BaseDirectory}");
    }
}
