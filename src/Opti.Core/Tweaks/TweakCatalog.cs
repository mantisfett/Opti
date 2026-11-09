namespace Opti.Core.Tweaks;

public enum TweakCategory { ContextMenu, Explorer, StartAndTaskbar, Responsiveness, System, Privacy }

/// <summary>Что нужно, чтобы твик вступил в силу.</summary>
public enum TweakApplyMode { Immediate, RestartExplorer, SignOut, Reboot }

/// <summary>Значение реестра во включённом состоянии твика. Kind: DWord или String.</summary>
public sealed record TweakValue(string Key, string Name, string Kind, string On);

/// <summary>
/// Твик удобства: заметное изменение поведения Windows, которое человек включает сам.
/// Каждый — через журнал изменений: выключение возвращает ровно то значение, что было до Opti.
/// </summary>
public sealed class Tweak
{
    public required string Id { get; init; }
    public required TweakCategory Category { get; init; }
    public required string Title { get; init; }
    /// <summary>Что изменится — одной-двумя фразами, без обещаний «ускорения на 30 %».</summary>
    public required string Description { get; init; }
    public required IReadOnlyList<TweakValue> Values { get; init; }
    public TweakApplyMode ApplyMode { get; init; } = TweakApplyMode.Immediate;
    /// <summary>Минимальная сборка Windows (22000 — Windows 11).</summary>
    public int MinBuild { get; init; }
    public bool Recommended { get; init; }
    public string? Link { get; init; }

    public string FixId => "TWEAK_" + Id;
}

public static class TweakCatalog
{
    private const string Adv = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Cdm = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    public static string CategoryTitle(TweakCategory c) => c switch
    {
        TweakCategory.ContextMenu => "Контекстное меню",
        TweakCategory.Explorer => "Проводник",
        TweakCategory.StartAndTaskbar => "Пуск и панель задач",
        TweakCategory.Responsiveness => "Отзывчивость",
        TweakCategory.Privacy => "Приватность",
        _ => "Система",
    };

    public static string CategoryIcon(TweakCategory c) => c switch
    {
        TweakCategory.ContextMenu => "",
        TweakCategory.Explorer => "",
        TweakCategory.StartAndTaskbar => "",
        TweakCategory.Responsiveness => "",
        TweakCategory.Privacy => "\uE72E",
        _ => "",
    };

    public static string ApplyModeText(TweakApplyMode m) => m switch
    {
        TweakApplyMode.RestartExplorer => "перезапуск Проводника",
        TweakApplyMode.SignOut => "после повторного входа",
        TweakApplyMode.Reboot => "после перезагрузки",
        _ => "сразу",
    };

    public static IReadOnlyList<Tweak> All { get; } = new Tweak[]
    {
        // ---------- Контекстное меню ----------
        new()
        {
            Id = "CLASSIC_CONTEXT_MENU", Category = TweakCategory.ContextMenu, MinBuild = 22000, Recommended = true,
            Title = "Полное контекстное меню, как в Windows 10",
            Description = "Правый клик сразу показывает все пункты (7-Zip, «Открыть с помощью», команды программ) — без лишнего «Показать дополнительные параметры».",
            Values = new[] { new TweakValue(@"HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "String", "") },
            ApplyMode = TweakApplyMode.RestartExplorer,
        },
        new()
        {
            Id = "FAST_MENUS", Category = TweakCategory.ContextMenu, Recommended = true,
            Title = "Быстрое открытие подменю",
            Description = "Подменю (например, «Отправить» или «Создать») открываются через 100 мс вместо 400 мс.",
            Values = new[] { new TweakValue(@"HKCU\Control Panel\Desktop", "MenuShowDelay", "String", "100") },
            ApplyMode = TweakApplyMode.SignOut,
        },

        // ---------- Проводник ----------
        new()
        {
            Id = "SHOW_EXTENSIONS", Category = TweakCategory.Explorer, Recommended = true,
            Title = "Показывать расширения файлов",
            Description = "Видно «setup.exe» и «фото.jpg.exe» — проще отличить программу от документа и заметить подделку.",
            Values = new[] { new TweakValue(Adv, "HideFileExt", "DWord", "0") },
            ApplyMode = TweakApplyMode.RestartExplorer,
        },
        new()
        {
            Id = "EXPLORER_THIS_PC", Category = TweakCategory.Explorer,
            Title = "Проводник открывается на «Этот компьютер»",
            Description = "Сразу видны диски и свободное место вместо списка недавних файлов.",
            Values = new[] { new TweakValue(Adv, "LaunchTo", "DWord", "1") },
            ApplyMode = TweakApplyMode.RestartExplorer,
        },

        // ---------- Пуск и панель задач ----------
        new()
        {
            Id = "NO_WEB_SEARCH", Category = TweakCategory.StartAndTaskbar, Recommended = true,
            Title = "Поиск в «Пуске» без интернета (Bing)",
            Description = "Поиск ищет только программы, настройки и файлы на компьютере — быстрее и без веб-результатов.",
            Values = new[] { new TweakValue(@"HKCU\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", "DWord", "1") },
            ApplyMode = TweakApplyMode.RestartExplorer,
            Link = "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-search",
        },
        new()
        {
            Id = "TASKBAR_END_TASK", Category = TweakCategory.StartAndTaskbar, MinBuild = 22631, Recommended = true,
            Title = "«Завершить задачу» на панели задач",
            Description = "Правый клик по зависшей игре или программе на панели задач → «Завершить задачу». Без Диспетчера задач.",
            Values = new[] { new TweakValue(Adv + @"\TaskbarDeveloperSettings", "TaskbarEndTask", "DWord", "1") },
        },
        new()
        {
            Id = "NO_SUGGESTED_APPS", Category = TweakCategory.StartAndTaskbar, Recommended = true,
            Title = "Без рекламы и автоустановки приложений",
            Description = "Windows перестаёт сама ставить «рекомендованные» игры и программы и показывать советы-рекламу в «Пуске» и на экране блокировки.",
            Values = new[]
            {
                new TweakValue(Cdm, "SilentInstalledAppsEnabled", "DWord", "0"),
                new TweakValue(Cdm, "SystemPaneSuggestionsEnabled", "DWord", "0"),
                new TweakValue(Cdm, "SubscribedContent-338388Enabled", "DWord", "0"),
                new TweakValue(Cdm, "SubscribedContent-338389Enabled", "DWord", "0"),
                new TweakValue(Cdm, "SubscribedContent-310093Enabled", "DWord", "0"),
            },
        },
        new()
        {
            Id = "TASKBAR_LEFT", Category = TweakCategory.StartAndTaskbar, MinBuild = 22000,
            Title = "Значки панели задач слева",
            Description = "Кнопка «Пуск» и значки — в левом углу, как в Windows 10.",
            Values = new[] { new TweakValue(Adv, "TaskbarAl", "DWord", "0") },
        },

        // ---------- Отзывчивость ----------
        new()
        {
            Id = "INSTANT_MINIMIZE", Category = TweakCategory.Responsiveness,
            Title = "Мгновенное сворачивание окон",
            Description = "Окна сворачиваются и разворачиваются без анимации — переключение между игрой и рабочим столом ощущается быстрее.",
            Values = new[] { new TweakValue(@"HKCU\Control Panel\Desktop\WindowMetrics", "MinAnimate", "String", "0") },
            ApplyMode = TweakApplyMode.SignOut,
        },
        new()
        {
            Id = "NO_STARTUP_DELAY", Category = TweakCategory.Responsiveness,
            Title = "Автозагрузка без искусственной паузы",
            Description = "Windows не ждёт ~10 секунд перед запуском программ из автозагрузки. На медленном HDD с большой автозагрузкой лучше не включать.",
            Values = new[] { new TweakValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", "DWord", "0") },
            ApplyMode = TweakApplyMode.SignOut,
        },

        // ---------- Система ----------
        new()
        {
            Id = "NO_FAST_STARTUP", Category = TweakCategory.System,
            Title = "Отключить «Быстрый запуск»",
            Description = "Выключение становится настоящим: драйверы и ядро загружаются заново. Лечит «после выключения всё равно глючит, помогает только перезагрузка». Включение ПК — на пару секунд дольше.",
            Values = new[] { new TweakValue(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", "DWord", "0") },
            ApplyMode = TweakApplyMode.Reboot,
            Link = "https://learn.microsoft.com/en-us/troubleshoot/windows-client/setup-upgrade-and-drivers/disable-and-re-enable-hibernation",
        },

        // ---------- Приватность ----------
        new()
        {
            Id = "NO_ADVERTISING_ID", Category = TweakCategory.Privacy, Recommended = true,
            Title = "Выключить рекламный идентификатор",
            Description = "Приложения перестают получать общий рекламный ID для персонализации рекламы. Программы продолжают работать как раньше.",
            Values = new[] { new TweakValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", "DWord", "0") },
            ApplyMode = TweakApplyMode.Immediate,
        },
        new()
        {
            Id = "NO_TAILORED_EXPERIENCES", Category = TweakCategory.Privacy, Recommended = true,
            Title = "Без «персонализированных советов» из диагностических данных",
            Description = "Windows не использует данные о работе устройства, чтобы подбирать советы и рекламу.",
            Values = new[] { new TweakValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", "DWord", "0") },
            ApplyMode = TweakApplyMode.Immediate,
        },
        new()
        {
            Id = "NO_ACTIVITY_HISTORY", Category = TweakCategory.Privacy, Recommended = true,
            Title = "Не вести и не отправлять журнал действий",
            Description = "Отключает «Журнал действий» (хронологию запущенных приложений и открытых файлов) и его отправку в Microsoft.",
            Values = new[]
            {
                new TweakValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", "DWord", "0"),
                new TweakValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", "DWord", "0"),
            },
            ApplyMode = TweakApplyMode.Reboot,
        },
        new()
        {
            Id = "MIN_DIAGNOSTICS", Category = TweakCategory.Privacy,
            Title = "Минимум диагностических данных",
            Description = "Windows отправляет только обязательные диагностические данные (на Домашней и Pro это нижний уровень). Обновления и защита работают как прежде.",
            Values = new[] { new TweakValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", "DWord", "0") },
            ApplyMode = TweakApplyMode.Reboot,
            Link = "https://learn.microsoft.com/en-us/windows/privacy/configure-windows-diagnostic-data-in-your-organization",
        },
        new()
        {
            Id = "NO_FEEDBACK_PROMPTS", Category = TweakCategory.Privacy,
            Title = "Без запросов отзывов о Windows",
            Description = "Windows перестаёт просить оценить систему и оставить отзыв.",
            Values = new[] { new TweakValue(@"HKCU\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", "DWord", "0") },
            ApplyMode = TweakApplyMode.Immediate,
        },
        new()
        {
            Id = "NO_ONLINE_SPEECH", Category = TweakCategory.Privacy,
            Title = "Выключить онлайн-распознавание речи",
            Description = "Голосовой ввод и диктовка перестают отправлять запись в облако для распознавания.",
            Values = new[] { new TweakValue(@"HKCU\Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", "DWord", "0") },
            ApplyMode = TweakApplyMode.Immediate,
        },
        new()
        {
            Id = "NO_COPILOT", Category = TweakCategory.Privacy, MinBuild = 22621,
            Title = "Отключить Windows Copilot",
            Description = "Политика убирает кнопку и панель Copilot. На новых сборках Copilot — отдельное приложение, его можно удалить в «Параметры» → «Приложения».",
            Values = new[] { new TweakValue(@"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", "DWord", "1") },
            ApplyMode = TweakApplyMode.RestartExplorer,
        },
        new()
        {
            Id = "NO_RECALL", Category = TweakCategory.Privacy, MinBuild = 26100,
            Title = "Отключить сохранение снимков экрана (Recall)",
            Description = "Политика запрещает Windows сохранять снимки экрана и анализировать их ИИ. Уже сохранённое удаляется в параметрах Windows.",
            Values = new[] { new TweakValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", "DWord", "1") },
            ApplyMode = TweakApplyMode.Reboot,
        },
        new()
        {
            Id = "NO_WIDGETS", Category = TweakCategory.Privacy, MinBuild = 22000,
            Title = "Отключить «Виджеты»",
            Description = "Панель виджетов и новостей не запускается и не тратит память и трафик в фоне.",
            Values = new[] { new TweakValue(@"HKLM\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", "DWord", "0") },
            ApplyMode = TweakApplyMode.RestartExplorer,
        },
    };

    public static Tweak? Find(string id) => All.FirstOrDefault(t => t.Id == id);
}
