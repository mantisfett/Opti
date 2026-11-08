using Opti.Core.Findings;
using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Core.Localization;

namespace Opti.Core.Strategy;

public sealed class PlannedFix
{
    public required IFix Fix { get; init; }
    /// <summary>Отмечено по умолчанию (рекомендовано).</summary>
    public bool Selected { get; set; }
    public List<string> TriggeredBy { get; init; } = new();
    public List<string> Notes { get; init; } = new();
    public int Order { get; set; }
    /// <summary>Применять отдельно и проверять A/B-замером.</summary>
    public bool ApplySeparately => Fix.RequiresAbTest || Fix.Risk == RiskLevel.Risky;
}

public sealed record ExcludedFix(string FixId, string Reason, List<string> TriggeredBy);

public sealed class StrategyPlan
{
    public required UserGoal Goal { get; init; }
    public required DeviceClassification Device { get; init; }
    public List<PlannedFix> Fixes { get; init; } = new();
    public List<ExcludedFix> Excluded { get; init; } = new();
    public List<Finding> AdviceOnly { get; init; } = new();
    public bool RequiresReboot => Fixes.Any(f => f.Selected && f.Fix.RequiresReboot);
}

/// <summary>
/// Превращает находки в план. Исправление попадает в план, только если сработал
/// его детектор И оно совместимо с профилем устройства; режим решает, что отмечено по умолчанию.
/// </summary>
public sealed class StrategyEngine
{
    private readonly IReadOnlyDictionary<string, IFix> _catalog;

    public StrategyEngine(IEnumerable<IFix> catalog) =>
        _catalog = catalog.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);

    public StrategyPlan Build(SystemProfile profile, IReadOnlyList<Finding> findings, UserGoal goal)
    {
        var device = DeviceClassification.From(profile);
        var plan = new StrategyPlan { Goal = goal, Device = device };

        // fixId -> находки, которые его предложили
        var triggered = new Dictionary<string, List<Finding>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in findings)
        {
            if (f.FixIds.Count == 0) { plan.AdviceOnly.Add(f); continue; }
            foreach (var id in f.FixIds)
            {
                if (!triggered.TryGetValue(id, out var list)) triggered[id] = list = new();
                list.Add(f);
            }
        }

        foreach (var (fixId, sources) in triggered)
        {
            var sourceIds = sources.Select(s => s.Id).Distinct().ToList();
            if (!_catalog.TryGetValue(fixId, out var fix))
            {
                plan.Excluded.Add(new ExcludedFix(fixId, Loc.T("исправление не реализовано в этой версии"), sourceIds));
                continue;
            }
            bool applicable;
            try { applicable = fix.IsApplicable(profile); }
            catch (Exception ex) { applicable = false; Opti.Core.Diagnostics.OptiLog.Warn($"{fixId}: ошибка проверки применимости", ex); }
            if (!applicable)
            {
                plan.Excluded.Add(new ExcludedFix(fixId, Loc.T("не подходит для этого ПК"), sourceIds));
                continue;
            }
            bool already;
            try { already = fix.IsAlreadyApplied(profile); }
            catch (Exception ex) { already = false; Opti.Core.Diagnostics.OptiLog.Warn($"{fixId}: ошибка проверки «уже применено»", ex); }
            if (already)
            {
                plan.Excluded.Add(new ExcludedFix(fixId,
                    device.IsModifiedOs ? Loc.T("уже применено (вероятно, сборкой Windows)") : Loc.T("уже применено"), sourceIds));
                continue;
            }

            var planned = new PlannedFix { Fix = fix, TriggeredBy = sourceIds };
            planned.Selected = fix.Risk != RiskLevel.Risky;
            if (fix.NeedsUserChoice)
            {
                planned.Selected = false;
                planned.Notes.Add(Loc.T("Выберите пункты вручную — программа изменит только отмеченные."));
            }
            if (fix.RequiresAbTest)
                planned.Notes.Add(Loc.T("Спорная настройка: будет проверена A/B-замером, останется лучший вариант."));
            if (fix.Risk == RiskLevel.Risky)
                planned.Notes.Add(Loc.T("Рискованно: включайте только осознанно."));
            if (fix.TouchesBcd && profile.Security.BitLockerOnSystemDrive == true)
            {
                planned.Selected = false;
                planned.Notes.Add(Loc.T("На системном диске включён BitLocker: изменение BCD может запросить ключ восстановления. Подготовьте ключ или приостановите BitLocker."));
            }
            if (device.IsModifiedOs && fix.Category is FixCategory.Timers or FixCategory.Experimental or FixCategory.Registry
                || device.IsModifiedOs && fix.Id == FixIds.MpoReset)
            {
                planned.Selected = false;
                planned.Notes.Add(Loc.T($"Значение, вероятно, задано сборкой {profile.Os.ModifiedBuild} намеренно — не отмечено по умолчанию."));
            }
            ApplyGoalPreferences(planned, goal, profile);
            plan.Fixes.Add(planned);
        }

        int order = 0;
        foreach (var p in plan.Fixes
                     .OrderBy(p => p.ApplySeparately ? 1 : 0)
                     .ThenBy(p => (int)p.Fix.Risk)
                     .ThenBy(p => CategoryOrder(p.Fix.Category))
                     .ThenBy(p => p.Fix.Priority)
                     .ThenBy(p => p.Fix.Id, StringComparer.Ordinal))
            p.Order = ++order;
        plan.Fixes.Sort((a, b) => a.Order.CompareTo(b.Order));
        return plan;
    }

    /// <summary>
    /// Политика режима. Исправления ошибок отмечены всегда; настройки под производительность — с «Баланса»;
    /// изменения привычного поведения — только в «Максимальной». Неотмеченное остаётся в плане: его можно выбрать вручную.
    /// </summary>
    private static void ApplyGoalPreferences(PlannedFix p, UserGoal goal, SystemProfile profile)
    {
        var intent = p.Fix.Intent;
        if (goal == UserGoal.Minimal && intent != FixIntent.Repair)
        {
            p.Selected = false;
            p.Notes.Add(intent == FixIntent.Tuning
                ? Loc.T("Режим «Минимальная»: это настройка под производительность, а не исправление ошибки — не отмечено.")
                : Loc.T("Режим «Минимальная»: это меняет привычное поведение Windows, а не исправляет ошибку — не отмечено."));
        }
        else if (goal == UserGoal.Balanced && p.Fix.Id == FixIds.MouseAccelOff)
        {
            p.Selected = false;
            p.Notes.Add(Loc.T("Меняет ощущение мыши и на рабочем столе — отмечено по умолчанию только в режиме «Максимальная»."));
        }

        switch (p.Fix.Id)
        {
            case FixIds.GameDvrOff:
                p.Notes.Add(Loc.T("Если вы сохраняете клипы через Xbox Game Bar — снимите галочку."));
                break;
            case FixIds.PowerThrottlingOff:
                if (goal != UserGoal.Maximum)
                {
                    if (p.Selected) p.Notes.Add(Loc.T("Отмечено по умолчанию только в режиме «Максимальная»."));
                    p.Selected = false;
                }
                if (profile.Device.IsLaptop) p.Notes.Add(Loc.T("На ноутбуке повышает нагрев и расход батареи."));
                break;
        }
    }

    /// <summary>Сначала бесплатные по эффекту (экран, GPU, OBS), затем системные.</summary>
    private static int CategoryOrder(FixCategory c) => c switch
    {
        FixCategory.Display => 0,
        FixCategory.GpuAssignment => 1,
        FixCategory.Obs => 2,
        FixCategory.GameSettings => 3,
        FixCategory.Background => 4,
        FixCategory.Power => 5,
        FixCategory.Network => 6,
        FixCategory.Timers => 7,
        _ => 8,
    };
}
