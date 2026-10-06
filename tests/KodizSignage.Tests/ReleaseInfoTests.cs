using KodizSignage.Core.Services;

namespace KodizSignage.Tests;

public class ReleaseInfoTests
{
    private const string Sample = """
    {
      "tag_name": "v1.4.0",
      "draft": false,
      "prerelease": false,
      "html_url": "https://github.com/egemenxgul/kodiz-signage/releases/tag/v1.4.0",
      "body": "Yenilikler",
      "assets": [
        { "name": "KodizSignage.exe", "browser_download_url": "https://example.test/KodizSignage.exe" },
        { "name": "KodizSignage.exe.sha256", "browser_download_url": "https://example.test/KodizSignage.exe.sha256" },
        { "name": "LICENSE", "browser_download_url": "https://example.test/LICENSE" }
      ]
    }
    """;

    [Fact]
    public void Parses_github_latest_release()
    {
        var release = ReleaseInfo.Parse(Sample)!;

        Assert.Equal(new Version(1, 4, 0), release.Version);
        Assert.Equal("https://example.test/KodizSignage.exe", release.ExeUrl);
        Assert.Equal("https://example.test/KodizSignage.exe.sha256", release.HashUrl);
        Assert.Equal("Yenilikler", release.Notes);
    }

    [Theory]
    [InlineData("""{ "tag_name": "v2.0.0", "prerelease": true, "assets": [ { "name": "KodizSignage.exe", "browser_download_url": "x" } ] }""")]
    [InlineData("""{ "tag_name": "v2.0.0", "draft": true, "assets": [ { "name": "KodizSignage.exe", "browser_download_url": "x" } ] }""")]
    [InlineData("""{ "tag_name": "v2.0.0", "assets": [ { "name": "other.zip", "browser_download_url": "x" } ] }""")]
    [InlineData("""{ "tag_name": "latest", "assets": [ { "name": "KodizSignage.exe", "browser_download_url": "x" } ] }""")]
    [InlineData("""{ "message": "API rate limit exceeded" }""")]
    [InlineData("not json")]
    public void Unusable_releases_are_ignored(string json) => Assert.Null(ReleaseInfo.Parse(json));

    [Theory]
    [InlineData("v1.3.0", 1, 3, 0)]
    [InlineData("1.3", 1, 3, 0)]
    [InlineData("V2.0.1-beta", 2, 0, 1)]
    [InlineData("1.3.0.7", 1, 3, 0)]
    public void Version_parsing(string text, int major, int minor, int build)
    {
        Assert.True(ReleaseInfo.TryParseVersion(text, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public void Newer_and_skipped_versions()
    {
        var release = ReleaseInfo.Parse(Sample)!;

        Assert.True(release.IsNewerThan(new Version(1, 3, 0), null));
        Assert.False(release.IsNewerThan(new Version(1, 4, 0), null));
        Assert.False(release.IsNewerThan(new Version(1, 5, 0), null));
        Assert.False(release.IsNewerThan(new Version(1, 3, 0), "1.4.0")); // rolled back once → not offered again
        Assert.True(release.IsNewerThan(new Version(1, 3, 0), "1.3.5"));
    }

    [Theory]
    [InlineData("ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD  KodizSignage.exe", true)]
    [InlineData("abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd\n", true)]
    [InlineData("nothex", false)]
    [InlineData("", false)]
    public void Hash_file_parsing(string text, bool valid) => Assert.Equal(valid, ReleaseInfo.ParseHash(text) is not null);

    [Fact]
    public void Release_is_built_from_the_latest_page_redirect()
    {
        var release = ReleaseInfo.FromTagUrl("egemenxgul/kodiz-signage", "https://github.com/egemenxgul/kodiz-signage/releases/tag/v1.4.0")!;

        Assert.Equal(new Version(1, 4, 0), release.Version);
        Assert.Equal("v1.4.0", release.Tag);
        Assert.Equal("https://github.com/egemenxgul/kodiz-signage/releases/download/v1.4.0/KodizSignage.exe", release.ExeUrl);
        Assert.Equal("https://github.com/egemenxgul/kodiz-signage/releases/download/v1.4.0/KodizSignage.exe.sha256", release.HashUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://github.com/egemenxgul/kodiz-signage/releases")]
    [InlineData("https://github.com/egemenxgul/kodiz-signage/releases/tag/")]
    [InlineData("https://github.com/egemenxgul/kodiz-signage/releases/tag/nightly")]
    public void Redirects_without_a_version_are_ignored(string? location) =>
        Assert.Null(ReleaseInfo.FromTagUrl("egemenxgul/kodiz-signage", location));
}
