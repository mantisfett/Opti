using Opti.Core.Diagnostics;
using Opti.Core.Fixes;
using Opti.Core.Profile;

namespace Opti.Fixes.Services;

public enum VerifyStatus
{
    /// <summary>Значение такое, как поставил Opti.</summary>
    InPlace,
    /// <summary>Вернулось исходное значение — обычно это делают обновления Windows или драйвера.</summary>
    Reverted,
    /// <summary>Значение другое — его поменяли вы или другая программа.</summary>
    Changed,
    /// <summary>Прочитать не удалось.</summary>
    Unreadable,
}

/// <param name="Record">Последняя действующая запись журнала для этой настройки.</param>
public sealed record VerifyItem(ChangeRecord Record, VerifyStatus Status, StateValue? Current, string? Error = null);

public sealed class VerifyReport
{
    public List<VerifyItem> Items { get; } = new();
    public int InPlace => Items.Count(i => i.Status == VerifyStatus.InPlace);
    public IEnumerable<VerifyItem> Reverted => Items.Where(i => i.Status == VerifyStatus.Reverted);
    public IEnumerable<VerifyItem> Changed => Items.Where(i => i.Status == VerifyStatus.Changed);
    public IEnumerable<VerifyItem> Unreadable => Items.Where(i => i.Status == VerifyStatus.Unreadable);
    public bool AllInPlace => Items.All(i => i.Status == VerifyStatus.InPlace);

    public string Summary()
    {
        if (Items.Count == 0) return "Действующих изменений Opti нет — проверять нечего.";
        if (AllInPlace) return $"Все изменения Opti на месте ({InPlace}).";
        var parts = new List<string> { $"на месте {InPlace}" };
        if (Reverted.Any()) parts.Add($"Windows или драйвер вернули {Reverted.Count()}");
        if (Changed.Any()) parts.Add($"изменено после Opti {Changed.Count()}");
        if (Unreadable.Any()) parts.Add($"не удалось проверить {Unreadable.Count()}");
        return "Проверка изменений: " + string.Join(", ", parts) + ".";
    }
}

/// <summary>
/// «Страж»: сверяет действующие изменения из журнала с тем, что сейчас в системе. Обновления Windows и драйверов
/// нередко возвращают настройки (частоту экрана, схему питания, ключи реестра) — тогда исправление тихо перестаёт работать.
/// Повторное применение пишется в журнал так же, как первое: откат вернёт исходное значение (до Opti).
/// </summary>
public sealed class VerifyService
{
    private readonly IBackendRegistry _backends;
    private readonly IChangeJournal _journal;

    public VerifyService(IBackendRegistry backends, IChangeJournal journal)
    {
        _backends = backends;
        _journal = journal;
    }

    /// <summary>
    /// Файлы не сверяются: значение там — путь к резервной копии, а чтение файла создаёт новую копию.
    /// </summary>
    public static bool IsVerifiable(ChangeType t) => t != ChangeType.File;

    public VerifyReport Verify()
    {
        var report = new VerifyReport();
        // Для каждой настройки важна только последняя действующая запись (A/B-проверки меняют одно значение дважды)
        var latest = _journal.GetAll()
            .Where(r => !r.RolledBack && IsVerifiable(r.Type))
            .GroupBy(r => (r.Type, Target: r.Target.ToUpperInvariant()))
            .Select(g => g.MaxBy(r => r.Id)!)
            .OrderBy(r => r.Id);
        foreach (var rec in latest)
        {
            try
            {
                var current = _backends.Get(rec.Type).Read(rec.Target);
                var status = RollbackService.Same(current, rec.After) ? VerifyStatus.InPlace
                           : RollbackService.Same(current, OriginalOf(rec)) ? VerifyStatus.Reverted
                           : VerifyStatus.Changed;
                report.Items.Add(new VerifyItem(rec, status, current));
            }
            catch (Exception ex)
            {
                report.Items.Add(new VerifyItem(rec, VerifyStatus.Unreadable, null, ex.Message));
            }
        }
        OptiLog.Info(report.Summary());
        return report;
    }

    /// <summary>Исходное значение до Opti — Before самой ранней действующей записи цепочки.</summary>
    private StateValue OriginalOf(ChangeRecord rec) => _journal.GetActiveOriginal(rec.Type, rec.Target) ?? rec.Before;

    /// <summary>Применяет значения Opti снова. Возвращает число восстановленных настроек и ошибки.</summary>
    public (int Reapplied, List<string> Errors) Reapply(IEnumerable<VerifyItem> items)
    {
        int done = 0;
        var errors = new List<string>();
        foreach (var item in items.Where(i => i.Status is VerifyStatus.Reverted or VerifyStatus.Changed))
        {
            var rec = item.Record;
            try
            {
                var ctx = new ChangeContext(_backends, _journal, rec.SessionId, rec.FixId, new SystemProfile());
                if (ctx.Set(rec.Type, rec.Target, rec.After) is not null) done++;
            }
            catch (Exception ex)
            {
                errors.Add($"{rec.FixId} {rec.Target}: {ex.Message}");
                OptiLog.Warn($"Повторное применение {rec.FixId} {rec.Target} не удалось", ex);
            }
        }
        OptiLog.Info($"Повторно применено настроек: {done}, ошибок: {errors.Count}");
        return (done, errors);
    }
}
