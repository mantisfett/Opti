using Opti.Core.Profile;

namespace Opti.Core.Findings;

public enum Severity { Advice = 0, Important = 1, Critical = 2 }

/// <summary>Результат детектора: найденная проблема с доказательствами.</summary>
public sealed class Finding
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required Severity Severity { get; init; }
    /// <summary>Простое объяснение: что не так и почему это важно.</summary>
    public required string Explanation { get; init; }
    /// <summary>Конкретные данные, на которых основан вывод.</summary>
    public List<string> Evidence { get; init; } = new();
    /// <summary>Исправления, которые программа может применить.</summary>
    public List<string> FixIds { get; init; } = new();
    /// <summary>Действия, которые выполняет пользователь (BIOS, драйвер, чистка…).</summary>
    public List<string> ManualSteps { get; init; } = new();
    public List<string> Links { get; init; } = new();
    /// <summary>Проблема в железе и программно не решается.</summary>
    public bool IsHardwareLimitation { get; init; }
}

public static class FindingLocalization
{
    /// <summary>
    /// Копия находки с текстами на языке интерфейса. Находки сохраняются в сессиях на языке, на котором их нашли,
    /// поэтому перевод делается при показе, а не при создании.
    /// </summary>
    public static Finding Translated(this Finding f) => !Opti.Core.Localization.Loc.IsEnglish ? f : new Finding
    {
        Id = f.Id, Severity = f.Severity, IsHardwareLimitation = f.IsHardwareLimitation,
        Title = Opti.Core.Localization.Loc.T(f.Title), Explanation = Opti.Core.Localization.Loc.T(f.Explanation),
        Evidence = f.Evidence.Select(Opti.Core.Localization.Loc.T).ToList(),
        ManualSteps = f.ManualSteps.Select(Opti.Core.Localization.Loc.T).ToList(),
        FixIds = f.FixIds, Links = f.Links,
    };
}

public sealed class DetectionContext
{
    public required SystemProfile Profile { get; init; }
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
}

public interface IDetector
{
    string Id { get; }
    /// <summary>Возвращает 0..N находок. Не бросает исключений при неполных данных.</summary>
    IEnumerable<Finding> Detect(DetectionContext ctx);
}
