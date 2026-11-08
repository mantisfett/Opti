using Opti.Bench;
using Opti.Core.Localization;

namespace Opti.App;

/// <summary>
/// Точка входа. «Opti.exe --bench …» запускает встроенный синтетический тест без окна приложения —
/// так весь Opti остаётся одним портативным файлом.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Язык: --lang ru|en (для снимков и демо), иначе из настроек (auto — по языку Windows)
        int li = Array.IndexOf(args, "--lang");
        Opti.Core.Localization.Loc.SetLanguage(li >= 0 && li + 1 < args.Length ? args[li + 1] : SafeLanguage(args));
        Localizer.Install();
        if (BenchProgram.IsBenchInvocation(args)) return BenchProgram.Run(args);
        // Снимки интерфейса на демо-данных (CI): система не трогается, блокировка экземпляра не нужна
        int shots = Array.IndexOf(args, "--screenshots");
        if (shots >= 0)
        {
            App.ScreenshotDir = Path.GetFullPath(shots + 1 < args.Length ? args[shots + 1] : "screenshots");
            var demo = new App();
            demo.InitializeComponent();
            demo.ThemeMode = System.Windows.ThemeMode.Light;
            return demo.Run();
        }
        // Демо: систему не трогает, поэтому ни блокировка экземпляра, ни папка данных не нужны
        if (args.Contains("--demo", StringComparer.OrdinalIgnoreCase))
        {
            App.DemoMode = true;
            var demo = new App();
            demo.InitializeComponent();
            demo.ThemeMode = System.Windows.ThemeMode.System;
            return demo.Run();
        }
        // Один экземпляр: два процесса, меняющие систему одновременно, могут перепутать исходные значения для отката.
        using var instance = Opti.Workflow.SingleInstance.TryAcquire(TimeSpan.FromSeconds(3));
        if (instance is null)
        {
            System.Windows.MessageBox.Show(Loc.T("Opti уже запущен (окно или командная строка). Закройте его и попробуйте снова."), "Opti",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return 3;
        }
        var app = new App();
        app.InitializeComponent();
        // Тема Fluent по системной (светлая/тёмная, акцентный цвет, тёмный заголовок окна).
        // Задаётся после загрузки ресурсов приложения, чтобы словарь темы не был перезаписан.
        app.ThemeMode = System.Windows.ThemeMode.System;
        return app.Run();
    }

    /// <summary>Язык из settings.json — до создания сервисов; в демо и снимках настройки не читаются.</summary>
    private static string SafeLanguage(string[] args)
    {
        if (args.Contains("--demo", StringComparer.OrdinalIgnoreCase) || args.Contains("--screenshots", StringComparer.OrdinalIgnoreCase)) return "auto";
        try { return Opti.Workflow.OptiSettings.Load().Language; }
        catch { return "auto"; }
    }
}
