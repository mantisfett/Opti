using System.Diagnostics;
using System.Runtime.Versioning;
using Opti.Core.Diagnostics;
using Opti.Core.Fixes;
using Opti.Fixes.Services;
using Opti.Workflow.Sessions;

namespace Opti.Workflow;

public sealed record UninstallReport(RollbackReport Rollback, bool DataRemovalScheduled, string DataDir);

/// <summary>
/// «Удалить Opti с компьютера»: откатывает все изменения, убирает задачу продолжения и удаляет папку данных
/// (журнал, отчёты, выложенное лаунчером приложение) после выхода программы. Сам Opti.exe пользователь удаляет как обычный файл.
/// Если часть изменений откатить не удалось, папка данных не удаляется — без журнала их уже не вернуть.
/// </summary>
public static class Uninstaller
{
    [SupportedOSPlatform("windows")]
    public static UninstallReport Run(TuneWorkflow wf, IChangeJournal journal, IResumeScheduler? scheduler, string dataDir)
    {
        var rollback = new RollbackService(wf.BackendsForRollback, journal).RollbackAll();
        foreach (var s in wf.Store.All().Where(s => s.Outcome != SessionOutcome.RolledBack))
        {
            s.Outcome = SessionOutcome.RolledBack;
            s.Stage = SessionStage.Closed;
            wf.Store.Save(s);
        }
        try { scheduler?.Cancel(); } catch (Exception ex) { OptiLog.Warn("Задача продолжения не удалена", ex); }

        bool scheduled = false;
        if (rollback.Errors.Count == 0)
        {
            scheduled = ScheduleRemoval(dataDir);
            OptiLog.Info("Удаление Opti: откат выполнен, папка данных будет удалена после выхода.");
        }
        return new UninstallReport(rollback, scheduled, dataDir);
    }

    /// <summary>Папку нельзя удалить, пока из неё запущена программа, — удаляем отдельным процессом через несколько секунд.</summary>
    [SupportedOSPlatform("windows")]
    private static bool ScheduleRemoval(string dataDir)
    {
        var full = Path.GetFullPath(dataDir).TrimEnd('\\');
        // Защита от удаления чего-то не того: только папка Opti, без кавычек и спецсимволов cmd в пути
        if (!string.Equals(Path.GetFileName(full), "Opti", StringComparison.OrdinalIgnoreCase) || full.IndexOfAny(['"', '&', '|', '<', '>', '^', '%']) >= 0)
            return false;
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c ping 127.0.0.1 -n 6 >nul & rmdir /s /q \"{full}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
            });
            return true;
        }
        catch (Exception ex)
        {
            OptiLog.Warn("Не удалось запланировать удаление папки данных", ex);
            return false;
        }
    }
}
