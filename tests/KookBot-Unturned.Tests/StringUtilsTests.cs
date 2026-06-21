using Emqo.KookBot_Unturned.Utilities;
using Xunit;

namespace Emqo.KookBot_Unturned.Tests;

public class StringUtilsTests
{
    [Fact]
    public void SanitizeRichText_replaces_unity_rich_text_delimiters()
    {
        var input = @"<color=red>[Admin]\path</color>";

        var output = StringUtils.SanitizeRichText(input);

        Assert.Equal(@"＜color=red＞［Admin］＼path＜/color＞", output);
        Assert.DoesNotContain("<", output);
        Assert.DoesNotContain(">", output);
        Assert.DoesNotContain("[", output);
        Assert.DoesNotContain("]", output);
        Assert.DoesNotContain("\\", output);
    }

    [Fact]
    public void SanitizeRichText_preserves_null_and_empty_inputs()
    {
        Assert.Null(StringUtils.SanitizeRichText(null));
        Assert.Equal(string.Empty, StringUtils.SanitizeRichText(string.Empty));
    }

    [Fact]
    public void SanitizeRichText_leaves_normal_text_unchanged()
    {
        Assert.Equal("hello 世界 123", StringUtils.SanitizeRichText("hello 世界 123"));
    }
}
