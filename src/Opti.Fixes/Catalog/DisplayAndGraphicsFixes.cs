using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Fixes.Backends;
using Opti.Core.Localization;

namespace Opti.Fixes.Catalog;

public sealed class DisplayMaxHzFix : FixBase
{
    public override string Id => FixIds.DisplayMaxHz;
    public override string Title => Loc.T("Максимальная частота обновления экрана");
    public override string Explanation => Loc.T("Выставляет максимальную частоту, которую поддерживает экран при текущем разрешении. Разрешение не меняется.");
    public override FixCategory Category => FixCategory.Display;

    public override bool IsApplicable(SystemProfile p) => p.Displays.Count > 0;
    public override bool IsAlreadyApplied(SystemProfile p) => p.Displays.All(d => d.MaxHzAtCurrentResolution - d.CurrentHz < 2);

    protected override void Execute(ChangeContext ctx)
    {
        foreach (var d in ctx.Profile.Displays.Where(d => d.MaxHzAtCurrentResolution - d.CurrentHz >= 2))
            ctx.Set(ChangeType.Display, Targets.DisplayRefresh(d.DeviceName), StateValue.Str(d.MaxHzAtCurrentResolution.ToString()));
    }
}

public sealed class GpuPreferenceDgpuFix : FixBase
{
    public const string Key = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences";
    public override string Id => FixIds.GpuPrefDgpu;
    public override string Title => Loc.T("Привязать игры к дискретной видеокарте");
    public override string Explanation => Loc.T("Для каждой найденной игры задаёт в настройках графики Windows «Высокая производительность». ") +
                                          Loc.T("Игры, для которых вы уже сами выбрали GPU, не трогаются.");
    public override FixCategory Category => FixCategory.GpuAssignment;

    public override bool IsApplicable(SystemProfile p) => p.Hybrid.IsHybrid && p.DiscreteGpu is not null;
    public override bool IsAlreadyApplied(SystemProfile p) => !Unassigned(p).Any();

    public static IEnumerable<string> Unassigned(SystemProfile p) =>
        p.Games.Where(g => g.Executables.All(e => !g.ExeGpuPreferences.ContainsKey(e))).SelectMany(g => g.Executables);

    protected override void Execute(ChangeContext ctx)
    {
        foreach (var exe in Unassigned(ctx.Profile).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var target = Targets.Reg(Key, exe);
            if (ctx.Read(ChangeType.Registry, target).Exists) continue; // выбор пользователя появился после анализа
            ctx.Set(ChangeType.Registry, target, StateValue.Str("GpuPreference=2;"));
        }
    }
}

public sealed class WindowedOptimizationsFix : FixBase
{
    public const string ValueName = "DirectXUserGlobalSettings";
    public override string Id => FixIds.WindowedOptimizations;
    public override string Title => Loc.T("Оптимизация оконных игр и VRR (Windows 11)");
    public override string Explanation => Loc.T("Включает «Оптимизацию для игр в окне» и «Оптимизацию переменной частоты обновления» в настройках графики Windows. ") +
                                          Loc.T("Остальные глобальные настройки DirectX сохраняются.");
    public override FixCategory Category => FixCategory.GameSettings;

    public override bool IsApplicable(SystemProfile p) => p.Os.IsWindows11;
    public override bool IsAlreadyApplied(SystemProfile p) =>
        p.Graphics.WindowedOptimizationsEnabled == true && p.Graphics.VrrOptimizationsEnabled == true;

    protected override void Execute(ChangeContext ctx)
    {
        var target = Targets.Reg(GpuPreferenceDgpuFix.Key, ValueName);
        var current = ctx.Read(ChangeType.Registry, target);
        var merged = Merge(current.Exists ? current.Value : null,
            ("SwapEffectUpgradeEnable", "1"), ("VRROptimizeEnable", "1"));
        ctx.Set(ChangeType.Registry, target, StateValue.Str(merged));
    }

    /// <summary>Слияние строки вида «A=1;B=0;» с сохранением неизвестных ключей и их порядка.</summary>
    public static string Merge(string? existing, params (string Key, string Value)[] set)
    {
        var pairs = new List<(string K, string V)>();
        foreach (var part in (existing ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int i = part.IndexOf('=');
            if (i > 0) pairs.Add((part[..i], part[(i + 1)..]));
        }
        foreach (var (k, v) in set)
        {
            int idx = pairs.FindIndex(p => string.Equals(p.K, k, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) pairs[idx] = (pairs[idx].K, v); else pairs.Add((k, v));
        }
        return string.Concat(pairs.Select(p => $"{p.K}={p.V};"));
    }

    public static Dictionary<string, string> Parse(string? s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int i = part.IndexOf('=');
            if (i > 0) d[part[..i]] = part[(i + 1)..];
        }
        return d;
    }
}

public sealed class GameModeOnFix : FixBase
{
    public override string Id => FixIds.GameModeOn;
    public override string Title => Loc.T("Включить игровой режим Windows");
    public override string Explanation => Loc.T("Игровой режим откладывает обновления и фоновое обслуживание во время игры.");
    public override FixCategory Category => FixCategory.GameSettings;
    public override bool IsAlreadyApplied(SystemProfile p) => p.Graphics.GameModeEnabled != false;
    protected override void Execute(ChangeContext ctx) => SetDword(ctx, @"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1);
}

public sealed class GameDvrOffFix : FixBase
{
    public override string Id => FixIds.GameDvrOff;
    public override FixIntent Intent => FixIntent.Tuning;
    public override string Title => Loc.T("Выключить фоновую запись Xbox Game Bar");
    public override string Explanation => Loc.T("Отключает постоянную фоновую запись игрового процесса (Game DVR). Сам Game Bar и снимки экрана продолжают работать.");
    public override FixCategory Category => FixCategory.GameSettings;
    public override bool IsAlreadyApplied(SystemProfile p) => p.Graphics.GameDvrEnabled != true;
    protected override void Execute(ChangeContext ctx)
    {
        SetDword(ctx, @"HKCU\System\GameConfigStore", "GameDVR_Enabled", 0);
        SetDword(ctx, @"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0);
        SetDword(ctx, @"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", 0);
    }
}

public sealed class MpoResetFix : FixBase
{
    public const string Key = @"HKLM\SOFTWARE\Microsoft\Windows\Dwm";
    public override string Id => FixIds.MpoReset;
    public override string Title => Loc.T("Вернуть стандартную работу MPO");
    public override string Explanation => Loc.T("Удаляет ключ OverlayTestMode, которым когда-то отключали Multiplane Overlay. На Windows 11 24H2+ он вредит режиму Independent Flip.");
    public override FixCategory Category => FixCategory.Display;
    public override bool RequiresReboot => true;
    public override bool IsAlreadyApplied(SystemProfile p) => p.Graphics.OverlayTestMode is null;
    protected override void Execute(ChangeContext ctx) => ctx.Delete(ChangeType.Registry, Targets.Reg(Key, "OverlayTestMode"));
}

/// <summary>
/// HAGS: переключает в противоположное состояние (вариант «B» для A/B-замера).
/// Опция «variant» = on|off задаёт состояние явно.
/// </summary>
public sealed class HagsAbFix : FixBase
{
    public const string Key = @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    public override string Id => FixIds.HagsAb;
    public override FixIntent Intent => FixIntent.Tuning;
    public override string Title => Loc.T("Аппаратное планирование GPU (HAGS): проверить оба варианта");
    public override string Explanation => Loc.T("Переключает HAGS, чтобы сравнить замером. Останется тот вариант, который покажет лучший фреймтайм. Требуется перезагрузка.");
    public override RiskLevel Risk => RiskLevel.Moderate;
    public override bool RequiresReboot => true;
    public override bool RequiresAbTest => true;
    public override FixCategory Category => FixCategory.Experimental;

    public override bool IsApplicable(SystemProfile p) => p.Graphics.HagsSupported == true;
    public override bool IsAlreadyApplied(SystemProfile p) => false;

    protected override void Execute(ChangeContext ctx)
    {
        bool on;
        if (ctx.Options.TryGetValue("variant", out var v)) on = v.Equals("on", StringComparison.OrdinalIgnoreCase);
        else on = ctx.Profile.Graphics.HagsEnabled != true;
        SetDword(ctx, Key, "HwSchMode", on ? 2u : 1u);
    }
}
