using CommunityToolkit.Mvvm.ComponentModel;
using Opti.Core.Findings;
using Opti.Core.Fixes;
using Opti.Core.Strategy;
using Opti.Measure.Stats;
using Opti.Core.Localization;

namespace Opti.ViewModels;

/// <summary>Карточка режима на главной и в настройках. Тексты берутся из <see cref="GoalInfo"/> — одно описание для интерфейса и CLI.</summary>
public sealed class GoalOption
{
    public required UserGoal Goal { get; init; }
    public required string Title { get; init; }
    /// <summary>Короткая строка под названием.</summary>
    public required string Description { get; init; }
    /// <summary>Что делает режим — 1–2 предложения.</summary>
    public required string Summary { get; init; }
    /// <summary>Глиф шрифта Segoe Fluent Icons.</summary>
    public required string Icon { get; init; }
    public required IReadOnlyList<string> Includes { get; init; }
    public required IReadOnlyList<string> Skips { get; init; }
    public required string ChangesLevel { get; init; }
    public required string HeatLevel { get; init; }
    public required string Verification { get; init; }
    /// <summary>Плашка «Рекомендуем» на карточке.</summary>
    public bool IsRecommended => Goal == UserGoal.Balanced;
    /// <summary>Сколько делений из трёх закрашено в индикаторе «глубина изменений».</summary>
    public int Intensity => Goal switch { UserGoal.Minimal => 1, UserGoal.Balanced => 2, _ => 3 };
    public bool Level2 => Intensity >= 2;
    public bool Level3 => Intensity >= 3;

    private static GoalOption From(UserGoal g, string icon)
    {
        var i = GoalInfo.Of(g);
        return new GoalOption
        {
            Goal = g, Title = Loc.T(i.Title), Description = Loc.T(i.Tagline), Summary = Loc.T(i.Summary), Icon = icon,
            Includes = i.Includes.Select(Loc.T).ToList(), Skips = i.Skips.Select(Loc.T).ToList(),
            ChangesLevel = Loc.T(i.ChangesLevel), HeatLevel = Loc.T(i.HeatLevel), Verification = Loc.T(i.Verification),
        };
    }

    /// <summary>Порядок слева направо: от самого глубокого к самому бережному.</summary>
    public static IReadOnlyList<GoalOption> All => new[]
    {
        From(UserGoal.Maximum, "\uE945"),   // молния
        From(UserGoal.Balanced, "\uE9E9"),  // ползунки
        From(UserGoal.Minimal, "\uE73E"),   // галочка
    };
}

/// <summary>Строка таблицы «чем отличаются режимы» (столбцы в порядке <see cref="GoalOption.All"/>).</summary>
public sealed class ModeComparisonRow
{
    public required string Feature { get; init; }
    public required IReadOnlyList<ComparisonCell> Cells { get; init; }

    public static IReadOnlyList<ModeComparisonRow> All => GoalInfo.Comparison
        .Select(r => new ModeComparisonRow
        {
            Feature = Loc.T(r.Feature),
            Cells = GoalOption.All.Select(o => ComparisonCell.From(Loc.T(r.For(o.Goal)), o.Goal)).ToList(),
        }).ToList();
}

public sealed class ComparisonCell
{
    public required UserGoal Goal { get; init; }
    public string? Text { get; init; }
    public bool IsYes { get; init; }
    public bool IsNo { get; init; }
    /// <summary>Подпись для экранного диктора.</summary>
    public string Spoken => IsYes ? Loc.T("да") : IsNo ? Loc.T("нет") : Text ?? "";

    public static ComparisonCell From(string v, UserGoal g) => v switch
    {
        "+" => new ComparisonCell { Goal = g, IsYes = true },
        "-" => new ComparisonCell { Goal = g, IsNo = true },
        _ => new ComparisonCell { Goal = g, Text = v },
    };
}

public sealed class LinkItem
{
    public required string Text { get; init; }
    public required string Url { get; init; }

    public static LinkItem From(string url) => new()
    {
        Url = url,
        Text = url.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ? Loc.T("Открыть параметры Windows")
             : Uri.TryCreate(url, UriKind.Absolute, out var u) ? Loc.T("Источник: ") + u.Host.Replace("www.", "") : url,
    };
}

public sealed class FindingItem
{
    public FindingItem(Finding f)
    {
        Finding = f.Translated();
        SeverityText = f.Severity switch { Severity.Critical => Loc.T("критично"), Severity.Important => Loc.T("важно"), _ => Loc.T("совет") };
        Color = f.Severity switch { Severity.Critical => Palette.Critical, Severity.Important => Palette.Important, _ => Palette.Advice };
    }
    public Finding Finding { get; }
    public string Title => Finding.Title;
    public string Explanation => Finding.Explanation;
    public string SeverityText { get; }
    public string Color { get; }
    public bool IsHardware => Finding.IsHardwareLimitation;
    public IReadOnlyList<string> Evidence => Finding.Evidence;
    public IReadOnlyList<string> ManualSteps => Finding.ManualSteps;
    public IReadOnlyList<string> Links => Finding.Links;
    public IReadOnlyList<LinkItem> LinkItems => Finding.Links.Select(LinkItem.From).ToList();
    public bool HasDetails => Evidence.Count + ManualSteps.Count + Links.Count > 0;
    public bool HasFix => Finding.FixIds.Count > 0;
    public string FixHint => HasFix ? Loc.T("Можно исправить автоматически") : IsHardware ? Loc.T("Аппаратное ограничение — программно не исправить") : Loc.T("Совет: действие за вами");
}

public sealed partial class FixItem : ObservableObject
{
    public FixItem(PlannedFix p)
    {
        Planned = p;
        _isSelected = p.Selected && !p.ApplySeparately;
    }
    public PlannedFix Planned { get; }
    public IFix Fix => Planned.Fix;
    public string Id => Fix.Id;
    public string Title => Fix.Title;
    public string Explanation => Fix.Explanation;
    public string RiskText => Fix.Risk switch { RiskLevel.Safe => Loc.T("безопасно"), RiskLevel.Moderate => Loc.T("умеренно"), _ => Loc.T("рискованно") };
    public string RiskColor => Fix.Risk switch { RiskLevel.Safe => Palette.Good, RiskLevel.Moderate => Palette.Warn, _ => Palette.Bad };
    public bool RequiresReboot => Fix.RequiresReboot;
    public bool IsAb => Planned.ApplySeparately;
    public string Reason => Loc.T("Причина: ") + string.Join(", ", Planned.TriggeredBy);
    public IReadOnlyList<string> Notes => Planned.Notes;
    public bool NeedsChoice => Fix.NeedsUserChoice;

    [ObservableProperty] private bool _isSelected;
}

public sealed partial class ChoiceItem : ObservableObject
{
    public required string Name { get; init; }
    public string? Detail { get; init; }
    [ObservableProperty] private bool _isChecked;
}

public sealed class LogItem
{
    public required string Title { get; init; }
    public required string Status { get; init; }
    public required string Color { get; init; }
    public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();
}

public sealed class RunSummary
{
    public RunSummary(MeasurementRun r)
    {
        Run = r;
        var m = Metrics.ComputeAll(r.FrameTimesMs);
        AvgFps = m[MetricId.AvgFps];
        Low1 = m[MetricId.Low1Fps];
        P99 = m[MetricId.FrameTimeP99];
    }
    public MeasurementRun Run { get; }
    public string Label => Run.Label;
    public double AvgFps { get; }
    public double Low1 { get; }
    public double P99 { get; }
    public string Text => Loc.T($"{Run.Conditions.ProcessName}: {Run.FrameTimesMs.Count} кадров · {AvgFps:0.#} FPS · 1 % low {Low1:0.#} · p99 {P99:0.##} мс") +
                          (Run.IndependentFlipShare is { } f ? $" · Independent Flip {f * 100:0} %" : "");
    public bool Reliable => Run.IsReliable;
    public string Color => Run.IsReliable ? Palette.Good : Palette.Warn;
    public string Warning => Run.ThermalThrottled ? Loc.T("троттлинг — прогон ненадёжен") : Run.FrameTimesMs.Count < 300 ? Loc.T("мало кадров") : string.Join("; ", Run.Warnings);
}

public sealed class MetricCard
{
    public MetricCard(MetricComparison m)
    {
        Name = m.Metric.Name;
        Before = Fmt(m.Before);
        After = Fmt(m.After);
        Unit = m.Metric.Unit;
        Delta = double.IsNaN(m.DeltaPercent) ? "" : m.DeltaPercent.ToString("+0.#;−0.#;0", Ru) + " %";
        VerdictText = m.Verdict switch { Verdict.Better => Loc.T("лучше, достоверно"), Verdict.Worse => Loc.T("хуже, достоверно"), Verdict.NoChange => Loc.T("в пределах шума"), _ => Loc.T("мало данных") };
        Color = m.Verdict switch { Verdict.Better => Palette.Good, Verdict.Worse => Palette.Bad, _ => Palette.Neutral };
        // Коротко — интервал разности (на нём основан вердикт); подробности — во всплывающей подсказке
        Ci = double.IsNaN(m.DiffCi.Low) ? "" : Loc.T($"разница {Signed(m.DiffCi.Low)}…{Signed(m.DiffCi.High)} {Unit}");
        CiDetails = Loc.T($"Интервал разницы «после − до» — 95 % доверительный (для ключевых метрик 98,3 %). Интервалы замеров: до {F2(m.BeforeCi.Low)}–{F2(m.BeforeCi.High)}, после {F2(m.AfterCi.Low)}–{F2(m.AfterCi.High)} {Unit}. ") +
                    Loc.T($"Разброс между прогонами: {F2(m.RunSpread)}. «Лучше» или «хуже» — только если интервал разницы не содержит ноль и разница больше разброса.");
    }
    private static System.Globalization.CultureInfo Ru => Loc.Culture;
    private static string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString(v >= 100 ? "0" : "0.#", Ru);
    private static string F2(double v) => double.IsNaN(v) ? "—" : v.ToString(Math.Abs(v) >= 100 ? "0" : "0.##", Ru);
    private static string Signed(double v) => v.ToString(Math.Abs(v) >= 100 ? "+0;−0;0" : Math.Abs(v) >= 10 ? "+0.#;−0.#;0" : "+0.##;−0.##;0", Ru);
    public string Name { get; }
    public string Before { get; }
    public string After { get; }
    public string Unit { get; }
    public string Delta { get; }
    public string VerdictText { get; }
    public string Color { get; }
    public string Ci { get; }
    public string CiDetails { get; }
}
