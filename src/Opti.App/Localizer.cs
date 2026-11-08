using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Opti.Core.Localization;

namespace Opti.App;

/// <summary>
/// Перевод текстов, написанных прямо в XAML (Text="…", Content="…", Header="…", ToolTip="…"). Значения из привязок
/// не трогаются — их переводят ViewModel'и. В русском режиме ничего не делает.
///
/// WPF не вызывает Loaded у TextBlock и Button, если на них нет своего обработчика, поэтому событие ловится у
/// контейнеров (Grid, панели, страницы), а обход дерева окна делается один раз за такт — каждый элемент переводится один раз.
/// </summary>
internal static class Localizer
{
    private static readonly ConditionalWeakTable<DependencyObject, object> Done = new();
    private static readonly ConditionalWeakTable<Window, object> Hooked = new();

    public static void Install()
    {
        if (!Loc.IsEnglish) return;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Hook((Window)s)));
    }

    /// <summary>
    /// У окна: при каждом изменении раскладки ставится флаг, а таймер (раз в 100 мс) обходит дерево, если флаг стоял.
    /// Так переводятся и страницы, и элементы, созданные позже (списки исправлений, твиков).
    /// </summary>
    private static void Hook(Window w)
    {
        if (Hooked.TryGetValue(w, out _)) return;
        Hooked.Add(w, w);
        bool dirty = true;
        w.LayoutUpdated += (_, _) => dirty = true;
        var timer = new DispatcherTimer(DispatcherPriority.Loaded, w.Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (!dirty) return;
            dirty = false;
            try { Walk(w); }
            catch (Exception ex) { Opti.Core.Diagnostics.OptiLog.Warn("Перевод интерфейса: ошибка обхода", ex); }
        };
        timer.Start();
        w.Closed += (_, _) => timer.Stop();
    }

    /// <summary>Перевести всё дерево (для случаев, когда нужно сразу).</summary>
    public static void TranslateTree(DependencyObject root) => Walk(root);

    private static void Walk(DependencyObject node)
    {
        if (!Done.TryGetValue(node, out _))
        {
            Done.Add(node, node);
            Translate(node);
        }
        int n = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(node, i));
        // Содержимое, которое ещё не стало визуальным (например, закрытые Expander)
        if (node is ContentControl { Content: DependencyObject c }) Walk(c);
    }

    private static void Translate(DependencyObject d)
    {
        switch (d)
        {
            case TextBlock tb:
                if (!IsBound(tb, TextBlock.TextProperty) && tb.Inlines.Count <= 1 && tb.Text is { Length: > 0 } txt) tb.Text = Loc.T(txt);
                foreach (var run in tb.Inlines.OfType<Run>().ToList())
                    if (tb.Inlines.Count > 1 && !IsBound(run, Run.TextProperty) && run.Text is { Length: > 0 } rt) run.Text = Loc.T(rt);
                break;
            case HeaderedContentControl h when h.Header is string hs && !IsBound(h, HeaderedContentControl.HeaderProperty):
                h.Header = Loc.T(hs);
                break;
            case ContentControl c when c.Content is string cs && !IsBound(c, ContentControl.ContentProperty):
                c.Content = Loc.T(cs);
                break;
        }
        if (d is FrameworkElement fe)
        {
            if (fe.ToolTip is string tip && !IsBound(fe, FrameworkElement.ToolTipProperty)) fe.ToolTip = Loc.T(tip);
            if (AutomationProperties.GetName(fe) is { Length: > 0 } an && !IsBound(fe, AutomationProperties.NameProperty))
                AutomationProperties.SetName(fe, Loc.T(an));
        }
    }

    private static bool IsBound(DependencyObject d, DependencyProperty p) => System.Windows.Data.BindingOperations.IsDataBound(d, p);
}
