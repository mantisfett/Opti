using Opti.Workflow.Cleanup;

namespace Opti.Tests;

public sealed class CleanupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("opti-clean-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("opti-outside-").FullName;
    public void Dispose() { Directory.Delete(_root, true); Directory.Delete(_outside, true); }

    private string Make(string rel, int bytes, TimeSpan age)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[bytes]);
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow - age);
        return p;
    }

    [Fact]
    public async Task OnlyOldFilesAreDeleted_LinksAreNotFollowed()
    {
        var old1 = Make("a.tmp", 1000, TimeSpan.FromDays(5));
        var old2 = Make(@"sub/deep/b.log", 2000, TimeSpan.FromDays(3));
        var fresh = Make("fresh.tmp", 500, TimeSpan.FromHours(1));
        var readOnly = Make("ro.tmp", 300, TimeSpan.FromDays(9));
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        // Ссылка на папку снаружи — её содержимое трогать нельзя
        var precious = Path.Combine(_outside, "document.docx");
        File.WriteAllBytes(precious, new byte[100]);
        File.SetLastWriteTimeUtc(precious, DateTime.UtcNow - TimeSpan.FromDays(100));
        Directory.CreateSymbolicLink(Path.Combine(_root, "link-to-docs"), _outside);

        var t = new FolderCleanupTarget("t", "test", "", () => new[] { _root }, TimeSpan.FromDays(2));
        var est = await t.EstimateAsync();
        Assert.Equal(3, est.Files);
        Assert.Equal(3300, est.Bytes);

        var r = await t.CleanAsync();
        Assert.Equal(3, r.Deleted);
        Assert.Equal(3300, r.FreedBytes);
        Assert.False(File.Exists(old1));
        Assert.False(File.Exists(old2));
        Assert.False(File.Exists(readOnly));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(precious));                     // по ссылке не прошли
        Assert.True(Directory.Exists(_root));                    // корень не удаляется
    }

    [Fact]
    public async Task FileFilter_AndNonRecursive()
    {
        Make("thumbcache_1024.db", 100, TimeSpan.FromDays(3));
        Make("iconcache_1.db", 100, TimeSpan.FromDays(3));
        Make(@"sub/thumbcache_x.db", 100, TimeSpan.FromDays(3));
        var t = new FolderCleanupTarget("t", "", "", () => new[] { _root }, TimeSpan.FromDays(1),
            fileFilter: f => Path.GetFileName(f).StartsWith("thumbcache_", StringComparison.Ordinal), recursive: false);
        var r = await t.CleanAsync();
        Assert.Equal(1, r.Deleted);
        Assert.True(File.Exists(Path.Combine(_root, "iconcache_1.db")));
        Assert.True(File.Exists(Path.Combine(_root, "sub", "thumbcache_x.db")));
    }

    [Fact]
    public void DangerousRootsAreRefused()
    {
        Assert.False(FolderCleanupTarget.IsSafeRoot(Path.GetPathRoot(_root)!));
        Assert.False(FolderCleanupTarget.IsSafeRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.True(FolderCleanupTarget.IsSafeRoot(_root));
    }

    [Fact]
    public async Task MissingRoots_AreIgnored()
    {
        var t = new FolderCleanupTarget("t", "", "", () => new[] { Path.Combine(_root, "nope"), "" }, TimeSpan.Zero);
        Assert.Equal(0, (await t.EstimateAsync()).Files);
        Assert.Equal(0, (await t.CleanAsync()).Deleted);
    }

    [Fact]
    public void SizeFormatting()
    {
        Assert.Equal("512 Б", SizeFormat.Bytes(512));
        Assert.Equal("3 МБ", SizeFormat.Bytes(3L << 20));
        Assert.Equal("1,5 ГБ", SizeFormat.Bytes((3L << 30) / 2).Replace('.', ','));
    }

    [Fact]
    public async Task LargeVideos_AreListedBiggestFirst_NothingDeleted()
    {
        Make("v/small.mp4", 100, TimeSpan.Zero);
        var big = Make("v/game.mkv", 3000, TimeSpan.FromDays(40));
        var bigger = Make("v/deep/stream.mp4", 5000, TimeSpan.Zero);
        Make("v/archive.zip", 9000, TimeSpan.Zero); // не видео
        var t = new LargeFilesTarget("v", "", "", () => new[] { Path.Combine(_root, "v"), Path.Combine(_root, "v") }, minBytes: 1000);
        var est = await t.EstimateAsync();
        Assert.Equal(2, est.Files);
        Assert.Equal(8000, est.Bytes);
        Assert.Equal(new[] { Path.GetFullPath(bigger), Path.GetFullPath(big) }, t.Details.Select(d => Path.GetFullPath(d.Path)));
        Assert.Equal(0, (await t.CleanAsync()).Deleted);
        Assert.True(File.Exists(big) && File.Exists(bigger));
        Assert.NotNull(t.ManualUri);
    }

    [Fact]
    public async Task ManualTarget_WarnsOnlyAboveThreshold()
    {
        Make("dl/setup.exe", 4000, TimeSpan.Zero);
        var dir = Path.Combine(_root, "dl");
        ICleanupTarget t = new ManualCleanupTarget("downloads", "", "", () => new[] { dir }, () => dir, warnAboveBytes: 3000, warnText: "много");
        Assert.Equal(dir, t.ManualUri);
        Assert.Equal("Открыть папку", t.ManualActionText);
        var est = await t.EstimateAsync();
        Assert.True(est.Bytes > t.WarnAboveBytes);
        Assert.Equal("много", t.WarnText);
        Assert.Equal(0, (await t.CleanAsync()).Deleted);
        Assert.True(File.Exists(Path.Combine(dir, "setup.exe")));
    }
}
