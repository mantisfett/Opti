using System.IO.Compression;
using Opti.Launcher;

namespace Opti.Tests;

public sealed class LauncherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("opti-launch-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private static MemoryStream Payload(params (string Name, string Text)[] files)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open());
                w.Write(text);
            }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void ExtractsOnce_ThenReuses()
    {
        var inst = new PayloadInstaller(Path.Combine(_dir, "app"));
        using var p = Payload(("Opti.exe", "gui"), ("opti-cli.exe", "cli"), ("sub/lib.dll", "x"));
        var dir = inst.EnsureExtracted(p, "2.0.0", out var first);
        Assert.True(first);
        Assert.Equal("gui", File.ReadAllText(Path.Combine(dir, "Opti.exe")));
        Assert.Equal("x", File.ReadAllText(Path.Combine(dir, "sub", "lib.dll")));
        Assert.StartsWith("2.0.0-", Path.GetFileName(dir), StringComparison.Ordinal);

        var again = inst.EnsureExtracted(p, "2.0.0", out var second);
        Assert.Equal(dir, again);
        Assert.False(second);
        Assert.Empty(Directory.GetDirectories(inst.Root, "*.tmp-*"));
    }

    [Fact]
    public void IncompleteExtraction_IsRedone()
    {
        var inst = new PayloadInstaller(Path.Combine(_dir, "app"));
        using var p = Payload(("Opti.exe", "gui"));
        var dir = inst.EnsureExtracted(p, "2.0.0", out _);
        // Прерванная распаковка или повреждение: нет метки — папка не считается готовой
        File.Delete(Path.Combine(dir, PayloadInstaller.MarkerName));
        File.WriteAllText(Path.Combine(dir, "Opti.exe"), "испорчено");
        inst.EnsureExtracted(p, "2.0.0", out var redone);
        Assert.True(redone);
        Assert.Equal("gui", File.ReadAllText(Path.Combine(dir, "Opti.exe")));
    }

    [Fact]
    public void NewVersion_GetsOwnFolder_AndOldOnesAreCleanedUp()
    {
        var inst = new PayloadInstaller(Path.Combine(_dir, "app"));
        string Extract(string version, string text)
        {
            using var p = Payload(("Opti.exe", text));
            var d = inst.EnsureExtracted(p, version, out _);
            Directory.SetLastWriteTimeUtc(d, DateTime.UtcNow.AddMinutes(int.Parse(version.Split('.')[0])));
            return d;
        }
        var v1 = Extract("1.0.0", "a");
        var v2 = Extract("2.0.0", "b");
        var v3 = Extract("3.0.0", "c");
        Assert.True(Directory.Exists(v3));
        Assert.True(Directory.Exists(v2)); // одна предыдущая остаётся (может ещё работать)
        Assert.False(Directory.Exists(v1));
    }

    [Fact]
    public void PathTraversalInPayload_IsRejected()
    {
        var inst = new PayloadInstaller(Path.Combine(_dir, "app"));
        using var p = Payload(("../evil.dll", "x"));
        Assert.ThrowsAny<IOException>(() => inst.EnsureExtracted(p, "2.0.0", out _));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.dll")));
        Assert.Empty(Directory.GetDirectories(inst.Root));
    }
}
