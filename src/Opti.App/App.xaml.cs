using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Opti.App.Services;
using Opti.Fixes.Backends;
using Opti.ViewModels;

namespace Opti.App;

public partial class App : Application
{
    private WindowsAppServices? _services;

    /// <summary>Папка для снимков интерфейса (Opti.exe --screenshots): демо-данные, система не трогается.</summary>
    public static string? ScreenshotDir { get; set; }
    /// <summary>Opti.exe --demo: настоящий интерфейс на демо-данных, система не меняется.</summary>
    public static bool DemoMode { get; set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (ScreenshotDir is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = await Screenshots.RunAsync(this, ScreenshotDir);
            Shutdown(code);
            return;
        }
        DispatcherUnhandledException += OnUnhandled;
        IAppServices services;
        try
        {
            services = DemoMode
                ? new Opti.ViewModels.Demo.DemoAppServices(Path.Combine(Path.GetTempPath(), "opti-demo-" + Environment.ProcessId))
                : _services = new WindowsAppServices();
        }
        catch (Exception ex)
        {
            Log(ex);
            MessageBox.Show("Не удалось запустить Opti: " + ex.Message, "Opti", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        ApplyBrandTheme();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(ApplyBrandTheme);
        var vm = new MainViewModel(services);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();
        try { await vm.InitializeAsync(); }
        catch (Exception ex) { Log(ex); vm.ShowMessage("Ошибка при запуске: " + ex.Message, error: true); }
    }

    /// <summary>
    /// Акцентный текст и ссылки в цветах бренда с достаточным контрастом: на светлом фоне — тёмно-синий,
    /// на тёмном — светло-бирюзовый. Заливки (кнопки, переключатели) одинаковые в обеих темах.
    /// </summary>
    internal void ApplyBrandTheme() => ApplyBrandTheme(null);

    /// <param name="forceLight">null — по системной теме; true/false — принудительно (снимки интерфейса).</param>
    internal void ApplyBrandTheme(bool? forceLight)
    {
        bool light = true;
        if (forceLight is { } f) light = f;
        else try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = k?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch { /* оставляем светлую */ }
        var text = light ? Color.FromRgb(0x0B, 0x5C, 0xD6) : Color.FromRgb(0x4F, 0xD3, 0xF7);
        Resources["AccentTextFillColorPrimaryBrush"] = new SolidColorBrush(text);
        Resources["AccentTextFillColorSecondaryBrush"] = new SolidColorBrush(text) { Opacity = 0.9 };
        Resources["AccentTextFillColorTertiaryBrush"] = new SolidColorBrush(text) { Opacity = 0.8 };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show("Непредвиденная ошибка: " + e.Exception.Message + "\n\nПодробности записаны в журнал: " + OptiPaths.LogsDir,
            "Opti", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void Log(Exception ex)
    {
        if (Opti.Core.Diagnostics.OptiLog.Directory is null) Opti.Core.Diagnostics.OptiLog.Init(OptiPaths.LogsDir);
        Opti.Core.Diagnostics.OptiLog.Error("Необработанная ошибка", ex);
    }
}
