using Opti.Core.Findings;
using Opti.Core.Json;
using Opti.Core.Profile;
using Opti.Core.Strategy;
using Opti.Fixes.Backends;
using Opti.Measure.Stats;
using Opti.Core.Localization;

namespace Opti.Workflow.Sessions;

public enum SessionStage
{
    Analyzed,
    MeasuredBefore,
    Applied,
    AwaitingReboot,
    VerifiedAfterReboot,
    MeasuredAfter,
    Compared,
    Closed,
}

public enum SessionOutcome { Unknown, Improved, NoEffect, Worse, RolledBack }

public enum MeasureMode { Game, Synthetic, PresentMon }

/// <summary>
/// Сессия оптимизации: всё, что нужно, чтобы продолжить после перезагрузки и построить отчёт.
/// Хранится в C:\ProgramData\Opti\sessions\{id}.json. Идентификатор совпадает с сессией журнала изменений.
/// </summary>
public sealed class TuneSession
{
    public string Id { get; set; } = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public UserGoal Goal { get; set; }
    public SessionStage Stage { get; set; } = SessionStage.Analyzed;
    public SessionOutcome Outcome { get; set; }
    public MeasureMode MeasureMode { get; set; } = MeasureMode.Synthetic;
    /// <summary>Автоматический цикл: тест → исправления → (перезагрузка) → тест → результат без лишних вопросов.</summary>
    public bool AutoMode { get; set; }
    public string? GameProcess { get; set; }
    /// <summary>Автоцикл без замеров: только применить выбранное (результат не доказан замером).</summary>
    public bool SkipMeasure { get; set; }
    /// <summary>Исправления, выбранные перед автоциклом (null — рекомендованные по плану).</summary>
    public List<string>? AutoFixIds { get; set; }
    /// <summary>Число и длительность прогонов, выбранные перед запуском (null — из настроек). «После» повторяет «до».</summary>
    public int? MeasureRuns { get; set; }
    public int? MeasureDurationSec { get; set; }

    public SystemProfile? Profile { get; set; }
    public List<Finding> Findings { get; set; } = new();
    public List<string> SelectedFixIds { get; set; } = new();
    public Dictionary<string, Dictionary<string, string>> FixOptions { get; set; } = new();
    public List<string> AppliedFixIds { get; set; } = new();
    public List<string> FailedFixes { get; set; } = new();
    public DateTimeOffset? AppliedAt { get; set; }
    public bool RebootRequired { get; set; }
    public string? RestorePoint { get; set; }

    public List<MeasurementRun> Before { get; set; } = new();
    public List<MeasurementRun> After { get; set; } = new();
    public List<AbTestRecord> AbTests { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public PostRebootReport? PostReboot { get; set; }
}

/// <summary>Результат A/B-проверки одной спорной настройки.</summary>
public sealed class AbTestRecord
{
    public required string FixId { get; set; }
    public string VariantA { get; set; } = Loc.T("исходно");
    public string VariantB { get; set; } = Loc.T("изменено");
    public AbPhase Phase { get; set; } = AbPhase.MeasureA;
    public List<MeasurementRun> RunsA { get; set; } = new();
    public List<MeasurementRun> RunsB { get; set; } = new();
    public Verdict? Verdict { get; set; }
    public bool KeptB { get; set; }
    public string? Summary { get; set; }
}

public enum AbPhase { MeasureA, ApplyB, AwaitRebootB, MeasureB, Decided }

public sealed class SessionStore
{
    private readonly string _dir;
    public SessionStore(string? dir = null) => _dir = dir ?? OptiPaths.SessionsDir;

    public string PathOf(string id) => Path.Combine(_dir, id + ".json");

    public void Save(TuneSession s)
    {
        Directory.CreateDirectory(_dir);
        var tmp = PathOf(s.Id) + ".tmp";
        File.WriteAllText(tmp, OptiJson.Serialize(s));
        File.Move(tmp, PathOf(s.Id), overwrite: true);
    }

    public TuneSession? Load(string id) =>
        File.Exists(PathOf(id)) ? OptiJson.Deserialize<TuneSession>(File.ReadAllText(PathOf(id))) : null;

    public IReadOnlyList<TuneSession> All()
    {
        if (!Directory.Exists(_dir)) return Array.Empty<TuneSession>();
        var list = new List<TuneSession>();
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try { list.Add(OptiJson.Deserialize<TuneSession>(File.ReadAllText(f))); }
            catch { /* повреждённый файл пропускаем */ }
        }
        return list.OrderByDescending(s => s.CreatedAt).ToList();
    }

    /// <summary>Последняя незакрытая сессия — её продолжает команда resume.</summary>
    public TuneSession? LatestOpen() => All().FirstOrDefault(s => s.Stage != SessionStage.Closed);
}
