using Opti.Core.Findings;
using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Core.Localization;

namespace Opti.Analysis.Detectors;

public sealed class DisplayLowHzDetector : DetectorBase
{
    public override string Id => "DISP_LOW_HZ";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        // допуск 2 Гц: 59/60, 143/144 — это один и тот же режим с разным округлением
        var low = p.Displays.Where(d => d.CurrentHz > 0 && d.MaxHzAtCurrentResolution - d.CurrentHz >= 2).ToList();
        if (low.Count == 0) yield break;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("Экран работает не на максимальной частоте"),
            Severity = Severity.Critical,
            Explanation = Loc.T("Монитор умеет показывать больше кадров в секунду, чем сейчас выставлено в Windows. " +
                          "Сколько бы FPS ни выдавала игра, вы видите не больше, чем частота экрана. Это самое дешёвое и заметное исправление."),
            Evidence = low.Select(d => Loc.T($"{d.FriendlyName ?? d.DeviceName}: {d.Width}×{d.Height}, сейчас {d.CurrentHz} Гц, доступно {d.MaxHzAtCurrentResolution} Гц")).ToList(),
            FixIds = { FixIds.DisplayMaxHz },
            Links = { Sources.RefreshRate },
        };
    }
}

public sealed class HybridGameOnIgpuDetector : DetectorBase
{
    public override string Id => "HYBRID_GAME_ON_IGPU";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        if (!p.Hybrid.IsHybrid || p.DiscreteGpu is null) yield break;

        var unassigned = p.Games.Where(g => g.Executables.Count > 0 &&
                                            g.Executables.All(e => !g.ExeGpuPreferences.ContainsKey(e))).ToList();
        var userPowerSaving = p.Games.Where(g => g.ExeGpuPreferences.Values.Any(v => v == 1)).ToList();
        if (unassigned.Count == 0 && userPowerSaving.Count == 0) yield break;

        var f = new Finding
        {
            Id = Id,
            Title = Loc.T("Игры могут запускаться на встроенной видеокарте"),
            Severity = unassigned.Count > 0 ? Severity.Critical : Severity.Advice,
            Explanation = Loc.T("В ноутбуке две видеокарты. Если Windows не знает, что игра «тяжёлая», она может запустить её на слабой встроенной " +
                          $"графике вместо {p.DiscreteGpu.Name}. Явная привязка в настройках графики Windows убирает эту лотерею."),
        };
        foreach (var g in unassigned.Take(15)) f.Evidence.Add(Loc.T($"{g.Name} ({g.Launcher}): GPU не назначен"));
        if (unassigned.Count > 15) f.Evidence.Add(Loc.T($"…и ещё {unassigned.Count - 15}"));
        foreach (var g in userPowerSaving)
            f.Evidence.Add(Loc.T($"{g.Name}: вручную выбрано «Энергосбережение» — программа это не трогает"));
        if (unassigned.Count > 0) f.FixIds.Add(FixIds.GpuPrefDgpu);
        yield return f;
    }
}

public sealed class HybridNoMuxInfoDetector : DetectorBase
{
    public override string Id => "HYBRID_NO_MUX_INFO";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        if (!p.Hybrid.IsHybrid || p.Hybrid.InternalDisplayOnIntegrated != true) yield break;
        var f = new Finding
        {
            Id = Id,
            Title = Loc.T("Встроенный экран подключён к встроенной графике"),
            Severity = Severity.Advice,
            Explanation = Loc.T("Кадры с дискретной видеокарты копируются через встроенную графику (Optimus / AMD Hybrid). " +
                          "Это добавляет немного задержки и снижает FPS, особенно в лёгких играх с высоким FPS. " +
                          "Если в ноутбуке есть MUX-переключатель, режим «только дискретная GPU» убирает эту прослойку. " +
                          "Внешний монитор, подключённый к порту дискретной видеокарты, тоже обходит её."),
            IsHardwareLimitation = p.Hybrid.HasMux == false,
            ManualSteps =
            {
                Loc.T("Найдите в фирменной утилите ноутбука (Armoury Crate, Legion Vantage, Omen Gaming Hub, MSI Center и т. п.) режим «Ultimate / dGPU / Discrete»."),
                Loc.T("Если такого режима нет, проверьте BIOS: пункт «Graphics mode» / «Display mode»."),
                Loc.T("Переключение MUX требует перезагрузки и увеличивает расход батареи."),
            },
        };
        if (p.Hybrid.DxDiagHybridInfo is { } info) f.Evidence.Add($"dxdiag: {info}");
        var internalDisplay = p.Displays.FirstOrDefault(d => d.IsInternal == true);
        if (internalDisplay?.AdapterName is { } an) f.Evidence.Add(Loc.T($"Встроенный экран выводится через: {an}"));
        yield return f;
    }
}

public sealed class DriverOldDetector : DetectorBase
{
    public override string Id => "DRIVER_OLD";
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(548); // ~18 месяцев

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        var old = p.Gpus.Where(g => !g.IsSoftware && g.DriverDate is { } d && ctx.Now - d > MaxAge).ToList();
        if (old.Count == 0) yield break;
        var f = new Finding
        {
            Id = Id,
            Title = Loc.T("Устаревший драйвер видеокарты"),
            Severity = Severity.Important,
            Explanation = Loc.T("Драйверу больше полутора лет. В новых версиях исправляют ошибки, фризы и улучшают производительность в новых играх. " +
                          "Скачивайте драйвер только с официального сайта производителя (для ноутбуков иногда — с сайта производителя ноутбука)."),
            Evidence = old.Select(g => Loc.T($"{g.Name}: версия {g.DriverVersion}, от {g.DriverDate:yyyy-MM-dd}")).ToList(),
            ManualSteps = { Loc.T("Скачайте драйвер по ссылке ниже, установите и перезагрузите компьютер.") },
        };
        foreach (var vendor in old.Select(g => g.Vendor).Distinct())
        {
            var link = vendor switch
            {
                GpuVendor.Nvidia => "https://www.nvidia.com/Download/index.aspx",
                GpuVendor.Amd => "https://www.amd.com/en/support/download/drivers.html",
                GpuVendor.Intel => "https://www.intel.com/content/www/us/en/support/detect.html",
                _ => null,
            };
            if (link is not null) f.Links.Add(link);
        }
        yield return f;
    }
}
