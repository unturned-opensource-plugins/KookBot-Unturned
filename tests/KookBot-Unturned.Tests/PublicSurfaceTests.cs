using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class PublicSurfaceTests
{
    [Theory]
    [InlineData("README.md")]
    [InlineData(".github/workflows/build-release.yml")]
    public void Published_surfaces_do_not_reference_local_only_docs(string relativePath)
    {
        var contents = File.ReadAllText(FindRepoFile(relativePath));

        Assert.DoesNotContain("docs/", contents, StringComparison.OrdinalIgnoreCase);
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
