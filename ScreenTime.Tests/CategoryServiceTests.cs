using ScreenTime.Services;
using Xunit;

namespace ScreenTime.Tests;

/// <summary>分类规则：空值、大小写、超长输入、各层兜底顺序。</summary>
public class CategoryServiceTests
{
    [Fact]
    public void Classify_NullEverything_DoesNotThrow()
    {
        var c = CategoryService.Classify(null!, null!, null!, null!);
        Assert.Equal(CategoryService.Unclassified, c);
    }

    [Fact]
    public void Classify_EmptyEverything_Unclassified()
    {
        Assert.Equal(CategoryService.Unclassified, CategoryService.Classify("", "", "", ""));
    }

    [Fact]
    public void Classify_UnknownProcess_Unclassified()
    {
        Assert.Equal(CategoryService.Unclassified,
            CategoryService.Classify("zzz-unknown-app-xyz", @"C:\Temp\zzz.exe", "Nobody Inc", "whatever"));
    }

    [Theory]
    [InlineData("chrome", "浏览与资讯")]
    [InlineData("chrome.exe", "浏览与资讯")]
    [InlineData("CHROME", "浏览与资讯")]
    [InlineData("CHROME.EXE", "浏览与资讯")]
    [InlineData("weixin", "通讯与协作")]
    [InlineData("steam", "游戏")]
    [InlineData("winword", "办公与文档")]
    [InlineData("devenv", "开发与创作")]
    [InlineData("explorer", "系统与工具")]
    public void Classify_ExactNames(string process, string expected)
    {
        Assert.Equal(expected, CategoryService.Classify(process, "", "", ""));
    }

    [Fact]
    public void Classify_KeywordInProcessName_Matches()
    {
        // 精确名单里没有 clash-verge，靠关键字层命中
        Assert.Equal("系统与工具", CategoryService.Classify("clash-verge", "", "", ""));
    }

    [Fact]
    public void Classify_PathSegment_Matches()
    {
        Assert.Equal("游戏",
            CategoryService.Classify("launcher", @"D:\Games\Genshin Impact\launcher.exe", "", ""));
    }

    [Fact]
    public void Classify_CompanyFallback_Matches()
    {
        Assert.Equal("游戏", CategoryService.Classify("whatever", "", "Rovio Entertainment Oy", ""));
    }

    [Fact]
    public void Classify_VeryLongInput_Completes()
    {
        var huge = new string('x', 1_000_000);
        var c = CategoryService.Classify(huge, huge, huge, huge);
        Assert.Equal(CategoryService.Unclassified, c);
    }

    [Fact]
    public void Classify_UnicodeAndSpaces_DoesNotThrow()
    {
        var c = CategoryService.Classify("　微信　", "", "", "");
        Assert.NotNull(c);
    }

    [Fact]
    public void Classify_AllResultsAreKnownCategories()
    {
        string[] samples =
        [
            "chrome", "weixin", "steam", "winword", "devenv", "explorer",
            "clash-verge", "unknown-a", "unknown-b", "node", "7zFM", "Nahimic3",
        ];
        var allowed = CategoryService.Categories.Append(CategoryService.Unclassified).ToHashSet();
        foreach (var s in samples)
            Assert.Contains(CategoryService.Classify(s, "", "", ""), allowed);
    }

    [Fact]
    public void MapLegacy_KnownAndUnknown()
    {
        Assert.Equal("开发与创作", CategoryService.MapLegacy("设计创作"));
        Assert.Equal(CategoryService.Unclassified, CategoryService.MapLegacy("其他"));
        Assert.Equal("某个新分类", CategoryService.MapLegacy("某个新分类"));
    }
}
