using ScreenTime.Services;
using Xunit;

namespace ScreenTime.Tests;

/// <summary>网页隐私规则：内置名单、用户追加、脱敏。纯字符串逻辑，不依赖数据库。</summary>
public class WebPrivacyTests
{
    [Fact]
    public void ResolveList_IncludesBuiltinAndUser_NoDuplicates()
    {
        var list = WebPrivacy.ResolveList("MyBank.example; foo.com,FOO.COM");

        Assert.Contains("mail.google.com", list);
        Assert.Contains("mybank.example", list);
        Assert.Single(list, x => x == "foo.com");
    }

    [Fact]
    public void ResolveList_EmptyUserValue_StillHasBuiltin()
    {
        var list = WebPrivacy.ResolveList("");
        Assert.NotEmpty(list);
        Assert.Contains("bitwarden.com", list);
    }

    [Fact]
    public void IsExcluded_MatchesSubstringIgnoringCase()
    {
        var list = WebPrivacy.ResolveList("");

        Assert.True(WebPrivacy.IsExcluded("https://mail.google.com/mail/u/0", list));
        Assert.True(WebPrivacy.IsExcluded("alipay.com/pay", list));
        Assert.False(WebPrivacy.IsExcluded("github.com", list));
        Assert.False(WebPrivacy.IsExcluded("", list));
        Assert.False(WebPrivacy.IsExcluded(null, list));
    }

    [Fact]
    public void FilterSite_DropsExcludedAndKeepsOthers()
    {
        var list = WebPrivacy.ResolveList("secret.example");

        Assert.Equal("", WebPrivacy.FilterSite("secret.example/page", list));
        Assert.Equal("github.com/a/b", WebPrivacy.FilterSite("github.com/a/b", list));
    }

    [Fact]
    public void CleanUrl_RemovesFragmentAndTrackingParams()
    {
        Assert.Equal("https://example.com/a?id=1",
            WebPrivacy.CleanUrl("https://example.com/a?utm_source=x&id=1#top"));
        Assert.Equal("https://example.com/a",
            WebPrivacy.CleanUrl("https://example.com/a?utm_medium=x"));
    }

    [Fact]
    public void CleanUrl_TruncatesTo200()
    {
        var longUrl = "https://example.com/" + new string('a', 300);
        Assert.Equal(200, WebPrivacy.CleanUrl(longUrl).Length);
    }
}
