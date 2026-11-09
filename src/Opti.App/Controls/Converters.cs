using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Opti.App.Controls;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility.Collapsed;
}

/// <summary>Пустая строка или null — скрыть.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Пустая коллекция или null — скрыть.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool any = value switch
        {
            int n => n > 0,
            ICollection c => c.Count > 0,
            IEnumerable e => e.Cast<object>().Any(),
            _ => false,
        };
        // ConverterParameter=invert — показать, когда пусто (пустые состояния)
        if (parameter as string == "invert") any = !any;
        return any ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Смысловой тон из ViewModel («good», «bad», «warn», «accent», «neutral») → кисть системной темы.
/// Если в теме нет нужной кисти, берётся нейтральный цвет, читаемый и на светлом, и на тёмном фоне.
/// </summary>
public sealed class ToneToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, (string Key, string Fallback)> Map = new()
    {
        ["good"] = ("SystemFillColorSuccessBrush", "#2E9E5B"),
        ["bad"] = ("SystemFillColorCriticalBrush", "#D13438"),
        ["warn"] = ("SystemFillColorCautionBrush", "#C27C0E"),
        ["accent"] = ("AccentTextFillColorPrimaryBrush", "#0F6CBD"),
        ["neutral"] = ("TextFillColorTertiaryBrush", "#8A8A8A"),
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var tone = value as string ?? "neutral";
        if (!Map.TryGetValue(tone, out var m)) m = Map["neutral"];
        if (Application.Current?.TryFindResource(m.Key) is System.Windows.Media.Brush b) return b;
        return new System.Windows.Media.BrushConverter().ConvertFromString(m.Fallback)!;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Тот же тон, но полупрозрачной заливкой — фон плашек (тегов) и подсказок.</summary>
public sealed class ToneToSoftBrushConverter : IValueConverter
{
    private static readonly ToneToBrushConverter Solid = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = Solid.Convert(value, targetType, parameter, culture) as System.Windows.Media.SolidColorBrush;
        var color = b?.Color ?? System.Windows.Media.Colors.Gray;
        var soft = new System.Windows.Media.SolidColorBrush(color) { Opacity = 0.16 };
        soft.Freeze();
        return soft;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
