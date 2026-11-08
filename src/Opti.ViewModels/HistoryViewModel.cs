using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Fixes.Services;
using Opti.Workflow.Reports;
using Opti.Workflow.Sessions;
using Opti.Measure.Stats;
using Opti.Core.Fixes;
using Opti.Core.Strategy;

namespace Opti.ViewModels;

public sealed class SessionItem
{
    public required TuneSession Session { get; init; }
    public string Title => $"{Session.CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm} · режим «{Session.Goal.Ru()}»";
    public required string OutcomeText { get; init; }
    public required string Color { get; init; }
    public required string Details { get; init; }
}

/// <summary>Строка «Стража»: настройка, которая сейчас не такая, как поставил Opti.</summary>
public sealed partial class VerifyRow : ObservableObject
{
    public required VerifyItem Item { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string StatusText { get; init; }
    public required string Color { get; init; }
    [ObservableProperty] private bool _isChecked;
}

public sealed partial class HistoryViewModel : PageViewModel
{
    public HistoryViewModel(MainViewModel main) : base(main) { }
    public override string Title => "История";
    public ObservableCollection<SessionItem> Sessions { get; } = new();

    // ---------------- «Страж»: на месте ли изменения ----------------

    public ObservableCollection<VerifyRow> VerifyRows { get; } = new();
    [ObservableProperty] private string? _verifyText;
    [ObservableProperty] private string _verifyColor = Palette.Neutral;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(VerifyCommand), nameof(ReapplyCommand))] private bool _isVerifying;
    [ObservableProperty] private bool _hasDrift;

    /// <summary>Показывает результат проверки (в том числе сделанной при запуске программы).</summary>
    public void ShowVerify(VerifyReport report)
    {
        VerifyRows.Clear();
        var wf = Main.Services.Workflow;
        foreach (var i in report.Items.Where(i => i.Status != VerifyStatus.InPlace))
        {
            var (status, color) = i.Status switch
            {
                VerifyStatus.Reverted when i.Record.Type == ChangeType.Bcd =>
                    ("Windows вернула параметр загрузки. Если включён BitLocker, перед повторным применением подготовьте ключ восстановления", Palette.Warn),
                VerifyStatus.Reverted => ("Windows или драйвер вернули исходное значение", Palette.Warn),
                VerifyStatus.Changed => ("изменено после Opti — возможно, вами", Palette.Neutral),
                _ => ("не удалось проверить: " + i.Error, Palette.Bad),
            };
            var row = new VerifyRow
            {
                Item = i, Title = wf.FixTitle(i.Record.FixId), StatusText = status, Color = color,
                Detail = $"{ShortTarget(i.Record.Target)}: сейчас {Show(i.Current)}, Opti ставил {Show(i.Record.After)}",
                // Возврат Windows — почти всегда случайность; своё изменение человек отмечает сам
                // Параметры загрузки (BCD) — только осознанно: при BitLocker их изменение может запросить ключ восстановления
                IsChecked = i.Status == VerifyStatus.Reverted && i.Record.Type != ChangeType.Bcd,
            };
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(VerifyRow.IsChecked)) ReapplyCommand.NotifyCanExecuteChanged(); };
            VerifyRows.Add(row);
        }
        HasDrift = VerifyRows.Any(r => r.Item.Status is VerifyStatus.Reverted or VerifyStatus.Changed);
        VerifyText = report.Summary();
        VerifyColor = report.AllInPlace ? Palette.Good : report.Reverted.Any() ? Palette.Warn : Palette.Neutral;
        ReapplyCommand.NotifyCanExecuteChanged();
    }

    private static string Show(StateValue? v) => v is null ? "?" : v.Exists ? v.Value ?? "" : "нет значения";

    private static string ShortTarget(string target)
    {
        var t = target.Replace("|", " → ", StringComparison.Ordinal);
        return t.Length <= 70 ? t : "…" + t[^69..];
    }

    private bool CanVerify() => !IsVerifying;

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task VerifyAsync()
    {
        IsVerifying = true;
        try { ShowVerify(await Task.Run(() => Main.Services.Workflow.Verify())); }
        catch (Exception ex) { Main.ShowMessage("Проверка не удалась: " + ex.Message, error: true); }
        finally { IsVerifying = false; }
    }

    private bool CanReapply() => !IsVerifying && VerifyRows.Any(r => r.IsChecked);

    [RelayCommand(CanExecute = nameof(CanReapply))]
    private async Task ReapplyAsync()
    {
        var chosen = VerifyRows.Where(r => r.IsChecked).Select(r => r.Item).ToList();
        IsVerifying = true;
        try
        {
            var (done, errors) = await Task.Run(() => Main.Services.Workflow.Reapply(chosen));
            Main.ShowMessage($"Применено снова: {done}." + (errors.Count > 0 ? " Ошибки: " + string.Join("; ", errors) : "") +
                             " Отменить можно, как обычно, откатом сессии.", errors.Count > 0);
            ShowVerify(await Task.Run(() => Main.Services.Workflow.Verify()));
            Refresh();
        }
        finally { IsVerifying = false; }
    }

    public override void OnShown() => Refresh();

    [RelayCommand]
    public void Refresh()
    {
        Sessions.Clear();
        var journal = Main.Services.Journal.GetAll();
        foreach (var s in Main.Services.Workflow.Store.All())
        {
            var recs = journal.Where(r => r.SessionId.StartsWith(s.Id, StringComparison.Ordinal)).ToList();
            var (text, color) = s.Outcome switch
            {
                SessionOutcome.Improved => ("стало лучше", Palette.Good),
                SessionOutcome.Worse => ("стало хуже", Palette.Bad),
                SessionOutcome.NoEffect => ("без эффекта", Palette.Neutral),
                SessionOutcome.RolledBack => ("откачено", Palette.Neutral),
                _ => (s.Stage == SessionStage.Closed ? "без сравнения" : "в процессе", Palette.Accent),
            };
            Sessions.Add(new SessionItem
            {
                Session = s, OutcomeText = text, Color = color,
                Details = $"Изменений: {recs.Count(r => !r.RolledBack)} действует, {recs.Count(r => r.RolledBack)} откачено" +
                          (recs.Any(r => !r.Committed && !r.RolledBack) ? $" · не подтверждено: {recs.Count(r => !r.Committed && !r.RolledBack)} (программа завершилась во время изменения — откат безопасен)" : "") +
                          (s.AbTests.Count > 0 ? $" · A/B: {string.Join(", ", s.AbTests.Select(a => a.FixId + (a.KeptB ? " ✓" : " ✗")))}" : ""),
            });
        }
    }

    [RelayCommand]
    private void OpenReport(SessionItem item)
    {
        var s = item.Session;
        Directory.CreateDirectory(Main.Services.ReportsDir);
        var path = Path.Combine(Main.Services.ReportsDir, $"report-{s.Id}.html");
        File.WriteAllText(path, HtmlReport.Build(new ReportData
        {
            Session = s,
            Comparison = s.Before.Count > 0 && s.After.Count > 0 ? new Comparer().Compare(s.Before, s.After) : null,
            Changes = Main.Services.Journal.GetAll().Where(r => r.SessionId.StartsWith(s.Id, StringComparison.Ordinal)).ToList(),
            Catalog = Main.Services.Workflow.Catalog,
            AppVersion = Main.Services.AppVersion,
        }));
        Main.Services.OpenPath(path);
    }

    [RelayCommand]
    private async Task RollbackSessionAsync(SessionItem item)
    {
        if (!await Main.Services.ConfirmAsync("Откат сессии", $"Вернуть изменения сессии {item.Title}?")) return;
        var r = await Task.Run(() => Main.Services.Workflow.RollbackSession(item.Session));
        Main.ShowMessage(r.Summary(), r.Errors.Count > 0);
        Refresh();
    }

    [RelayCommand]
    private async Task RollbackAllAsync()
    {
        if (!await Main.Services.ConfirmAsync("Полный откат", "Вернуть ВСЕ изменения, когда-либо сделанные Opti?")) return;
        var wf = Main.Services.Workflow;
        var r = await Task.Run(() => new RollbackService(wf.BackendsForRollback, Main.Services.Journal).RollbackAll());
        foreach (var s in wf.Store.All().Where(s => s.Outcome != SessionOutcome.RolledBack)) { s.Outcome = SessionOutcome.RolledBack; s.Stage = SessionStage.Closed; wf.Store.Save(s); }
        Main.ShowMessage(r.Summary("Полный откат: восстановлено") + " Перезагрузите компьютер.", r.Errors.Count > 0);
        Refresh();
    }
}
