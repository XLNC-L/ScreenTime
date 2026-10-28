using ScreenTime.Services;
using Xunit;

namespace ScreenTime.Tests;

/// <summary>显示名清洗、外壳判断、浏览器标题清洗、用途解析。</summary>
public class TextRulesTests
{
    // ---------- AppDisplayService ----------

    [Fact]
    public void DisplayName_NullInputs_DoesNotThrow()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppDisplayService.DisplayName(null!, null!)));
    }

    [Fact]
    public void DisplayName_EmptyInputs_FallsBackToUnknown()
    {
        Assert.Equal("未知应用", AppDisplayService.DisplayName("", ""));
    }

    [Fact]
    public void DisplayName_MappedProcess_UsesChineseName()
    {
        Assert.Equal("微信", AppDisplayService.DisplayName("Weixin", "WeChat"));
    }

    [Fact]
    public void DisplayName_GenericProductName_Ignored()
    {
        // 系统通用 ProductName 没有区分度，必须被过滤掉
        var name = AppDisplayService.DisplayName("OpenWith", "Microsoft® Windows® Operating System");
        Assert.DoesNotContain("Windows", name);
    }

    [Fact]
    public void DisplayName_BitnessSuffix_Trimmed()
    {
        Assert.Equal("CLion", AppDisplayService.DisplayName("clion64", ""));
    }

    [Fact]
    public void DisplayName_VeryLongFallback_DoesNotThrow()
    {
        var name = AppDisplayService.DisplayName("x", new string('长', 100_000));
        Assert.False(string.IsNullOrEmpty(name));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    [InlineData("SearchHost", "", true)]
    [InlineData("chrome", "", false)]
    public void IsShell_Cases(string? process, string? exe, bool expected)
    {
        Assert.Equal(expected, AppDisplayService.IsShell(process!, exe!));
    }

    [Fact]
    public void IsShell_SystemAppsPath_True()
    {
        Assert.True(AppDisplayService.IsShell("whatever",
            @"C:\Windows\SystemApps\Microsoft.Windows.Search_cw5n1h2txyewy\SearchApp.exe"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Program Manager", true)]
    [InlineData("UnlockingWindow", true)]
    [InlineData("正常标题", false)]
    public void IsTransientWindow_Cases(string? title, bool expected)
    {
        Assert.Equal(expected, AppDisplayService.IsTransientWindow(title));
    }

    [Fact]
    public void ChineseName_Unknown_ReturnsNull()
    {
        Assert.Null(AppDisplayService.ChineseName("no-such-process"));
        Assert.Null(AppDisplayService.ChineseName(null!));
    }

    // ---------- BrowserCatalog ----------

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("chrome", true)]
    [InlineData("chrome.exe", true)]
    [InlineData("CHROME.EXE", true)]
    [InlineData("msedge", true)]
    [InlineData("firefox", false)]
    public void IsBrowser_Cases(string? process, bool expected)
    {
        Assert.Equal(expected, BrowserCatalog.IsBrowser(process));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("知乎 - Google Chrome", "知乎")]
    [InlineData("知乎 - 个人 - Microsoft Edge", "知乎")]
    [InlineData("A 和另外 3 个页面 - Google Chrome", "A")]
    [InlineData("无后缀标题", "无后缀标题")]
    public void CleanTitle_Cases(string? title, string expected)
    {
        Assert.Equal(expected, BrowserCatalog.CleanTitle(title));
    }

    [Fact]
    public void CleanTitle_VeryLong_DoesNotThrow()
    {
        var t = new string('x', 500_000) + " - Google Chrome";
        Assert.False(string.IsNullOrEmpty(BrowserCatalog.CleanTitle(t)));
    }

    // ---------- PurposeService ----------

    [Fact]
    public void Resolve_ExplicitWins()
    {
        Assert.Equal(PurposeService.Work, PurposeService.Resolve(PurposeService.Work, "游戏"));
    }

    [Fact]
    public void Resolve_NullExplicit_FallsBackToCategory()
    {
        Assert.Equal(PurposeService.Consumption, PurposeService.Resolve(null, "游戏"));
        Assert.Equal(PurposeService.Work, PurposeService.Resolve("", "开发与创作"));
    }

    [Fact]
    public void Resolve_UnknownCategory_ReturnsEmpty()
    {
        Assert.Equal("", PurposeService.Resolve(null, "未分类"));
        Assert.Equal("", PurposeService.Resolve(null, null!));
    }

    [Fact]
    public void Display_Null_ReturnsUnknown()
    {
        Assert.Equal(PurposeService.Unknown, PurposeService.Display(null));
        Assert.Equal(PurposeService.Unknown, PurposeService.Display(""));
        Assert.Equal(PurposeService.Work, PurposeService.Display(PurposeService.Work));
    }

    [Fact]
    public void FromSite_NullOrEmpty_ReturnsFallback()
    {
        Assert.Equal("兜底", PurposeService.FromSite(null!, "兜底"));
        Assert.Equal("兜底", PurposeService.FromSite("", "兜底"));
    }

    [Fact]
    public void FromSite_KnownSite_Matched()
    {
        Assert.Equal(PurposeService.Work, PurposeService.FromSite("github.com/a/b", "兜底"));
    }

    [Fact]
    public void IsGrayArea_NullInputs_DoesNotThrow()
    {
        Assert.False(PurposeService.IsGrayArea(null!, "开发与创作"));
        Assert.True(PurposeService.IsGrayArea("", "浏览与资讯"));
    }
}
