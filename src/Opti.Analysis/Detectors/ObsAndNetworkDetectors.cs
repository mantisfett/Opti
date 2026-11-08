using Opti.Core.Findings;
using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Core.Localization;

namespace Opti.Analysis.Detectors;

public sealed class ObsDisplayCaptureHybridDetector : DetectorBase
{
    public override string Id => "OBS_DISPLAY_CAPTURE_HYBRID";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        if (p.Obs is not { Installed: true, UsesDisplayCapture: true } obs || !p.Hybrid.IsHybrid) yield break;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("OBS захватывает экран через другую видеокарту"),
            Severity = Severity.Critical,
            Explanation = Loc.T("На ноутбуке с двумя видеокартами «Захват экрана» в OBS заставляет каждый кадр копироваться между GPU. " +
                          "Это сильно роняет FPS игры и даёт рывки в записи. «Захват игры» берёт кадр прямо из игры и этой проблемы не имеет."),
            Evidence = obs.SceneFilesWithDisplayCapture.Select(f => Loc.T($"Источник «Захват экрана» в коллекции сцен: {Path.GetFileName(f)}")).ToList(),
            FixIds = { FixIds.ObsGameCapture },
            Links = { Sources.ObsLaptops },
        };
    }
}

public sealed class ObsAdminDetector : DetectorBase
{
    public override string Id => "OBS_ADMIN";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        if (p.Obs is not { Installed: true, RunsAsAdmin: true } obs) yield break;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("OBS всегда запускается от администратора"),
            Severity = Severity.Important,
            Explanation = Loc.T("Запуск OBS от администратора повышает приоритет его GPU-задач, и при нагрузке на видеокарту OBS начинает отбирать время у игры. " +
                          "Для захвата игр права администратора в большинстве случаев не нужны."),
            Evidence = { Loc.T($"Флаг совместимости RUNASADMIN для {obs.ExePath}") },
            FixIds = { FixIds.ObsNoAdmin },
        };
    }
}

public sealed class ObsReplayBufferDetector : DetectorBase
{
    public override string Id => "OBS_REPLAY_BUFFER_BIG";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        if (p.Obs is not { Installed: true, ReplayBufferEnabled: true } obs) yield break;
        if (obs.ReplayBufferMaxMb is not > 2048 && obs.ReplayBufferSeconds is not > 300) yield break;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("Очень большой буфер повтора OBS"),
            Severity = Severity.Advice,
            Explanation = Loc.T("Буфер повтора хранится в оперативной памяти. Большой буфер отнимает память у игры. Обычно хватает 30–120 секунд."),
            Evidence = { Loc.T($"Буфер: {obs.ReplayBufferSeconds?.ToString() ?? "?"} с, до {obs.ReplayBufferMaxMb?.ToString() ?? "?"} МБ") },
            ManualSteps = { Loc.T("OBS → Настройки → Вывод → Буфер повтора: уменьшите время и лимит памяти.") },
        };
    }
}

public sealed class OverlayHeavyDetector : DetectorBase
{
    public override string Id => "OVERLAY_HEAVY";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        var o = p.Background.RunningOverlays;
        if (o.Count < 2) yield break;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("Одновременно работает несколько оверлеев"),
            Severity = Severity.Advice,
            Explanation = Loc.T("Каждый оверлей встраивается в вывод кадров игры. Несколько сразу увеличивают задержку и могут сбивать режим Independent Flip. " +
                          "Оставьте один (тот, которым пользуетесь). Программа не меняет настройки чужих программ — ниже инструкции."),
            Evidence = o.Select(x => Loc.T($"Запущен: {x}")).ToList(),
            ManualSteps = o.Select(OverlayHint).Where(h => h is not null).Cast<string>().ToList(),
        };
    }

    private static string? OverlayHint(string name) => name.ToLowerInvariant() switch
    {
        var n when n.Contains("discord") => Loc.T("Discord: Настройки → Игровой оверлей → выключить."),
        var n when n.Contains("steam") => Loc.T("Steam: Настройки → В игре → снять «Включить оверлей Steam в игре»."),
        var n when n.Contains("nvidia") => Loc.T("NVIDIA App: Настройки → Оверлей в игре → выключить (если не нужны запись/фильтры)."),
        var n when n.Contains("overwolf") => Loc.T("Overwolf: Настройки → Оверлей и горячие клавиши → отключите для нужных игр."),
        var n when n.Contains("rtss") || n.Contains("rivatuner") => Loc.T("RTSS: если нужен только лимит FPS — используйте лимитер драйвера или игры."),
        var n when n.Contains("radeon") || n.Contains("amd") => Loc.T("AMD Software: Настройки → Общие → «Оверлей в игре» → выключить."),
        var n when n.Contains("xbox") || n.Contains("gamebar") => Loc.T("Xbox Game Bar: Параметры → Игры → Xbox Game Bar → выключить."),
        _ => null,
    };
}

public sealed class WifiGamingDetector : DetectorBase
{
    public override string Id => "NET_WIFI_GAMING";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        var up = p.Network.Where(n => n.IsUp).ToList();
        var wifi = up.FirstOrDefault(n => n.Kind == NetAdapterKind.WiFi);
        if (wifi is null || up.Any(n => n.Kind == NetAdapterKind.Ethernet)) yield break;
        if (p.Games.Count > 0 && !p.Games.Any(g => g.IsOnline)) yield break;
        var f = new Finding
        {
            Id = Id,
            Title = Loc.T("Онлайн-игры через Wi-Fi"),
            Severity = Severity.Advice,
            Explanation = Loc.T("Wi-Fi даёт нестабильный пинг и потери пакетов из-за помех и соседних сетей. Самое надёжное — кабель. " +
                          "Если кабель невозможен: диапазон 5 или 6 ГГц вместо 2,4 ГГц и роутер в прямой видимости."),
            Evidence = { Loc.T($"Активное подключение: {wifi.Description ?? wifi.Name}") + (wifi.WifiBandGhz is { } b ? Loc.T($", {b:0.#} ГГц") : "") + (wifi.LinkSpeedMbps is { } s ? Loc.T($", {s} Мбит/с") : "") },
            IsHardwareLimitation = true,
            ManualSteps = { Loc.T("Подключите кабель Ethernet, если есть возможность."), Loc.T("Подключитесь к сети 5 ГГц (часто с суффиксом _5G).") },
        };
        if (wifi.AllowComputerToTurnOff == true) f.FixIds.Add(FixIds.NetAdapterPowerOff);
        yield return f;
    }
}

public sealed class NetAdapterPowerSavingDetector : DetectorBase
{
    public override string Id => "NET_ADAPTER_POWER_SAVING";

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        var a = p.Network.Where(n => n.IsUp && n.AllowComputerToTurnOff == true && n.Kind is NetAdapterKind.Ethernet or NetAdapterKind.WiFi).ToList();
        if (a.Count == 0) yield break;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("Сетевой адаптер может отключаться для экономии энергии"),
            Severity = Severity.Advice,
            Explanation = Loc.T("Windows разрешено отключать сетевой адаптер для экономии энергии. На части адаптеров это вызывает задержки при «пробуждении» и обрывы соединения."),
            Evidence = a.Select(n => Loc.T($"{n.Description ?? n.Name}: «Разрешить отключение для экономии энергии» включено")).ToList(),
            FixIds = { FixIds.NetAdapterPowerOff },
        };
    }
}

public sealed class BufferbloatDetector : DetectorBase
{
    public override string Id => "NET_BUFFERBLOAT";
    public const double ThresholdMs = 100;

    protected override IEnumerable<Finding> Run(SystemProfile p, DetectionContext ctx)
    {
        if (p.NetQuality is not { LoadedPingMs: { } loaded } q || loaded - q.IdlePingMs <= ThresholdMs) yield break;
        yield return new Finding
        {
            Id = Id,
            Title = Loc.T("Пинг резко растёт, когда сеть загружена (bufferbloat)"),
            Severity = Severity.Important,
            Explanation = Loc.T("Когда кто-то в сети качает или смотрит видео, роутер копит очередь пакетов, и пинг в игре вырастает в разы. " +
                          "Это решается на роутере: включите SQM / Smart Queue / Adaptive QoS (часто называется «QoS» или «Приоритизация трафика»). " +
                          "Программа на ПК это исправить не может."),
            Evidence = { Loc.T($"Пинг до {q.Target}: в простое {q.IdlePingMs:0} мс, под нагрузкой {loaded:0} мс (+{loaded - q.IdlePingMs:0} мс)") },
            IsHardwareLimitation = true,
            ManualSteps = { Loc.T("В настройках роутера найдите SQM / Smart Queue Management / Adaptive QoS и включите, указав скорость тарифа ~90 %.") },
            Links = { Sources.Bufferbloat },
        };
    }
}
