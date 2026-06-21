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

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}");
    }
}
