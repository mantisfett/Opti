namespace Opti.Core.Software;

/// <summary>Программа из списка рекомендаций. Ссылка — только на официальный сайт разработчика.</summary>
public sealed record SoftwareEntry(string Name, string Description, string Url, bool Free = true, string? Note = null);

public sealed record SoftwareCategory(string Title, string Icon, IReadOnlyList<SoftwareEntry> Items);

/// <summary>
/// Рекомендуемые программы по назначению. Opti ничего не скачивает и не устанавливает сам —
/// только открывает официальный сайт. Список намеренно короткий: по одной-две проверенные программы на задачу.
/// </summary>
public static class SoftwareCatalog
{
    public static IReadOnlyList<SoftwareCategory> All { get; } = new SoftwareCategory[]
    {
        new("Диск и место", "\uEDA2", new SoftwareEntry[]
        {
            new("WizTree", "Показывает, что занимает место на диске, за секунды.", "https://diskanalyzer.com/", Note: "бесплатно для личного использования"),
            new("CrystalDiskInfo", "Здоровье и износ SSD и HDD (SMART), температура дисков.", "https://crystalmark.info/en/software/crystaldiskinfo/"),
            new("CrystalDiskMark", "Скорость чтения и записи диска.", "https://crystalmark.info/en/software/crystaldiskmark/"),
        }),
        new("Мониторинг и диагностика", "\uE9D9", new SoftwareEntry[]
        {
            new("HWiNFO", "Все датчики: температуры, частоты, троттлинг, напряжения.", "https://www.hwinfo.com/"),
            new("LatencyMon", "Находит драйверы, из-за которых трещит звук и дёргается картинка (DPC/ISR).", "https://www.resplendence.com/latencymon"),
            new("OCCT", "Стресс-тест стабильности CPU, GPU, памяти и блока питания.", "https://www.ocbase.com/"),
            new("CPU-Z", "Модель процессора, платы, частота и тайминги памяти (проверка XMP).", "https://www.cpuid.com/softwares/cpu-z.html"),
            new("GPU-Z", "Видеокарта, версия драйвера, шина PCIe, датчики GPU.", "https://www.techpowerup.com/gpuz/"),
        }),
        new("Драйверы", "\uE950", new SoftwareEntry[]
        {
            new("NVIDIA App", "Драйверы и настройки видеокарт NVIDIA.", "https://www.nvidia.com/en-us/software/nvidia-app/"),
            new("AMD Software: Adrenalin", "Драйверы видеокарт и чипсетов AMD.", "https://www.amd.com/en/support/download/drivers.html"),
            new("Intel Driver & Support Assistant", "Находит и обновляет драйверы Intel: графика, Wi-Fi, Bluetooth, чипсет.", "https://www.intel.com/content/www/us/en/support/detect.html"),
            new("Display Driver Uninstaller (DDU)", "Полностью удаляет видеодрайвер перед чистой установкой — когда обычная переустановка не помогает.",
                "https://www.wagnardsoft.com/", Note: "запускать в безопасном режиме"),
        }),
        new("Общение", "\uE8BD", new SoftwareEntry[]
        {
            new("Discord", "Голосовой чат и сообщества для игр.", "https://discord.com/download"),
            new("Telegram Desktop", "Мессенджер для компьютера.", "https://desktop.telegram.org/"),
        }),
        new("Стрим и запись", "\uE714", new SoftwareEntry[]
        {
            new("OBS Studio", "Стрим и запись экрана. Бесплатно, без водяных знаков.", "https://obsproject.com/"),
            new("ShareX", "Скриншоты и короткие записи экрана с загрузкой в облако.", "https://getsharex.com/"),
        }),
        new("Игры и разгон", "\uE7FC", new SoftwareEntry[]
        {
            new("Steam", "Магазин и библиотека игр Valve.", "https://store.steampowered.com/about/"),
            new("Epic Games Launcher", "Магазин Epic, еженедельные бесплатные игры.", "https://store.epicgames.com/download"),
            new("MSI Afterburner", "Оверлей FPS и температур, кривая вентиляторов и андервольт видеокарты (любого производителя).",
                "https://www.msi.com/Landing/afterburner/graphics-cards"),
        }),
        new("Системные компоненты", "\uE74C", new SoftwareEntry[]
        {
            new("Visual C++ Redistributable", "Библиотеки, без которых многие игры не запускаются (ошибки «MSVCP140.dll»).",
                "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist"),
            new("DirectX End-User Runtime", "Старые компоненты DirectX 9–11 для классических игр (ошибки «d3dx9_43.dll»).",
                "https://www.microsoft.com/en-us/download/details.aspx?id=35"),
        }),
        new("Утилиты", "\uE90F", new SoftwareEntry[]
        {
            new("7-Zip", "Архиватор: zip, 7z, rar и другие форматы.", "https://www.7-zip.org/"),
            new("Everything", "Мгновенный поиск файлов по имени на всех дисках.", "https://www.voidtools.com/"),
            new("PowerToys", "Набор утилит Microsoft: раскладки окон, пипетка, переименование файлов.", "https://learn.microsoft.com/en-us/windows/powertoys/"),
            new("Notepad++", "Удобный редактор текстов и конфигов.", "https://notepad-plus-plus.org/"),
        }),
        new("Браузеры и медиа", "\uE774", new SoftwareEntry[]
        {
            new("Firefox", "Браузер с упором на приватность.", "https://www.mozilla.org/firefox/"),
            new("Google Chrome", "Самый распространённый браузер.", "https://www.google.com/chrome/"),
            new("VLC", "Плеер, который открывает почти любое видео.", "https://www.videolan.org/vlc/"),
        }),
    };
}
