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

    [Fact]
    public void Configuration_template_keeps_player_damaged_enabled_with_high_frequency_warning()
    {
        var contents = File.ReadAllText(FindRepoFile("KookBot-Unturned.configuration.template.xml"));

        Assert.Contains("<SettingItem><Key>PlayerDamaged</Key><Value>true</Value></SettingItem>", contents);
        Assert.Contains("高频", contents);
        Assert.Contains("生产", contents);
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
