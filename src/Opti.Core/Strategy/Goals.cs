using Opti.Core.Localization;
using Opti.Core.Profile;

namespace Opti.Core.Strategy;

/// <summary>
/// Режим оптимизации — насколько глубоко Opti меняет систему.
/// Значения хранятся в JSON строкой; старые цели 1.0–1.2 (LowLatency, Smoothness, MaxFps, Streaming, QuietCool)
/// читаются через <see cref="UserGoalJsonConverter"/>.
/// </summary>
public enum UserGoal
{
    /// <summary>Исправления ошибок + настройки под производительность (рекомендуемый режим).</summary>
    Balanced = 0,
    /// <summary>Всё, что даёт прирост: питание без ограничений, таймеры, мышь — ценой нагрева и расхода энергии.</summary>
    Maximum = 1,
    /// <summary>Только исправление явных ошибок и возврат стандартных значений Windows.</summary>
    Minimal = 2,
}

public enum DeviceClass
{
    HybridLaptop,
    IgpuLaptop,
    DesktopDgpu,
    DesktopIgpu,
}

public sealed record DeviceClassification(DeviceClass Class, bool IsWeak, bool IsModifiedOs, bool? HasMux)
{
    public static DeviceClassification From(SystemProfile p)
    {
        bool hasDgpu = p.DiscreteGpu is not null;
        DeviceClass cls = p.Device.IsLaptop
            ? (hasDgpu && p.IntegratedGpu is not null ? DeviceClass.HybridLaptop : hasDgpu ? DeviceClass.DesktopDgpu /* ноутбук только с dGPU ведёт себя как ПК */ : DeviceClass.IgpuLaptop)
            : (hasDgpu ? DeviceClass.DesktopDgpu : DeviceClass.DesktopIgpu);

        var sys = p.SystemDisk;
        bool weak = (p.Cpu.Cores > 0 && p.Cpu.Cores <= 4)
                    || (p.Memory.TotalMb > 0 && p.Memory.TotalMb <= 8 * 1024 + 512)
                    || sys?.MediaType == DiskMediaType.Hdd;

        return new DeviceClassification(cls, weak, p.Os.ModifiedBuild is not null, p.Hybrid.HasMux);
    }

    public string Describe()
    {
        var name = Class switch
        {
            DeviceClass.HybridLaptop => Loc.T("Ноутбук с гибридной графикой") + (HasMux == true ? Loc.T(" (есть MUX)") : HasMux == false ? Loc.T(" (без MUX)") : ""),
            DeviceClass.IgpuLaptop => Loc.T("Ноутбук только со встроенной графикой"),
            DeviceClass.DesktopDgpu => Loc.T("Настольный ПК с дискретной видеокартой"),
            DeviceClass.DesktopIgpu => Loc.T("Настольный ПК со встроенной графикой"),
            _ => Class.ToString(),
        };
        if (IsWeak) name += Loc.T(", слабая конфигурация");
        if (IsModifiedOs) name += Loc.T(", модифицированная сборка Windows");
        return name;
    }
}

public static class GoalNames
{
    public static string Ru(this UserGoal g) => g switch
    {
        UserGoal.Maximum => "Максимальная",
        UserGoal.Balanced => "Баланс",
        UserGoal.Minimal => "Минимальная",
        _ => g.ToString(),
    };

    /// <summary>Перевод старых целей (до 1.3) в режимы.</summary>
    public static UserGoal? FromLegacy(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "maximum" or "max" or "lowlatency" or "maxfps" => UserGoal.Maximum,
        "balanced" or "balance" or "smoothness" or "streaming" => UserGoal.Balanced,
        "minimal" or "min" or "quietcool" => UserGoal.Minimal,
        _ => null,
    };
}

/// <summary>Читает и старые цели, и новые режимы; пишет имя режима.</summary>
public sealed class UserGoalJsonConverter : System.Text.Json.Serialization.JsonConverter<UserGoal>
{
    public override UserGoal Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            System.Text.Json.JsonTokenType.String => GoalNames.FromLegacy(reader.GetString()) ?? UserGoal.Balanced,
            System.Text.Json.JsonTokenType.Number when reader.TryGetInt32(out var n) && Enum.IsDefined(typeof(UserGoal), n) => (UserGoal)n,
            _ => UserGoal.Balanced,
        };

    public override void Write(System.Text.Json.Utf8JsonWriter writer, UserGoal value, System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>Описание режима для интерфейса и командной строки: одно место, где сказано, чем режимы отличаются.</summary>
public sealed record GoalInfo(
    UserGoal Goal,
    string Title,
    string Tagline,
    string Summary,
    IReadOnlyList<string> Includes,
    IReadOnlyList<string> Skips,
    string ChangesLevel,
    string HeatLevel,
    string Verification)
{
    public static IReadOnlyList<GoalInfo> All { get; } = new[]
    {
        new GoalInfo(UserGoal.Maximum, "Максимальная", "Вся производительность, которую можно доказать",
            "Исправляет ошибки и снимает все ограничения, которые мешают FPS и отклику. Подходит для игрового ПК, который работает от сети.",
            new[]
            {
                "Всё из режима «Баланс»",
                "Без энергосбережения процессора (Power Throttling)",
                "Без ускорения мыши — точное прицеливание",
                "Таймеры и HAGS — с проверкой A/B-замером",
            },
            new[] { "На ноутбуке выше нагрев и расход батареи" },
            "много", "выше", "Каждое спорное изменение проверяется замером"),
        new GoalInfo(UserGoal.Balanced, "Баланс", "Рекомендуем для большинства",
            "Исправляет ошибки и настраивает питание, сеть и игровые параметры Windows под игры. То, что меняет привычное поведение, не трогает.",
            new[]
            {
                "Всё из режима «Минимальная»",
                "Питание от сети без энергосберегающих ограничений",
                "Без фоновой записи Game Bar и энергосбережения сети",
                "Таймеры и HAGS — с проверкой A/B-замером",
            },
            new[] { "Мышь и энергосбережение процессора не трогает" },
            "средне", "чуть выше", "Спорные изменения проверяются замером"),
        new GoalInfo(UserGoal.Minimal, "Минимальная", "Только исправить ошибки",
            "Исправляет только явные проблемы и возвращает стандартные значения Windows. Ничего не меняет «под производительность».",
            new[]
            {
                "Частота экрана и привязка игр к видеокарте",
                "Игровой режим, OBS, файл подкачки",
                "Стандартные значения после «оптимизаторов»",
            },
            new[] { "Питание, сеть, таймеры и мышь не трогает" },
            "мало", "как было", "Без экспериментов и A/B-замеров"),
    };

    public static GoalInfo Of(UserGoal g) => All.First(i => i.Goal == g);

    /// <summary>
    /// Таблица «чем отличаются режимы». Значение ячейки: "+" — делается, "-" — не делается, иначе — текст.
    /// Должна совпадать с политикой <see cref="StrategyEngine"/> (проверяется тестом).
    /// </summary>
    public static IReadOnlyList<GoalComparisonRow> Comparison { get; } = new[]
    {
        new GoalComparisonRow("Исправление найденных ошибок: частота экрана, видеокарта для игр, OBS, файл подкачки", "+", "+", "+"),
        new GoalComparisonRow("Возврат стандартных значений Windows после «оптимизаторов»", "+", "+", "+"),
        new GoalComparisonRow("Питание от сети без энергосберегающих ограничений", "+", "+", "-"),
        new GoalComparisonRow("Без фоновой записи Game Bar и энергосбережения сетевой карты", "+", "+", "-"),
        new GoalComparisonRow("Таймеры и HAGS — остаются, только если A/B-замер докажет пользу", "+", "+", "-"),
        new GoalComparisonRow("Процессор не экономит энергию в фоне (Power Throttling)", "+", "-", "-"),
        new GoalComparisonRow("Без ускорения мыши", "+", "-", "-"),
        new GoalComparisonRow("Нагрев, шум и расход батареи", "Выше", "Чуть выше", "Как было"),
    };
}

public sealed record GoalComparisonRow(string Feature, string Maximum, string Balanced, string Minimal)
{
    public string For(UserGoal g) => g switch { UserGoal.Maximum => Maximum, UserGoal.Balanced => Balanced, _ => Minimal };
}
