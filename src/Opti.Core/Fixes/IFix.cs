using Opti.Core.Profile;

namespace Opti.Core.Fixes;

public enum RiskLevel { Safe = 0, Moderate = 1, Risky = 2 }

public enum FixCategory
{
    Display = 0,
    GpuAssignment = 1,
    Obs = 2,
    Background = 3,
    Power = 4,
    GameSettings = 5,
    Network = 6,
    Timers = 7,
    Experimental = 8,
    /// <summary>Возврат стандартных значений реестра после «оптимизаторов».</summary>
    Registry = 9,
}

/// <summary>Зачем нужно исправление — от этого зависит, в каких режимах оно отмечено по умолчанию.</summary>
public enum FixIntent
{
    /// <summary>Исправляет явную ошибку или возвращает стандарт Windows. Отмечено во всех режимах.</summary>
    Repair = 0,
    /// <summary>Настройка под производительность (питание, сеть, таймеры). Режимы «Баланс» и «Максимальная».</summary>
    Tuning = 1,
    /// <summary>Меняет привычное поведение (мышь, автозагрузка). По умолчанию — только «Максимальная».</summary>
    Preference = 2,
}

public interface IFix
{
    string Id { get; }
    string Title { get; }
    /// <summary>Простым языком: что и зачем.</summary>
    string Explanation { get; }
    RiskLevel Risk { get; }
    bool RequiresReboot { get; }
    FixCategory Category { get; }
    /// <summary>Исправление ошибки, настройка под производительность или изменение привычек.</summary>
    FixIntent Intent => FixIntent.Repair;

    /// <summary>Эффект спорный — оставлять только по результату A/B-замера.</summary>
    bool RequiresAbTest => false;
    /// <summary>Затрагивает BCD (нужна проверка BitLocker).</summary>
    bool TouchesBcd => false;
    /// <summary>Пользователь должен сам выбрать пункты (например, автозагрузку) — по умолчанию не отмечено.</summary>
    bool NeedsUserChoice => false;
    /// <summary>Порядок внутри одной категории: меньше — раньше.</summary>
    int Priority => 0;

    bool IsApplicable(SystemProfile p);
    bool IsAlreadyApplied(SystemProfile p);
    IReadOnlyList<ChangeRecord> Apply(ChangeContext ctx);
}
