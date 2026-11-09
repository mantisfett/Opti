using System.Security.AccessControl;
using System.Security.Principal;
using Opti.Fixes.Backends;
using Opti.Workflow;
using Opti.Workflow.Sessions;

namespace Opti.Tests;

public sealed class SecurityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("opti-sec-").FullName;
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* права могли измениться в тесте */ }
    }

    /// <summary>Захват в отдельном потоке — как из другого процесса (мьютекс принадлежит потоку-владельцу).</summary>
    private static SingleInstance? AcquireOnOtherThread(string name)
    {
        SingleInstance? result = null;
        var t = new Thread(() => result = SingleInstance.TryAcquire(name: name));
        t.Start();
        t.Join();
        return result;
    }

    [Fact]
    public void SingleInstance_SecondAcquireFails_UntilFirstReleased()
    {
        var name = @"Global\Opti.Test." + Guid.NewGuid().ToString("N");
        using (var first = SingleInstance.TryAcquire(name: name))
        {
            Assert.NotNull(first);
            // Именованные мьютексы между потоками одного процесса так ведут себя только в Windows.
            if (OperatingSystem.IsWindows()) Assert.Null(AcquireOnOtherThread(name));
        }
        // Поток, захвативший мьютекс, завершился — для Windows это «брошенный» мьютекс, его можно забрать
        var again = AcquireOnOtherThread(name);
        Assert.NotNull(again);
    }

    [Fact]
    public void ResumeBinary_CopiesIntoProtectedDir_AndRefreshesOnChange()
    {
        var exe = Path.Combine(_dir, "Opti.exe");
        File.WriteAllText(exe, "v1");
        var bin = Path.Combine(_dir, "data", "bin");

        var copy = ResumeBinary.Protect(exe, bin);
        Assert.Equal(Path.Combine(bin, "Opti.exe"), copy);
        Assert.Equal("v1", File.ReadAllText(copy));

        File.WriteAllText(exe, "v2-longer");
        Assert.Equal("v2-longer", File.ReadAllText(ResumeBinary.Protect(exe, bin)));
        // Запуск из самой копии — копировать нечего
        Assert.Equal(copy, ResumeBinary.Protect(copy, bin));
    }

    [Fact]
    public void ResumeBinary_FallsBackToOriginal_WhenCopyFails()
    {
        var missing = Path.Combine(_dir, "нет-такого.exe");
        Assert.Equal(missing, ResumeBinary.Protect(missing, Path.Combine(_dir, "bin")));
    }

    [Fact]
    public void DataDir_IsLockedDownForNonAdmins_OnWindows()
    {
        if (!OperatingSystem.IsWindows() || !DataDirSecurity.IsElevated()) return; // проверяется в CI на Windows (администратор)
        var data = Path.Combine(_dir, "Opti");
        var notes = DataDirSecurity.Harden(data);
        Assert.Empty(notes);

        var sec = new DirectoryInfo(data).GetAccessControl();
        Assert.True(sec.AreAccessRulesProtected);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if ((SecurityIdentifier)r.IdentityReference != users) continue;
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
            Assert.Equal(0, (int)(r.FileSystemRights & (FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete)));
        }
        // Повторный запуск ничего не трогает
        File.WriteAllText(Path.Combine(data, "journal.db"), "");
        Assert.Empty(DataDirSecurity.Harden(data));
        Assert.True(File.Exists(Path.Combine(data, "journal.db")));
    }
}

public sealed class ProblemReportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("opti-report-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Report_ContainsLogsSessionsJournal_AndHidesUserName()
    {
        var data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        Directory.CreateDirectory(Path.Combine(data, "sessions"));
        File.WriteAllText(Path.Combine(data, "logs", "opti-20260101.log"), $@"путь C:\Users\{Environment.UserName}\Downloads\Opti.exe");
        File.WriteAllText(Path.Combine(data, "sessions", "s1.json"), "{}");
        File.WriteAllText(Path.Combine(data, "settings.json"), "{}");
        var journal = new Opti.Fixes.Journal.InMemoryChangeJournal();

        var zipPath = ProblemReport.Create(data, journal, Path.Combine(_dir, "out"), "test");
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("about.txt", names);
        Assert.Contains("journal.json", names);
        Assert.Contains("logs/opti-20260101.log", names);
        Assert.Contains("sessions/s1.json", names);
        Assert.Contains("settings.json", names);
        using var rd = new StreamReader(zip.GetEntry("logs/opti-20260101.log")!.Open());
        var log = rd.ReadToEnd();
        if (Environment.UserName.Length > 1) Assert.DoesNotContain(Environment.UserName, log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERNAME%", log, StringComparison.Ordinal);
    }
}
