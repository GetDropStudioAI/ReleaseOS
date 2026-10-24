using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-139 / D52: App:PublicBaseUrl builds the deep links; unset means no link; a value that could leak or mislead stops the start.</summary>
public class PublicLinksTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_means_no_link(string? value)
    {
        var l = new PublicLinks(value);
        Assert.Null(l.BaseUrl);
        Assert.Null(l.Train("t1"));
        Assert.Null(l.To("/approvals"));
    }

    [Theory]
    [InlineData("https://releaseos.example.com", "https://releaseos.example.com")]
    [InlineData("https://releaseos.example.com/", "https://releaseos.example.com")]
    [InlineData(" http://10.1.2.3:6080 ", "http://10.1.2.3:6080")]
    [InlineData("https://intranet.example.com/releaseos/", "https://intranet.example.com/releaseos")]
    public void A_plain_address_is_kept_without_its_trailing_slash(string value, string expected)
    {
        var l = new PublicLinks(value);
        Assert.Equal(expected, l.BaseUrl);
        Assert.Equal(expected + "/trains/t%201", l.Train("t 1"));
        Assert.Equal(expected + "/sync", l.To("/sync"));
    }

    [Theory]
    [InlineData("releaseos.example.com")]                       // no scheme
    [InlineData("ftp://releaseos.example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@releaseos.example.com")]   // credentials never go into a message
    [InlineData("https://releaseos.example.com/?token=abc")]
    [InlineData("https://releaseos.example.com/#frag")]
    public void A_value_that_is_not_a_plain_address_is_refused_and_names_the_key(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new PublicLinks(value));
        Assert.Contains("App:PublicBaseUrl", ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
    }
}
