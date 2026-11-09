using Opti.Workflow;

namespace Opti.Tests;

public sealed class SelfUpdaterTests
{
    private const string H1 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string H2 = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    [Fact]
    public void FindHash_ReadsSha256sumFormat()
    {
        var sums = $"{H1}  Opti.exe\r\n{H2.ToUpperInvariant()} *opti-cli.exe\n";
        Assert.Equal(H1, SelfUpdater.FindHash(sums, "Opti.exe"));
        Assert.Equal(H2, SelfUpdater.FindHash(sums, "opti-cli.exe"));
        Assert.Null(SelfUpdater.FindHash(sums, "other.exe"));
        Assert.Null(SelfUpdater.FindHash("abc  Opti.exe", "Opti.exe"));
    }

    [Fact]
    public void ParseRelease_KeepsOnlyGithubHttpsAssets()
    {
        const string json = """
        { "tag_name": "v2.0.1", "assets": [
          { "name": "Opti.exe", "browser_download_url": "https://github.com/mantisfett/Opti/releases/download/v2.0.1/Opti.exe" },
          { "name": "SHA256SUMS.txt", "browser_download_url": "https://objects.githubusercontent.com/x/SHA256SUMS.txt" },
          { "name": "opti-cli.exe", "browser_download_url": "http://github.com/evil/opti-cli.exe" },
          { "name": "evil.exe", "browser_download_url": "https://github.com.evil.example/evil.exe" } ] }
        """;
        var r = SelfUpdater.ParseRelease(json)!;
        Assert.Equal("v2.0.1", r.Tag);
        Assert.Equal(new[] { "Opti.exe", "SHA256SUMS.txt" }, r.Assets.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void CannotSelfUpdate_WithoutLauncher()
    {
        Environment.SetEnvironmentVariable("OPTI_LAUNCHER_PATH", null);
        Assert.Null(SelfUpdater.LauncherPath);
        Assert.False(SelfUpdater.CanSelfUpdate);
    }
}
