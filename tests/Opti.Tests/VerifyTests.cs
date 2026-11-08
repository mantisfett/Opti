using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Fixes.Backends;
using Opti.Fixes.Journal;
using Opti.Fixes.Services;

namespace Opti.Tests;

public class VerifyTests
{
    private static readonly string A = Targets.Reg(@"HKCU\Software\Opti\Test", "A");
    private static readonly string B = Targets.Reg(@"HKCU\Software\Opti\Test", "B");
    private static readonly string C = Targets.Reg(@"HKCU\Software\Opti\Test", "C");

    private static (BackendRegistry Reg, InMemoryBackend Map, InMemoryChangeJournal J) Setup()
    {
        var reg = InMemoryBackend.CreateRegistry(out var map);
        var r = map[ChangeType.Registry];
        r.Seed(A, StateValue.DWord(0));
        r.Seed(B, StateValue.DWord(0));
        r.Seed(C, StateValue.DWord(0));
        var j = new InMemoryChangeJournal();
        var ctx = new ChangeContext(reg, j, "s1", "FIX", new SystemProfile());
        ctx.Set(ChangeType.Registry, A, StateValue.DWord(1));
        ctx.Set(ChangeType.Registry, B, StateValue.DWord(1));
        ctx.Set(ChangeType.Registry, C, StateValue.DWord(1));
        return (reg, r, j);
    }

    [Fact]
    public void Verify_DistinguishesInPlace_RevertedByWindows_AndChangedByUser()
    {
        var (reg, map, j) = Setup();
        map.Write(B, StateValue.DWord(0)); // «обновление Windows» вернуло исходное
        map.Write(C, StateValue.DWord(5)); // человек поставил своё

        var report = new VerifyService(reg, j).Verify();
        Assert.Equal(VerifyStatus.InPlace, report.Items.Single(i => i.Record.Target == A).Status);
        Assert.Equal(VerifyStatus.Reverted, report.Items.Single(i => i.Record.Target == B).Status);
        Assert.Equal(VerifyStatus.Changed, report.Items.Single(i => i.Record.Target == C).Status);
        Assert.Contains("вернули 1", report.Summary());
    }

    [Fact]
    public void Reapply_RestoresOptiValue_AndRollbackStillReturnsOriginal()
    {
        var (reg, map, j) = Setup();
        map.Write(B, StateValue.DWord(0));
        var svc = new VerifyService(reg, j);
        var (done, errors) = svc.Reapply(svc.Verify().Reverted);
        Assert.Equal(1, done);
        Assert.Empty(errors);
        Assert.Equal(StateValue.DWord(1), map.Read(B));
        Assert.True(svc.Verify().AllInPlace);

        // Откат сессии возвращает значения до Opti — и для повторно применённой настройки тоже
        var rb = new RollbackService(reg, j).RollbackSession("s1");
        Assert.Empty(rb.ChangedSinceOpti);
        Assert.Equal(StateValue.DWord(0), map.Read(A));
        Assert.Equal(StateValue.DWord(0), map.Read(B));
        Assert.Equal(StateValue.DWord(0), map.Read(C));
    }

    [Fact]
    public void Verify_LooksOnlyAtLatestRecordOfChain_AndSkipsRolledBack()
    {
        var (reg, map, j) = Setup();
        // A/B-проверка: та же настройка изменена второй раз — сверяем с последним значением
        new ChangeContext(reg, j, "s1", "FIX", new SystemProfile()).Set(ChangeType.Registry, A, StateValue.DWord(2));
        new RollbackService(reg, j).RollbackRecords(j.GetAll().Where(r => r.Target == C));
        var report = new VerifyService(reg, j).Verify();
        Assert.Equal(2, report.Items.Count);
        Assert.True(report.AllInPlace);
    }

    [Fact]
    public void Verify_WithNoActiveChanges_SaysNothingToCheck()
    {
        var reg = InMemoryBackend.CreateRegistry(out _);
        var report = new VerifyService(reg, new InMemoryChangeJournal()).Verify();
        Assert.Empty(report.Items);
        Assert.Contains("проверять нечего", report.Summary());
    }
}
