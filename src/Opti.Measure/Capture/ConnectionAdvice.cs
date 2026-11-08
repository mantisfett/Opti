using Opti.Core.Localization;

namespace Opti.Measure.Capture;

/// <summary>Вывод простым языком по результатам проверки регионов.</summary>
public static class ConnectionAdvice
{
    /// <summary>Вывод простым языком: где ближе всего и есть ли нестабильность.</summary>
    public static string Describe(IReadOnlyList<ProbeResult> ok)
    {
        if (ok.Count == 0) return Loc.T("Ни один регион не ответил — проверьте подключение к интернету.");
        var best = ok[0];
        var text = Loc.T($"Ближе всего: {Loc.T(best.Target.Name)} — {best.MedianMs:0} мс.");
        if (best.LossPercent >= 2) text += " " + Loc.T($"Потери пакетов {best.LossPercent:0} % — связь нестабильна (Wi-Fi, кабель или провайдер).");
        else if (best.JitterMs >= 15) text += " " + Loc.T($"Большой разброс задержки ({best.JitterMs:0} мс) — в играх это рывки; проверьте Wi-Fi и загрузку канала.");
        else if (best.MedianMs >= 60) text += " " + Loc.T("До ближайшего региона далеко — задержку уже не уменьшить настройками, поможет только другой провайдер или сервер игры в другом регионе.");
        return text;
    }

}
