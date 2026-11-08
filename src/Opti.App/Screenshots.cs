using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Opti.ViewModels;
using Opti.ViewModels.Demo;

namespace Opti.App;

/// <summary>
/// Снимки всех страниц на демо-данных: Opti.exe --screenshots &lt;папка&gt;.
/// Запускается в CI на Windows, чтобы интерфейс можно было проверить глазами в каждом PR.
/// Система не трогается: профиль встроенный, настройки пишутся в эмуляцию, замеры синтетические.
/// Для каждой страницы два снимка: окно 1180×780 («view») и вся страница целиком («full»).
/// </summary>
internal static class Screenshots
{
    private const double Scale = 1.5;

    public static async Task<int> RunAsync(App app, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var temp = Path.Combine(Path.GetTempPath(), "opti-shots-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            foreach (var (name, mode, light) in new[] { ("light", ThemeMode.Light, true), ("dark", ThemeMode.Dark, false) })
            {
                app.ThemeMode = mode;
                app.ApplyBrandTheme(light);
                await ShootThemeAsync(app, Path.Combine(temp, name), outDir, name);
            }
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outDir, "error.txt"), ex.ToString());
            return 1;
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* временная папка */ }
        }
    }

    private static async Task ShootThemeAsync(App app, string dataDir, string outDir, string theme)
    {
        var services = new DemoAppServices(dataDir) { IsDemo = false };
        var vm = new MainViewModel(services);
        var window = new MainWindow
        {
            DataContext = vm, Width = 1180, Height = 780, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0,
        };
        window.Show();
        int n = 0;
        async Task Shot(string page) => await CaptureAsync(app, window, outDir, $"{theme}-{++n:00}-{page}");

        await Shot("home");
        await vm.Home.AnalyzeCommand.ExecuteAsync(null);
        await Shot("analysis");

        var s = vm.Session!;
        vm.Strategy.Load(s);
        vm.Navigate(vm.Strategy);
        await Shot("strategy");

        await vm.StartAutoFlowAsync();
        await Shot("autosetup");

        vm.Navigate(MeasureViewModel.ForBefore(vm));
        await Shot("measure");

        // Полный цикл на эмуляции: замер «до» → исправления → замер «после» → сравнение
        var wf = services.Workflow;
        wf.RecordBefore(s, Enumerable.Range(0, 3).Select(i => DemoAppServices.SyntheticRun($"до {i + 1}", after: false, seed: 10 + i)));
        var fixes = wf.BuildPlan(s).Fixes.Where(f => f.Selected && !f.ApplySeparately).Select(f => f.Fix).ToList();
        wf.Apply(s, fixes);
        wf.RecordAfter(s, Enumerable.Range(0, 3).Select(i => DemoAppServices.SyntheticRun($"после {i + 1}", after: true, seed: 20 + i)));
        vm.ShowCompare(CompareViewModel.ForSession(vm, s));
        await Shot("compare");

        // «Страж» в «Истории»: будто обновление Windows вернуло одну настройку
        services.SimulateWindowsRevert();
        await vm.History.VerifyCommand.ExecuteAsync(null);

        foreach (var (page, title) in new (PageViewModel, string)[]
                 {
                     (vm.Drivers, "drivers"), (vm.Tweaks, "tweaks"), (vm.Cleanup, "cleanup"),
                     (vm.Software, "software"), (vm.History, "history"), (vm.SettingsPage, "settings"),
                 })
        {
            vm.Navigate(page);
            await Shot(title);
        }
        window.Close();
    }

    private static async Task CaptureAsync(App app, MainWindow window, string outDir, string name)
    {
        await SettleAsync(window);
        Save(app, window, Path.Combine(outDir, name + "-view.png"));

        // Вся страница: содержимое прокрутки уже размечено на полную высоту — рисуем его целиком
        // (растянуть окно нельзя: Windows ограничивает его высотой экрана)
        var scroll = FindScroll(window);
        if (scroll is { Content: FrameworkElement content } && scroll.ExtentHeight > scroll.ViewportHeight + 1)
            SaveElement(app, content, Path.Combine(outDir, name + "-full.png"), withLayer: true);
    }

    private static async Task SettleAsync(Window window)
    {
        for (int i = 0; i < 3; i++)
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(150);
        }
        window.UpdateLayout();
    }

    /// <summary>Самая большая прокрутка в странице (у страниц один основной ScrollViewer).</summary>
    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        ScrollViewer? best = null;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is ScrollViewer sv && sv.ExtentHeight > 0 && (best is null || sv.ViewportHeight * sv.ViewportWidth > best.ViewportHeight * best.ViewportWidth))
                best = sv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
        return best;
    }

    private static void Save(App app, Window window, string path) =>
        SaveElement(app, (FrameworkElement)window.Content, path, withLayer: false);

    private static void SaveElement(App app, FrameworkElement root, string path, bool withLayer)
    {
        double w = root.ActualWidth, h = root.ActualHeight;
        // Подложка: Mica в снимок не попадает, поэтому рисуем базовый фон темы (и слой страницы для «всей страницы»)
        var bg = app.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White;
        var layer = withLayer ? app.TryFindResource("LayerFillColorDefaultBrush") as Brush : null;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
            if (layer is not null) dc.DrawRectangle(layer, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * Scale), (int)Math.Ceiling(h * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        png.Save(fs);
    }
}
