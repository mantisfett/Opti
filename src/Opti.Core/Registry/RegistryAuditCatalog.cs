using Opti.Core.Findings;

namespace Opti.Core.Registry;

public enum RegistryRuleGroup
{
    /// <summary>Остатки «твиков» производительности, которые вредят или не дают эффекта.</summary>
    Performance,
    /// <summary>Отключённые обновления, защита и службы — риск для безопасности и драйверов.</summary>
    UpdatesAndSecurity,
}

/// <summary>
/// Правило аудита реестра: значение, которое «оптимизаторы» и твик-паки часто оставляют в нестандартном
/// состоянии, и стандартное значение Windows, к которому его можно вернуть (с откатом).
/// Opti не «чистит» реестр и не добавляет недокументированных твиков: только находит отклонения от
/// стандарта, которые известны как вредные, и предлагает вернуть стандарт.
/// </summary>
public sealed class RegistryRule
{
    public required string Id { get; init; }
    public required RegistryRuleGroup Group { get; init; }
    /// <summary>Путь ключа: «HKLM\…» или «HKCU\…».</summary>
    public required string Key { get; init; }
    public required string ValueName { get; init; }
    public required string Title { get; init; }
    public required string Explanation { get; init; }
    public Severity Severity { get; init; } = Severity.Advice;
    /// <summary>Значение отклоняется от стандарта и стоит его вернуть. Аргумент — значение строкой (DWORD — десятичным), null — значения нет.</summary>
    public required Func<string?, bool> IsBad { get; init; }
    /// <summary>Стандартное значение DWORD; null — стандарт = отсутствие значения (удаляем).</summary>
    public uint? DefaultDword { get; init; }
    public bool RequiresReboot { get; init; } = true;
    public string? Link { get; init; }

    public string DefaultText => DefaultDword is { } d ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : "нет значения";
}

public static class RegistryAuditCatalog
{
    private const string Mm = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string Gd = @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string Svc = @"HKLM\SYSTEM\CurrentControlSet\Services\";
    private const string WuPolicy = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string DefPolicy = @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender";

    private static bool Is(string? v, params string[] bad) => v is not null && bad.Contains(v.Trim());
    private static bool Above(string? v, long limit) => long.TryParse(v, out var n) && n > limit;

    public const string TdrDocs = "https://learn.microsoft.com/en-us/windows-hardware/drivers/display/tdr-registry-keys";
    public const string SpectreDocs = "https://support.microsoft.com/en-us/topic/kb4072698-windows-server-and-azure-stack-hci-guidance-to-protect-against-silicon-based-microarchitectural-and-speculative-execution-side-channel-vulnerabilities-2f965763-00e2-8f98-b632-0d96f30c8c8e";
    public const string WuPolicyDocs = "https://learn.microsoft.com/en-us/windows/deployment/update/waas-wu-settings";
    public const string DefenderDocs = "https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-antivirus-windows";

    public static IReadOnlyList<RegistryRule> All { get; } = new RegistryRule[]
    {
        // ---------- Производительность ----------
        new()
        {
            Id = "LARGE_SYSTEM_CACHE", Group = RegistryRuleGroup.Performance, Key = Mm, ValueName = "LargeSystemCache",
            Title = "Включён режим «большого системного кеша» (LargeSystemCache)",
            Explanation = "Режим для файловых серверов: Windows отдаёт память под кеш файлов в ущерб программам и играм. На игровом и домашнем ПК это " +
                          "приводит к нехватке памяти и подтормаживаниям. Популярный «твик», который вредит.",
            Severity = Severity.Important, IsBad = v => Is(v, "1"), DefaultDword = 0,
        },
        new()
        {
            Id = "DISABLE_PAGING_EXECUTIVE", Group = RegistryRuleGroup.Performance, Key = Mm, ValueName = "DisablePagingExecutive",
            Title = "Запрещена выгрузка ядра из памяти (DisablePagingExecutive)",
            Explanation = "Старый «твик» времён Windows XP. На современных системах прироста не даёт, но держит в памяти лишнее при нехватке ОЗУ.",
            IsBad = v => Is(v, "1"), DefaultDword = 0,
        },
        new()
        {
            Id = "CLEAR_PAGEFILE_AT_SHUTDOWN", Group = RegistryRuleGroup.Performance, Key = Mm, ValueName = "ClearPageFileAtShutdown",
            Title = "Файл подкачки очищается при каждом выключении",
            Explanation = "Windows перезаписывает весь файл подкачки нулями при выключении — выключение и перезагрузка идут на минуты дольше. " +
                          "Нужно только на компьютерах с особыми требованиями безопасности; если это не ваш случай — верните стандарт.",
            IsBad = v => Is(v, "1"), DefaultDword = 0,
        },
        new()
        {
            Id = "WIN32_PRIORITY_SEPARATION", Group = RegistryRuleGroup.Performance, Key = @"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl", ValueName = "Win32PrioritySeparation",
            Title = "Изменено распределение процессорного времени (Win32PrioritySeparation)",
            Explanation = "Значение изменено «оптимизатором». Независимых замеров с выигрышем нет, а неудачные значения делают фоновые программы " +
                          "(Discord, OBS, браузер со стримом) рывковыми. Стандарт Windows для ПК — 2. Opti проверяет результат замером.",
            IsBad = v => v is not null && !Is(v, "2", "38"), DefaultDword = 2, RequiresReboot = false,
        },
        new()
        {
            Id = "TDR_DISABLED", Group = RegistryRuleGroup.Performance, Key = Gd, ValueName = "TdrLevel",
            Title = "Отключено восстановление видеодрайвера (TDR)",
            Explanation = "Если видеодрайвер зависнет, Windows больше не сможет его перезапустить: вместо короткого чёрного экрана будет полное зависание " +
                          "компьютера. Этот «твик» иногда советуют против вылетов, но он лишь прячет проблему.",
            Severity = Severity.Important, IsBad = v => Is(v, "0"), DefaultDword = null, Link = TdrDocs,
        },
        new()
        {
            Id = "TDR_DELAY_HUGE", Group = RegistryRuleGroup.Performance, Key = Gd, ValueName = "TdrDelay",
            Title = "Слишком большой тайм-аут видеодрайвера (TdrDelay)",
            Explanation = "При зависании видеокарты Windows ждёт больше минуты, прежде чем восстановить драйвер, — всё это время компьютер не отвечает. " +
                          "Небольшое увеличение (до 10–60 с) иногда нужно для рендера; большие значения — нет.",
            IsBad = v => Above(v, 60), DefaultDword = null, Link = TdrDocs,
        },
        new()
        {
            Id = "SYSMAIN_DISABLED", Group = RegistryRuleGroup.Performance, Key = Svc + "SysMain", ValueName = "Start",
            Title = "Отключена служба SysMain (SuperFetch)",
            Explanation = "SysMain заранее подгружает часто используемые программы в память. «Оптимизаторы» часто её отключают; на HDD и при 8–16 ГБ " +
                          "памяти без неё заметно дольше запускаются программы и игры.",
            IsBad = v => Is(v, "4"), DefaultDword = 2,
        },
        new()
        {
            Id = "PREFETCH_DISABLED", Group = RegistryRuleGroup.Performance, Key = Mm + @"\PrefetchParameters", ValueName = "EnablePrefetcher",
            Title = "Отключена предзагрузка (Prefetch)",
            Explanation = "Prefetch ускоряет загрузку Windows и запуск программ. Отключать его на SSD не нужно — это миф времён первых SSD.",
            IsBad = v => Is(v, "0"), DefaultDword = 3,
        },
        new()
        {
            Id = "SPECTRE_MITIGATIONS_OFF", Group = RegistryRuleGroup.UpdatesAndSecurity, Key = Mm, ValueName = "FeatureSettingsOverride",
            Title = "Отключены защиты процессора от Spectre/Meltdown",
            Explanation = "Защиты отключены ради нескольких процентов производительности. Компьютер становится уязвим для атак через браузер и программы. " +
                          "Если вы не отключали их осознанно — верните стандарт.",
            IsBad = v => Is(v, "3"), DefaultDword = null, Link = SpectreDocs,
        },

        // ---------- Обновления и защита ----------
        new()
        {
            Id = "WU_DISABLED_POLICY", Group = RegistryRuleGroup.UpdatesAndSecurity, Key = WuPolicy + @"\AU", ValueName = "NoAutoUpdate",
            Title = "Обновления Windows отключены политикой",
            Explanation = "Компьютер не получает исправления безопасности и обновления драйверов. Часто остаётся после «оптимизаторов» и сборок. " +
                          "Если обновления мешают, их лучше приостановить в параметрах — это обратимо и безопасно.",
            Severity = Severity.Important, IsBad = v => Is(v, "1"), DefaultDword = null, RequiresReboot = false, Link = WuPolicyDocs,
        },
        new()
        {
            Id = "WU_DRIVERS_EXCLUDED", Group = RegistryRuleGroup.UpdatesAndSecurity, Key = WuPolicy, ValueName = "ExcludeWUDriversInQualityUpdate",
            Title = "Драйверы исключены из Центра обновления",
            Explanation = "Windows не предлагает драйверы чипсета, сети, звука. Если вы ставите их вручную с сайтов производителей — это нормально; " +
                          "иначе устройства остаются на старых или стандартных драйверах.",
            IsBad = v => Is(v, "1"), DefaultDword = null, RequiresReboot = false, Link = WuPolicyDocs,
        },
        new()
        {
            Id = "WUAUSERV_DISABLED", Group = RegistryRuleGroup.UpdatesAndSecurity, Key = Svc + "wuauserv", ValueName = "Start",
            Title = "Отключена служба Центра обновления Windows",
            Explanation = "Без неё не устанавливаются обновления, драйверы из Центра обновления и некоторые компоненты (например, .NET и игровые сервисы Xbox).",
            Severity = Severity.Important, IsBad = v => Is(v, "4"), DefaultDword = 3,
        },
        new()
        {
            Id = "BITS_DISABLED", Group = RegistryRuleGroup.UpdatesAndSecurity, Key = Svc + "BITS", ValueName = "Start",
            Title = "Отключена служба фоновой передачи (BITS)",
            Explanation = "BITS скачивает обновления Windows, Microsoft Store и Xbox-игры. Без неё загрузки «зависают» на 0 %.",
            IsBad = v => Is(v, "4"), DefaultDword = 3,
        },
        new()
        {
            Id = "AUDIO_SERVICE_DISABLED", Group = RegistryRuleGroup.UpdatesAndSecurity, Key = Svc + "Audiosrv", ValueName = "Start",
            Title = "Отключена служба звука Windows",
            Explanation = "Без службы Audiosrv нет звука ни в играх, ни в Discord.",
            Severity = Severity.Important, IsBad = v => Is(v, "4"), DefaultDword = 2,
        },
        new()
        {
            Id = "DEFENDER_DISABLED_POLICY", Group = RegistryRuleGroup.UpdatesAndSecurity, Key = DefPolicy, ValueName = "DisableAntiSpyware",
            Title = "Microsoft Defender отключён политикой",
            Explanation = "Встроенный антивирус выключен через реестр. Если у вас нет другого антивируса, компьютер не защищён. Влияние Defender на FPS " +
                          "в играх минимально; при необходимости добавьте папку с играми в исключения вместо полного отключения.",
            IsBad = v => Is(v, "1"), DefaultDword = null, RequiresReboot = true, Link = DefenderDocs,
        },
        new()
        {
            Id = "DEFENDER_REALTIME_OFF_POLICY", Group = RegistryRuleGroup.UpdatesAndSecurity, Key = DefPolicy + @"\Real-Time Protection", ValueName = "DisableRealtimeMonitoring",
            Title = "Защита Defender в реальном времени отключена политикой",
            Explanation = "Файлы не проверяются при открытии и скачивании. Обычно остаётся после «оптимизаторов» и модифицированных сборок.",
            IsBad = v => Is(v, "1"), DefaultDword = null, RequiresReboot = true, Link = DefenderDocs,
        },
    };

    public static RegistryRule? Find(string id) => All.FirstOrDefault(r => r.Id == id);

    /// <summary>Правила, нарушенные в профиле.</summary>
    public static IEnumerable<RegistryRule> Violations(IReadOnlyDictionary<string, string?> audit, RegistryRuleGroup? group = null) =>
        All.Where(r => (group is null || r.Group == group) && audit.TryGetValue(r.Id, out var v) && r.IsBad(v));
}
