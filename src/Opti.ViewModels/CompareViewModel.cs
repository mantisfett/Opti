using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opti.Measure.Stats;
using Opti.Workflow;
using Opti.Workflow.Reports;
using Opti.Workflow.Sessions;
using Opti.Core.Localization;

namespace Opti.ViewModels;

public sealed partial class AppliedFixItem : ObservableObject
{
    public required string FixId { get; init; }
    public required string Title { get; init; }
    public required string Changes { get; init; }
    [ObservableProperty] private bool _rolledBack;
    public string StatusText => RolledBack ? Loc.T("откачено") : Loc.T("действует");
    partial void OnRolledBackChanged(bool value) => OnPropertyChanged(nameof(StatusText));
}

public sealed partial class CompareViewModel : PageViewModel
{
    private readonly TuneSession _session;
    private readonly AbTestRecord? _ab;
    private readonly CompareOutcome? _outcome;

    private CompareViewModel(MainViewModel main, TuneSession s, ComparisonResult result, string message, Recommendation rec, AbTestRecord? ab, CompareOutcome? outcome) : base(main)
    {
        _session = s;
        _ab = ab;
        _outcome = outcome;
        Result = result;
        Message = message;
        Recommendation = rec;
        MessageColor = result.Overall switch { Verdict.Better => Palette.Good, Verdict.Worse => Palette.Bad, _ => Palette.Neutral };
        foreach (var m in result.Metrics) Cards.Add(new MetricCard(m));
        foreach (var w in result.Warnings) Warnings.Add(w);
        var before = ab is null ? s.Before : ab.RunsA;
        var after = ab is null ? s.After : ab.RunsB;
        BeforeFrames = HtmlReport.Representative(before)?.FrameTimesMs ?? new List<double>();
        AfterFrames = HtmlReport.Representative(after)?.FrameTimesMs ?? new List<double>();
        BeforeLabel = ab is null ? Loc.T("До") : "A: " + ab.VariantA;
        AfterLabel = ab is null ? Loc.T("После") : "B: " + ab.VariantB;
        double? Flip(IEnumerable<MeasurementRun> runs) { var v = runs.Where(r => r.IndependentFlipShare is not null).Select(r => r.IndependentFlipShare!.Value).ToList(); return v.Count > 0 ? v.Average() : null; }
        if (Flip(before) is { } fb && Flip(after) is { } fa) FlipText = $"Independent Flip: {fb * 100:0} % → {fa * 100:0} %";
        LoadFixes();
    }

    public static CompareViewModel ForSession(MainViewModel m, TuneSession s, CompareOutcome? outcome = null)
    {
        outcome ??= s.Before.Count > 0 && s.After.Count > 0 ? m.Services.Workflow.Compare(s) : null;
        var result = outcome?.Result ?? new ComparisonResult();
        return new CompareViewModel(m, s, result, outcome?.Message ?? Loc.T("Нет данных для сравнения."), outcome?.Recommendation ?? Recommendation.NeedMoreData, null, outcome);
    }

    public static CompareViewModel ForAb(MainViewModel m, TuneSession s, AbTestRecord ab, ComparisonResult result) =>
        new(m, s, result, ab.Summary ?? "", ab.KeptB ? Recommendation.Keep : Recommendation.MarkNoEffect, ab, null);

    public override string Title => _ab is null ? Loc.T("Сравнение «до / после»") : $"A/B: {_ab.VariantB}";
    public ComparisonResult Result { get; }
    public ObservableCollection<MetricCard> Cards { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<AppliedFixItem> AppliedFixes { get; } = new();
    public IReadOnlyList<double> BeforeFrames { get; }
    public IReadOnlyList<double> AfterFrames { get; }
    public string BeforeLabel { get; }
    public string AfterLabel { get; }
    public string? FlipText { get; }
    public string Message { get; }
    public string MessageColor { get; }
    public Recommendation Recommendation { get; }
    public bool SuggestRollback => Recommendation == Recommendation.SuggestRollback && _session.Outcome != SessionOutcome.RolledBack;
    public bool IsAb => _ab is not null;

    private void LoadFixes()
    {
        AppliedFixes.Clear();
        var records = Main.Services.Journal.GetAll().Where(r => r.SessionId.StartsWith(_session.Id, StringComparison.Ordinal)).ToList();
        foreach (var g in records.GroupBy(r => r.FixId))
        {
            AppliedFixes.Add(new AppliedFixItem
            {
                FixId = g.Key,
                Title = Main.Services.Workflow.Catalog.FirstOrDefault(f => f.Id == g.Key)?.Title ?? g.Key,
                Changes = string.Join("\n", g.Select(r => $"{r.Type} {r.Target}: {r.Before} → {r.After}")),
                RolledBack = g.All(r => r.RolledBack),
            });
        }
    }

    [RelayCommand]
    private async Task RollbackFixAsync(AppliedFixItem item)
    {
        if (!await Main.Services.ConfirmAsync(Loc.T("Откатить исправление?"), item.Title)) return;
        var r = await Task.Run(() => Main.Services.Workflow.RollbackFix(_session, item.FixId));
        item.RolledBack = true;
        Main.ShowMessage(r.Errors.Count == 0
            ? Loc.T($"«{item.Title}» откачено.") + (r.ChangedSinceOpti.Count > 0 ? Loc.T($" Настройки, изменённые после Opti ({r.ChangedSinceOpti.Count}), оставлены как есть.") : "")
            : Loc.T("Ошибки отката: ") + string.Join("; ", r.Errors), r.Errors.Count > 0);
    }

    [RelayCommand]
    private async Task RollbackSessionAsync()
    {
        if (!await Main.Services.ConfirmAsync(Loc.T("Откат сессии"), Loc.T("Вернуть все изменения этой сессии к исходным значениям?"))) return;
        var r = await Task.Run(() => Main.Services.Workflow.RollbackSession(_session));
        LoadFixes();
        OnPropertyChanged(nameof(SuggestRollback));
        Main.ShowMessage(r.Summary() + Loc.T(" Некоторые настройки вернутся после перезагрузки."), r.Errors.Count > 0);
    }

    [RelayCommand]
    private void ExportHtml()
    {
        Directory.CreateDirectory(Main.Services.ReportsDir);
        var path = Path.Combine(Main.Services.ReportsDir, $"report-{_session.Id}.html");
        File.WriteAllText(path, HtmlReport.Build(new ReportData
        {
            Session = _session,
            Comparison = _ab is null ? Result : (_session.Before.Count > 0 && _session.After.Count > 0 ? new Comparer().Compare(_session.Before, _session.After) : null),
            CompareMessage = _ab is null ? Message : null,
            Changes = Main.Services.Journal.GetAll().Where(r => r.SessionId.StartsWith(_session.Id, StringComparison.Ordinal)).ToList(),
            Catalog = Main.Services.Workflow.Catalog,
            AppVersion = Main.Services.AppVersion,
        }));
        Main.Services.OpenPath(path);
    }

    [RelayCommand]
    private void ExportCsv()
    {
        Directory.CreateDirectory(Main.Services.ReportsDir);
        var path = Path.Combine(Main.Services.ReportsDir, $"frames-{_session.Id}{(_ab is null ? "" : "-ab-" + _ab.FixId)}.csv");
        using (var w = new StreamWriter(path))
        {
            w.WriteLine("set,run,frame,frametime_ms");
            var sets = _ab is null ? new[] { ("before", _session.Before), ("after", _session.After) } : new[] { ("A", _ab.RunsA), ("B", _ab.RunsB) };
            foreach (var (set, runs) in sets)
                for (int r = 0; r < runs.Count; r++)
                    for (int f = 0; f < runs[r].FrameTimesMs.Count; f++)
                        w.WriteLine(FormattableString.Invariant($"{set},{r + 1},{f},{runs[r].FrameTimesMs[f]:0.###}"));
        }
        Main.ShowMessage(Loc.T("CSV сохранён: ") + path);
    }

    [RelayCommand]
    private void Finish()
    {
        if (_ab is null) Main.Services.Workflow.Close(_session);
        Main.Session = _session;
        Main.History.Refresh();
        Main.Navigate(Main.History);
    }
}
