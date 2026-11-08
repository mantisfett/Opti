using System.Diagnostics;
using Opti.Analysis;
using Opti.Analysis.Detectors;
using Opti.Core.Fixes;
using Opti.Core.Json;
using Opti.Core.Profile;
using Opti.Core.Strategy;
using Opti.Fixes.Backends;
using Opti.Fixes.Catalog;
using Opti.Fixes.Journal;
using Opti.Fixes.Services;
using Opti.Measure.Capture;
using Opti.Measure.Stats;
using Opti.Workflow;
using Opti.Workflow.Measuring;
using Opti.Workflow.Reports;
using Opti.Workflow.Sessions;
using Opti.Core.Localization;

namespace Opti.Cli;

/// <summary>Отчёт, откат, проверка изменений, удаление, отчёт о проблеме, история.</summary>
internal static partial class Commands
{
    // ------------------------------------------------------------------ report / rollback / history

    public static int Report(Args a)
    {
        RequireWindows(Loc.T("Отчёт по сессии"));
        using var host = new CliHost();
        var s = a.Get("session") is { } id ? host.Workflow.Store.Load(id) ?? throw new ArgumentException(Loc.T($"Сессия {id} не найдена"))
                                           : host.Workflow.Store.All().FirstOrDefault() ?? throw new InvalidOperationException(Loc.T("Сессий ещё нет."));
        var path = WriteReport(host, s, null, a.Get("out"));
        Console.WriteLine(Loc.T($"Отчёт: {path}"));
        if (a.Has("open")) OpenFile(path);
        return 0;
    }

    public static int Rollback(Args a)
    {
        RequireWindows(Loc.T("Откат"));
        using var host = new CliHost();
        var svc = new RollbackService(WindowsBackends.Create(), host.Journal);
        bool force = a.Has("force");
        RollbackReport rep;
        if (a.Get("fix") is { } fix)
        {
            rep = a.Get("session") is { } sid && host.Workflow.Store.Load(sid) is { } s1
                ? host.Workflow.RollbackFix(s1, fix, force)
                : svc.RollbackFix(fix, a.Get("session"), force);
        }
        else if (a.Get("session") is { } s)
        {
            rep = host.Workflow.Store.Load(s) is { } session ? host.Workflow.RollbackSession(session, force)
                : svc.RollbackRecords(host.Journal.GetAll().Where(r => r.SessionId.StartsWith(s, StringComparison.Ordinal)), force);
        }
        else if (a.Has("all"))
        {
            if (!a.Has("yes") && !Output.Confirm(Loc.T("Откатить ВСЕ изменения Opti?"))) return 1;
            rep = svc.RollbackAll(force);
        }
        else throw new ArgumentException(Loc.T("Укажите --fix ID, --session ID или --all"));
        PrintRollback(rep);
        return rep.Errors.Count > 0 ? 3 : 0;
    }

    public static int Verify(Args a)
    {
        RequireWindows(Loc.T("Проверка изменений"));
        using var host = new CliHost();
        var wf = host.Workflow;
        var report = wf.Verify();
        Output.Header(report.Summary());
        foreach (var i in report.Items.Where(i => i.Status != VerifyStatus.InPlace))
        {
            var (text, color) = i.Status switch
            {
                VerifyStatus.Reverted => (Loc.T("возвращено Windows/драйвером"), ConsoleColor.Yellow),
                VerifyStatus.Changed => (Loc.T("изменено после Opti"), ConsoleColor.Gray),
                _ => (Loc.T("не проверено: ") + i.Error, ConsoleColor.Red),
            };
            Output.Color(color, $"  {wf.FixTitle(i.Record.FixId)} — {text}\n");
            Output.Color(ConsoleColor.DarkGray, Loc.T($"     {i.Record.Type} {i.Record.Target}: сейчас {i.Current}, Opti ставил {i.Record.After}\n"));
        }
        if (a.Has("reapply"))
        {
            // Параметры загрузки (BCD) — только с --all: при BitLocker их изменение может запросить ключ восстановления
            var items = report.Items.Where(i => a.Has("all")
                ? i.Status is VerifyStatus.Reverted or VerifyStatus.Changed
                : i.Status == VerifyStatus.Reverted && i.Record.Type != ChangeType.Bcd).ToList();
            var (done, errors) = wf.Reapply(items);
            Output.Color(ConsoleColor.Green, Loc.T($"Применено снова: {done}\n"));
            foreach (var e in errors) Output.Color(ConsoleColor.Red, Loc.T("  ошибка: ") + e + "\n");
            return errors.Count > 0 ? 3 : 0;
        }
        return report.AllInPlace ? 0 : 1;
    }

    public static int Uninstall(Args a)
    {
        RequireWindows(Loc.T("Удаление"));
        if (!a.Has("yes") && !Output.Confirm(Loc.T("Откатить ВСЕ изменения Opti и удалить его папку данных?"))) return 1;
        UninstallReport r;
        using (var host = new CliHost())
            r = Uninstaller.Run(host.Workflow, host.Journal, new WindowsResumeScheduler(), OptiPaths.DataDir);
        PrintRollback(r.Rollback);
        if (r.Rollback.Errors.Count > 0)
        {
            Output.Color(ConsoleColor.Red, Loc.T("Часть изменений откатить не удалось — папка данных оставлена, без журнала их не вернуть.\n"));
            return 3;
        }
        Console.WriteLine(r.DataRemovalScheduled
            ? Loc.T($"Папка {r.DataDir} будет удалена через несколько секунд. Файл opti-cli.exe удалите сами. Перезагрузите компьютер.")
            : Loc.T($"Удалите папку {r.DataDir} вручную. Перезагрузите компьютер."));
        return 0;
    }

    public static int BugReport(Args a)
    {
        using var journal = new SqliteChangeJournal(OptiPaths.JournalPath);
        var path = ProblemReport.Create(OptiPaths.DataDir, journal, a.Get("out") ?? Environment.CurrentDirectory, AppInfo.Version);
        Console.WriteLine(Loc.T($"Отчёт о проблеме: {path}"));
        Console.WriteLine(Loc.T("Он никуда не отправляется — приложите его к обращению сами."));
        return 0;
    }

    public static int History(Args a)
    {
        RequireWindows(Loc.T("История"));
        using var host = new CliHost();
        var all = host.Journal.GetAll();
        var sessions = host.Workflow.Store.All();
        if (sessions.Count == 0 && all.Count == 0) { Console.WriteLine(Loc.T("Истории пока нет.")); return 0; }
        foreach (var s in sessions)
        {
            var outcome = s.Outcome switch
            {
                SessionOutcome.Improved => Loc.T("стало лучше"), SessionOutcome.Worse => Loc.T("стало хуже"), SessionOutcome.NoEffect => Loc.T("без эффекта"),
                SessionOutcome.RolledBack => Loc.T("откачено"), _ => Loc.T("без вердикта"),
            };
            Output.Header($"{s.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm} · {s.Id} · {s.Goal.Ru()} · {outcome} · {s.Stage}");
            foreach (var r in all.Where(r => r.SessionId.StartsWith(s.Id, StringComparison.Ordinal)))
                Console.WriteLine(Loc.T($"  {(r.RolledBack ? "↩" : r.Committed ? "✓" : "?")} {r.FixId} {r.Type} {r.Target}: {r.Before} → {r.After}{(r.Committed ? "" : " (не подтверждено: программа завершилась во время изменения)")}"));
            foreach (var ab in s.AbTests) Console.WriteLine($"  A/B {ab.FixId}: {ab.Summary ?? ab.Phase.ToString()}");
        }
        var orphan = all.Where(r => sessions.All(s => !r.SessionId.StartsWith(s.Id, StringComparison.Ordinal))).ToList();
        if (orphan.Count > 0)
        {
            Output.Header(Loc.T("Изменения вне сессий"));
            foreach (var r in orphan) Console.WriteLine($"  {(r.RolledBack ? "↩" : "✓")} {r.SessionId} {r.FixId} {r.Type} {r.Target}: {r.Before} → {r.After}");
        }
        return 0;
    }
}
