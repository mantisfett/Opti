using Opti.Core.Findings;
using Opti.Core.Fixes;
using Opti.Core.Strategy;
using Opti.Core.Localization;

namespace Opti.Cli;

internal static class Output
{
    public static void Header(string text)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(text);
        Console.WriteLine(new string('─', Math.Min(text.Length, 70)));
        Console.ResetColor();
    }

    public static void Color(ConsoleColor c, string text)
    {
        Console.ForegroundColor = c;
        Console.Write(text);
        Console.ResetColor();
    }

    public static string SeverityRu(Severity s) => s switch
    {
        Severity.Critical => Loc.T("КРИТИЧНО"),
        Severity.Important => Loc.T("ВАЖНО"),
        _ => Loc.T("СОВЕТ"),
    };

    public static ConsoleColor SeverityColor(Severity s) => s switch
    {
        Severity.Critical => ConsoleColor.Red,
        Severity.Important => ConsoleColor.Yellow,
        _ => ConsoleColor.Gray,
    };

    public static string RiskRu(RiskLevel r) => r switch
    {
        RiskLevel.Safe => Loc.T("безопасно"),
        RiskLevel.Moderate => Loc.T("умеренно"),
        _ => Loc.T("рискованно"),
    };

    public static void PrintFindings(IReadOnlyList<Finding> findings)
    {
        Header(Loc.T($"Найдено проблем: {findings.Count}"));
        if (findings.Count == 0) { Console.WriteLine(Loc.T("Явных проблем не найдено.")); return; }
        foreach (var f in findings.Select(x => x.Translated()))
        {
            Console.WriteLine();
            Color(SeverityColor(f.Severity), $"[{SeverityRu(f.Severity)}] ");
            Console.Write(f.Title);
            Color(ConsoleColor.DarkGray, $"  ({f.Id})\n");
            if (f.IsHardwareLimitation) Color(ConsoleColor.DarkYellow, Loc.T("  Аппаратное ограничение: программно не исправляется.\n"));
            Console.WriteLine("  " + f.Explanation);
            foreach (var e in f.Evidence) Color(ConsoleColor.DarkGray, $"    • {e}\n");
            foreach (var s in f.ManualSteps) Console.WriteLine($"    → {s}");
            foreach (var l in f.Links) Console.WriteLine($"    🔗 {l}");
            if (f.FixIds.Count > 0) Color(ConsoleColor.Green, Loc.T($"    Исправления: {string.Join(", ", f.FixIds)}\n"));
        }
    }

    public static void PrintPlan(StrategyPlan plan)
    {
        Header(Loc.T($"Стратегия — режим «{plan.Goal.Ru()}»; устройство: {plan.Device.Describe()}"));
        if (plan.Fixes.Count == 0) Console.WriteLine(Loc.T("Программе нечего исправлять автоматически."));
        foreach (var p in plan.Fixes)
        {
            Console.Write($"{p.Order,2}. [{(p.Selected ? "x" : " ")}] ");
            Console.Write(p.Fix.Title);
            Color(ConsoleColor.DarkGray, Loc.T($"  ({p.Fix.Id}, {RiskRu(p.Fix.Risk)}{(p.Fix.RequiresReboot ? ", нужна перезагрузка" : "")}{(p.ApplySeparately ? ", отдельный замер" : "")})\n"));
            Console.WriteLine("       " + p.Fix.Explanation);
            Color(ConsoleColor.DarkGray, Loc.T($"       Причина: {string.Join(", ", p.TriggeredBy)}\n"));
            foreach (var n in p.Notes) Color(ConsoleColor.DarkYellow, $"       ! {n}\n");
        }
        if (plan.Excluded.Count > 0)
        {
            Header(Loc.T("Не включено в план"));
            foreach (var e in plan.Excluded) Console.WriteLine($"  – {e.FixId}: {e.Reason}");
        }
        if (plan.AdviceOnly.Count > 0)
        {
            Header(Loc.T("Только советы (вы делаете сами)"));
            foreach (var a in plan.AdviceOnly) Console.WriteLine($"  – {a.Title}");
        }
    }

    public static bool Confirm(string question)
    {
        Console.Write(question + " [y/N]: ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return answer is "y" or "yes" or "д" or "да";
    }
}
