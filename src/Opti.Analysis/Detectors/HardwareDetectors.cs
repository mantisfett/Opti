using Opti.Core.Findings;
using Opti.Core.Profile;
using Opti.Core.Localization;

namespace Opti.Analysis.Detectors;

public sealed class RamBelowRatedDetector : DetectorBase
{
    public override string Id => "RAM_BELOW_RATED";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        if (p.Device.IsLaptop) yield break; // в ноутбуках XMP обычно недоступен
        var slow = p.Memory.Modules.Where(m => m.RatedSpeed > 0 && m.ConfiguredSpeed > 0 && m.ConfiguredSpeed < m.RatedSpeed * 0.95).ToList();
        if (slow.Count == 0) yield break;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("Память работает ниже своей номинальной частоты"),
            Severity = Severity.Important,
            Explanation = Loc.T("Модули памяти рассчитаны на более высокую частоту, но BIOS запустил их на стандартной. " +
                          "Включение профиля XMP (Intel) или EXPO (AMD) в BIOS обычно даёт прирост минимального FPS. Программа не может сделать это сама."),
            Evidence = slow.Select(m => Loc.T($"{m.Slot ?? "слот ?"} {m.PartNumber?.Trim()}: {m.ConfiguredSpeed} МТ/с из {m.RatedSpeed} МТ/с")).ToList(),
            IsHardwareLimitation = true,
            ManualSteps =
            {
                Loc.T("Перезагрузитесь в BIOS (обычно клавиша Del или F2 при включении)."),
                Loc.T("Найдите пункт XMP / DOCP / EXPO и выберите Profile 1, сохраните (F10)."),
                Loc.T("Если компьютер не стартует — BIOS сам сбросит настройку через несколько попыток, либо сбросьте CMOS."),
            },
            Links = { Sources.IntelXmp },
        };
    }
}

public sealed class RamSingleChannelDetector : DetectorBase
{
    public override string Id => "RAM_SINGLE_CHANNEL";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        bool single = p.Memory.ChannelsPopulated == 1 || (p.Memory.ChannelsPopulated is null && p.Memory.Modules.Count == 1);
        if (!single) yield break;
        bool igpuOnly = p.DiscreteGpu is null;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("Память, вероятно, работает в одноканальном режиме"),
            Severity = igpuOnly ? Severity.Important : Severity.Advice,
            Explanation = Loc.T("Установлен один модуль памяти (или заполнен один канал). Пропускная способность памяти в два раза ниже, чем в двухканальном режиме. ") +
                          (igpuOnly ? Loc.T("Встроенная графика использует оперативную память как видеопамять, поэтому теряет особенно много — до 30–50 % FPS. ")
                                    : Loc.T("Это снижает минимальный FPS в играх, нагружающих процессор. ")) +
                          Loc.T("Решается только установкой второго модуля такого же объёма."),
            Evidence = p.Memory.Modules.Select(m => Loc.T($"{m.Slot ?? "слот ?"}: {m.CapacityMb / 1024.0:0.#} ГБ")).ToList(),
            IsHardwareLimitation = true,
            ManualSteps = { Loc.T("Добавьте второй модуль того же объёма и частоты (в ноутбуке проверьте, есть ли свободный слот).") },
        };
    }
}

public sealed class RamPressureDetector : DetectorBase
{
    public override string Id => "RAM_PRESSURE";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        if (p.Memory.TotalMb <= 0 || p.Memory.AvailablePercent >= 20) yield break;
        var f = new Finding
        {
            Id = Id,
            Title = Loc.T("Мало свободной оперативной памяти уже в простое"),
            Severity = Severity.Important,
            Explanation = Loc.T("Свободно меньше 20 % памяти ещё до запуска игры. Игре придётся вытеснять данные в файл подкачки, а это фризы. " +
                          "Закройте лишние программы (ниже — кто занимает больше всего) или добавьте память. «Очистители ОЗУ» не помогают."),
            Evidence = { Loc.T($"Свободно {p.Memory.AvailableMb / 1024.0:0.0} ГБ из {p.Memory.TotalMb / 1024.0:0.0} ГБ ({p.Memory.AvailablePercent:0} %)") },
        };
        foreach (var c in p.Memory.TopConsumers.Take(8)) f.Evidence.Add(Loc.T($"{c.Name}: {c.WorkingSetMb} МБ"));
        yield return f;
    }
}

public sealed class ThermalThrottleDetector : DetectorBase
{
    public override string Id => "THERMAL_THROTTLE";
    public const double CpuHotC = 95;
    public const double GpuHotC = 87;

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        var t = p.Thermals;
        bool hot = t.CpuTempC >= CpuHotC || t.GpuTempC >= GpuHotC;
        if (t.ThrottleObserved != true && !hot) yield break;
        var f = new Finding
        {
            Id = Id,
            Title = Loc.T("Перегрев и сброс частот"),
            Severity = Severity.Critical,
            Explanation = Loc.T("Процессор или видеокарта нагреваются до предела и снижают частоту, чтобы не сгореть. Отсюда проседания FPS через несколько минут игры. " +
                          "Это аппаратная проблема: программные твики её не решат. Помогает чистка от пыли, замена термопасты, подставка для ноутбука, " +
                          "а в игре — ограничение FPS (меньше нагрев при той же плавности)."),
            IsHardwareLimitation = true,
            ManualSteps =
            {
                Loc.T("Почистите систему охлаждения от пыли (ноутбук — в сервисе, если нет опыта)."),
                Loc.T("Ноутбук: поставьте на твёрдую поверхность или подставку, не закрывайте решётки."),
                Loc.T("Ограничьте FPS в игре или в панели драйвера до частоты экрана."),
            },
        };
        if (t.CpuTempC is { } c) f.Evidence.Add($"CPU: {c:0} °C");
        if (t.GpuTempC is { } g) f.Evidence.Add($"GPU: {g:0} °C");
        if (t.ThrottleObserved == true) f.Evidence.Add(Loc.T("Зафиксировано снижение частоты под нагрузкой"));
        if (p.Cpu.PerformancePercent is { } pp) f.Evidence.Add(Loc.T($"Частота CPU: {pp:0} % от номинала"));
        yield return f;
    }
}

public sealed class DiskFullDetector : DetectorBase
{
    public override string Id => "DISK_FULL";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        if (p.SystemDisk is not { SizeGb: > 0 } d || d.UsedPercent <= 90) yield break;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("Системный диск почти заполнен"),
            Severity = Severity.Important,
            Explanation = Loc.T("Когда на системном диске меньше 10 % свободного места, Windows хуже управляет файлом подкачки и обновлениями, а SSD замедляет запись. " +
                          "Освободите место стандартной «Очисткой диска» и удалите ненужные игры/файлы."),
            Evidence = { Loc.T($"{d.DriveLetter}: свободно {d.FreeGb:0.#} ГБ из {d.SizeGb:0} ГБ ({100 - d.UsedPercent:0.#} %)") },
            ManualSteps = { Loc.T("Откройте раздел «Очистка» в Opti — он бережно удалит временные файлы и кеш."), Loc.T("Проверьте папку «Загрузки» и старые игры.") },
            Links = { Sources.FreeUpSpace },
        };
    }
}

public sealed class DiskHealthDetector : DetectorBase
{
    public override string Id => "DISK_HEALTH";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        var bad = p.Disks.Where(d => d.HealthStatus is { } h && !h.Equals("Healthy", StringComparison.OrdinalIgnoreCase)
                                     || d.WearPercent >= 90).ToList();
        if (bad.Count > 0)
            yield return new Finding
            {
                Id = Id,
                Title = Loc.T("Диск сообщает о проблемах со здоровьем"),
                Severity = Severity.Critical,
                Explanation = Loc.T("Диск сам сообщает о деградации. Это может вызывать зависания и потерю данных. Сделайте резервную копию и планируйте замену."),
                Evidence = bad.Select(d => Loc.T($"{d.Model}: состояние {d.HealthStatus}, износ {d.WearPercent?.ToString() ?? "?"} %")).ToList(),
                IsHardwareLimitation = true,
            };

        if (p.SystemDisk?.MediaType == DiskMediaType.Hdd)
            yield return new Finding
            {
                Id = "DISK_SYSTEM_HDD",
                Title = Loc.T("Windows установлена на жёсткий диск (HDD)"),
                Severity = Severity.Important,
                Explanation = Loc.T("Жёсткий диск в десятки раз медленнее SSD при случайном доступе. Это даёт долгие загрузки и подгрузочные фризы в играх. " +
                              "Программно это не исправить — поможет только SSD."),
                Evidence = { $"{p.SystemDisk.Model}: HDD" },
                IsHardwareLimitation = true,
            };

        var noTrim = p.Disks.Where(d => d.MediaType is DiskMediaType.Ssd or DiskMediaType.Nvme && d.TrimEnabled == false).ToList();
        if (noTrim.Count > 0)
            yield return new Finding
            {
                Id = "DISK_TRIM_OFF",
                Title = Loc.T("TRIM для SSD отключён"),
                Severity = Severity.Advice,
                Explanation = Loc.T("Без TRIM скорость записи SSD со временем падает. Включается командой «fsutil behavior set DisableDeleteNotify 0»."),
                Evidence = noTrim.Select(d => Loc.T($"{d.Model}: TRIM выключен")).ToList(),
                ManualSteps = { Loc.T("Командная строка от администратора: fsutil behavior set DisableDeleteNotify 0") },
            };
    }
}

public sealed class WeakHardwareDetector : DetectorBase
{
    public override string Id => "HW_WEAK";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        var reasons = new List<string>();
        if (p.Cpu.Cores is > 0 and <= 4) reasons.Add(Loc.T($"Процессор: {p.Cpu.Cores} ядер / {p.Cpu.Threads} потоков"));
        if (p.Memory.TotalMb is > 0 and <= 8 * 1024 + 512) reasons.Add(Loc.T($"Оперативная память: {p.Memory.TotalMb / 1024.0:0.#} ГБ"));
        if (reasons.Count == 0) yield break;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("Конфигурация ниже требований большинства современных игр"),
            Severity = Severity.Advice,
            Explanation = Loc.T("Программа исправит то, что исправляется настройками, но часть лагов на этом ПК объясняется железом. " +
                          "Снижение настроек графики и разрешения в игре даст больше, чем любые системные твики."),
            Evidence = reasons,
            IsHardwareLimitation = true,
        };
    }
}
