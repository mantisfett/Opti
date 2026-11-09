using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Opti.Fixes.Backends;
using Opti.Native;
using Opti.Core.Localization;

namespace Opti.Workflow.Cleanup;

/// <summary>Корзина всех дисков (Shell API). Удаление из корзины необратимо — об этом предупреждаем.</summary>
[SupportedOSPlatform("windows")]
public sealed class RecycleBinTarget : ICleanupTarget
{
    // В x64 shellapi.h использует выравнивание 8 (размер 24 байта) — Pack не задаём, Opti только для x64.
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBinW(string? root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? root, uint flags);
    private const uint SHERB_NOCONFIRMATION = 1, SHERB_NOPROGRESSUI = 2, SHERB_NOSOUND = 4;

    public string Id => "recycle";
    public string Title => Loc.T("Корзина");
    public string Description => Loc.T("Файлы, которые вы уже удалили. После очистки их не восстановить.");
    public bool DefaultSelected => true;
    public string? Caution => Loc.T("Проверьте, что в корзине нет нужного — восстановить будет нельзя.");
    public string? ManualUri => null;

    public Task<CleanupEstimate> EstimateAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? new CleanupEstimate(info.i64Size, (int)Math.Min(int.MaxValue, info.i64NumItems)) : new CleanupEstimate(0, 0);
    }, ct);

    public async Task<CleanupResult> CleanAsync(CancellationToken ct = default)
    {
        var before = await EstimateAsync(ct);
        var r = new CleanupResult();
        int hr = await Task.Run(() => SHEmptyRecycleBinW(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND), ct);
        // S_OK или «корзина уже пуста» (0x8000FFFF на некоторых системах)
        if (hr == 0 || before.Files == 0) { r.FreedBytes = before.Bytes; r.Deleted = before.Files; }
        else r.Errors.Add(Loc.T($"Корзина: код 0x{hr:X8}"));
        return r;
    }
}

/// <summary>Кеш оптимизации доставки (P2P-раздача обновлений Windows) — через штатную команду PowerShell.</summary>
[SupportedOSPlatform("windows")]
public sealed class DeliveryOptimizationTarget : ICleanupTarget
{
    public const string CacheDir = @"C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache";
    public string Id => "delivery-optimization";
    public string Title => Loc.T("Кеш оптимизации доставки");
    public string Description => Loc.T("Копии обновлений Windows, которые раздаются другим компьютерам. Windows очищает его и сама, но не всегда вовремя.");
    public bool DefaultSelected => true;
    public string? Caution => null;
    public string? ManualUri => null;

    public Task<CleanupEstimate> EstimateAsync(CancellationToken ct = default) =>
        new FolderCleanupTarget(Id, Title, Description, () => new[] { CacheDir }, TimeSpan.Zero).EstimateAsync(ct);

    public async Task<CleanupResult> CleanAsync(CancellationToken ct = default)
    {
        var before = await EstimateAsync(ct);
        var r = new CleanupResult();
        var pr = await Task.Run(() => ProcessRunner.Instance.RunPowerShell("Delete-DeliveryOptimizationCache -Force", TimeSpan.FromMinutes(2)), ct);
        var after = await EstimateAsync(ct);
        r.FreedBytes = Math.Max(0, before.Bytes - after.Bytes);
        r.Deleted = Math.Max(0, before.Files - after.Files);
        if (!pr.Ok) r.Errors.Add(Loc.T("Оптимизация доставки: ") + pr.StdErr.Trim());
        return r;
    }
}

/// <summary>
/// Точки восстановления (теневые копии). Размер — через WMI; удаляет сама Windows в «Защите системы»,
/// потому что это откат системы и решать должен человек.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RestorePointsTarget : ICleanupTarget
{
    public string Id => "restore-points";
    public string Title => Loc.T("Точки восстановления (откаты системы)");
    public string Description => Loc.T("Снимки системы для отката. Нужны, если после обновления или драйвера что-то сломается. ") +
                                 Loc.T("Удалить старые: «Защита системы» → «Настроить» → «Удалить» (последняя точка тоже удалится).");
    public bool DefaultSelected => false;
    public string? Caution => null;
    public string? ManualUri => "SystemPropertiesProtection.exe";
    public string ManualActionText => Loc.T("Защита системы");
    public long? WarnAboveBytes => 15L << 30;
    public string? WarnText => Loc.T("Точки занимают больше 15 ГБ. Если система работает стабильно, старые можно удалить, а лимит в «Настроить» уменьшить до 5–10 %.");

    public Task<CleanupEstimate> EstimateAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        long used = 0; int count = 0;
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT UsedSpace FROM Win32_ShadowStorage");
            foreach (var o in s.Get()) used += Convert.ToInt64(o["UsedSpace"]);
            using var c = new System.Management.ManagementObjectSearcher("SELECT ID FROM Win32_ShadowCopy");
            count = c.Get().Count;
        }
        catch { /* служба теневого копирования недоступна */ }
        return new CleanupEstimate(used, count);
    }, ct);

    public Task<CleanupResult> CleanAsync(CancellationToken ct = default) => Task.FromResult(new CleanupResult());
}

public static class CleanupCatalog
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string SystemRoot => Path.GetPathRoot(Win) ?? @"C:\";

    /// <summary>Папка «Загрузки» (может быть перенесена пользователем на другой диск).</summary>
    [SupportedOSPlatform("windows")]
    public static string DownloadsDir
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                if (k?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string p && !string.IsNullOrWhiteSpace(p))
                    return Environment.ExpandEnvironmentVariables(p);
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    /// <summary>Подпапки в корне (C:\NVIDIA\*, C:\AMD\*): сам корень не трогаем.</summary>
    private static IEnumerable<string> SubDirs(string dir)
    {
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> Under(string dir, Func<string, bool> nameFilter) =>
        SubDirs(dir).Where(d => nameFilter(Path.GetFileName(d)));

    private static readonly string[] LogExtensions = { ".log", ".cab", ".etl", ".old" };

    /// <summary>Подпапки профилей браузеров на основе Chromium: …\User Data\Default|Profile N\{Cache,Code Cache,GPUCache}.</summary>
    private static IEnumerable<string> ChromiumCaches(string userData)
    {
        if (!Directory.Exists(userData)) yield break;
        foreach (var profile in Directory.EnumerateDirectories(userData).Where(d => Path.GetFileName(d) is "Default" || Path.GetFileName(d).StartsWith("Profile ", StringComparison.Ordinal)))
            foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
                yield return Path.Combine(profile, sub);
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<ICleanupTarget> ForWindows() => new ICleanupTarget[]
    {
        new RecycleBinTarget(),
        new FolderCleanupTarget("user-temp", Loc.T("Временные файлы"), Loc.T("Папка TEMP вашего профиля: остатки установщиков и программ. Удаляются только файлы старше двух дней."),
            () => new[] { Path.GetTempPath() }, TimeSpan.FromDays(2)),
        new FolderCleanupTarget("windows-temp", Loc.T("Временные файлы Windows"), Loc.T("Системная папка Windows\\Temp. Только файлы старше двух дней."),
            () => new[] { Path.Combine(Win, "Temp") }, TimeSpan.FromDays(2)),
        new FolderCleanupTarget("wu-download", Loc.T("Загруженные обновления Windows"), Loc.T("Установочные файлы уже установленных обновлений (старше 10 дней). При необходимости Windows скачает их снова."),
            () => new[] { Path.Combine(Win, "SoftwareDistribution", "Download") }, TimeSpan.FromDays(10)),
        new DeliveryOptimizationTarget(),
        new FolderCleanupTarget("wer", Loc.T("Отчёты об ошибках"), Loc.T("Отчёты Windows о сбоях программ, которые уже отправлены или не нужны."),
            () => new[] { Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue"), Path.Combine(Local, @"Microsoft\Windows\WER") },
            TimeSpan.FromDays(1)),
        new FolderCleanupTarget("opti-old", Loc.T("Старые отчёты Opti"), Loc.T("HTML-отчёты и журналы Opti старше 30 дней (журнал отката не затрагивается)."),
            () => new[] { OptiPaths.ReportsDir, OptiPaths.LogsDir }, TimeSpan.FromDays(30)),
        new FolderCleanupTarget("thumbnails", Loc.T("Кеш миниатюр"), Loc.T("Картинки-превью в Проводнике. Будут созданы заново при открытии папок."),
            () => new[] { Path.Combine(Local, @"Microsoft\Windows\Explorer") }, TimeSpan.FromDays(1), defaultSelected: false,
            fileFilter: f => Path.GetFileName(f).StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase), recursive: false),
        new FolderCleanupTarget("shader-cache", Loc.T("Кеш шейдеров (DirectX, NVIDIA, AMD)"), Loc.T("Скомпилированные шейдеры игр. Помогает, если после обновления драйвера игры стали фризить."),
            () => new[] { Path.Combine(Local, "D3DSCache"), Path.Combine(Local, @"NVIDIA\DXCache"), Path.Combine(Local, @"NVIDIA\GLCache"), Path.Combine(Local, @"AMD\DxCache"), Path.Combine(Local, @"AMD\DxcCache"), Path.Combine(Local, @"AMD\VkCache") },
            TimeSpan.FromDays(1), defaultSelected: false, caution: Loc.T("Первый запуск каждой игры после очистки будет с подтормаживаниями, пока кеш не наполнится заново.")),
        new FolderCleanupTarget("browser-cache", Loc.T("Кеш браузеров"), Loc.T("Только кеш Chrome, Edge, Brave, Яндекс, Opera, Vivaldi и Firefox: пароли, история, вкладки и расширения не затрагиваются."),
            () => ChromiumCaches(Path.Combine(Local, @"Google\Chrome\User Data"))
                .Concat(ChromiumCaches(Path.Combine(Local, @"Microsoft\Edge\User Data")))
                .Concat(ChromiumCaches(Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data")))
                .Concat(ChromiumCaches(Path.Combine(Local, @"Yandex\YandexBrowser\User Data")))
                .Concat(ChromiumCaches(Path.Combine(Local, @"Vivaldi\User Data")))
                .Concat(new[] { Path.Combine(Local, @"Opera Software\Opera Stable\Cache"), Path.Combine(Local, @"Opera Software\Opera GX Stable\Cache") })
                .Concat(Directory.Exists(Path.Combine(Local, @"Mozilla\Firefox\Profiles"))
                    ? Directory.EnumerateDirectories(Path.Combine(Local, @"Mozilla\Firefox\Profiles")).Select(p => Path.Combine(p, "cache2")) : Array.Empty<string>()),
            TimeSpan.FromHours(1), defaultSelected: false, caution: Loc.T("Закройте браузеры перед очисткой — открытые файлы будут пропущены. Сайты первый раз загрузятся чуть медленнее.")),
        new FolderCleanupTarget("dumps", Loc.T("Дампы памяти"), Loc.T("Файлы, которые Windows и программы создают при сбоях и синих экранах."),
            () => new[] { Path.Combine(Win, "Minidump"), Path.Combine(Win, "LiveKernelReports"), Path.Combine(Local, "CrashDumps") }, TimeSpan.FromDays(7), defaultSelected: false,
            caution: Loc.T("Нужны для диагностики синих экранов. Удаляйте, только если сбоев больше нет.")),
        new FolderCleanupTarget("driver-installers", Loc.T("Распакованные установщики драйверов"), Loc.T("Папки C:\\NVIDIA и C:\\AMD: туда драйверы распаковываются перед установкой и остаются навсегда."),
            () => SubDirs(Path.Combine(SystemRoot, "NVIDIA")).Concat(SubDirs(Path.Combine(SystemRoot, "AMD"))), TimeSpan.FromDays(7)),
        new FolderCleanupTarget("windows-logs", Loc.T("Журналы обслуживания Windows"), Loc.T("Старые журналы установки обновлений и компонентов (CBS, DISM) старше 30 дней."),
            () => new[] { Path.Combine(Win, @"Logs\CBS"), Path.Combine(Win, @"Logs\DISM"), Path.Combine(Win, @"Logs\MoSetup") }, TimeSpan.FromDays(30),
            fileFilter: f => LogExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)),
        new FolderCleanupTarget("app-caches", Loc.T("Кеш Discord, Steam, Epic, Spotify, Teams"), Loc.T("Временный кеш интерфейса программ: картинки, веб-страницы, видео. Аккаунты, чаты и игры не затрагиваются."),
            () => new[] { "Cache", "Code Cache", "GPUCache" }.Select(c => Path.Combine(Roaming, "discord", c))
                .Concat(new[]
                {
                    Path.Combine(Local, @"Steam\htmlcache"), Path.Combine(Local, @"Spotify\Data"),
                    Path.Combine(Roaming, @"Microsoft\Teams\Cache"), Path.Combine(Roaming, @"Microsoft\Teams\Service Worker\CacheStorage"),
                })
                .Concat(Under(Path.Combine(Local, @"EpicGamesLauncher\Saved"), n => n.StartsWith("webcache", StringComparison.OrdinalIgnoreCase))),
            TimeSpan.FromHours(6), defaultSelected: false, caution: Loc.T("Закройте эти программы перед очисткой — открытые файлы будут пропущены.")),
        new ManualCleanupTarget("downloads", Loc.T("Папка «Загрузки»"), Loc.T("Установщики, архивы и файлы, скачанные из интернета. Opti её не очищает — разберите вручную, отсортировав по размеру."),
            () => new[] { DownloadsDir }, () => DownloadsDir, warnAboveBytes: 5L << 30,
            warnText: Loc.T("«Загрузки» весят больше 5 ГБ. Обычно там старые установщики и архивы — их можно удалить, нужное переложите в «Документы».")),
        new LargeFilesTarget("large-videos", Loc.T("Тяжёлые видео (больше 1 ГБ)"), Loc.T("Записи игр (OBS, NVIDIA, Xbox Game Bar) и фильмы в «Видео», «Загрузках» и на рабочем столе. Ниже — самые большие; откройте и удалите ненужные."),
            () => new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), DownloadsDir,
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            }, 1L << 30),
        new RestorePointsTarget(),
        new ManualCleanupTarget("windows-old", Loc.T("Предыдущая установка Windows (Windows.old)"), Loc.T("Остаётся после обновления или переустановки с сохранением файлов. Удаляет сама Windows: «Параметры» → «Память» → «Временные файлы»."),
            () => new[] { Path.Combine(Path.GetPathRoot(Win)!, "Windows.old") }, "ms-settings:storagesense"),
    };
}
