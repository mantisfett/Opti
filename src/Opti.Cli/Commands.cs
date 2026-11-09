using System.Diagnostics;
using Opti.Analysis;
using Opti.Analysis.Detectors;
using Opti.Core.Fixes;
using Opti.Core.Json;
using Opti.Core.Profile;
using Opti.Core.Strategy;
using Opti.Fixes.Backends;
using Opti.Fixes.Catalog;
using Opti.Fixes.Journal;
using Opti.Fixes.Services;
using Opti.Measure.Capture;
using Opti.Measure.Stats;
using Opti.Workflow;
using Opti.Workflow.Measuring;
using Opti.Workflow.Reports;
using Opti.Workflow.Sessions;
using Opti.Core.Localization;

namespace Opti.Cli;

/// <summary>Справка и общие помощники командной строки.</summary>
internal static partial class Commands
{
    public static int Help()
    {
        Console.WriteLine($"Opti {AppInfo.Version} — " + Loc.T("диагностика лагов, точечные исправления и честный замер «до / после»."));
        Console.WriteLine(Loc.T("""

            Пошагово (рекомендуется):
              run      [--goal <режим>] [--mode game|synthetic|presentmon]   Мастер: анализ → замер «до» → план → применение →
                                                                             (перезагрузка) → замер «после» → сравнение → отчёт
              resume                                                         Продолжить после перезагрузки (запускается автоматически)

            Отдельные шаги:
              analyze  [--out profile.json] [--html report.html] [--dpc 30] [--net]
              plan     --goal <режим> [--profile profile.json]                План и точный список изменений (сухой прогон)
              measure  [--mode game|synthetic|presentmon] [--phase before|after] [--runs 3] [--duration 60] [--warmup 10]
                       [--process game.exe] [--out file.json]                Замер (в текущую сессию или в файл)
              apply    [--goal <режим>] [--fix ID,ID] [--option ID:key=value] [--yes] [--reboot]
              compare  [before.json after.json] [--html out.html]           Сравнение (сессии или двух файлов)
              ab       --fix HAGS_AB|TIMER_RES_GLOBAL|BCD_TIMERS_RESET [--mode synthetic] [--variant on|off]
                                                                             A/B-проверка спорной настройки, остаётся лучший вариант
              report   [--session ID] [--out report.html] [--open]          HTML-отчёт
              rollback --fix ID [--session ID] | --session ID | --all [--force]
                                                                             Откат; изменённое после Opti остаётся,
                                                                             --force возвращает исходное значение
              history                                                        Сессии и журнал изменений
              latency  [--seconds 60]                                        Задержка системы: DPC/ISR + точность таймера
              net      [--target 1.1.1.1] [--no-load] [--regions] [--dns]   Пинг, джиттер, bufferbloat; --regions — задержка до регионов, --dns — скорость DNS
              version                                                        Версия и проверка обновлений
              doctor                                                         Проверка окружения (права, ETW, утилиты, PresentMon)
              verify   [--reapply] [--all]                                   На месте ли изменения Opti (обновления Windows и драйверов
                                                                             их иногда возвращают); --reapply — применить снова
                                                                             возвращённое Windows, --all — и изменённое после Opti,
                                                                             и параметры загрузки (BCD)
              startup  [--disable имя,имя] [--enable имя] [--suggested]      Автозагрузка: список; отключение и включение с откатом в «Истории»
              uninstall [--yes]                                              Удалить Opti: откат всего, задача продолжения, папка данных
              bugreport [--out папка]                                        Архив для обращения: журнал работы, сессии, журнал изменений
                                                                             (никуда не отправляется)

            Режимы (--goal):
              max       Максимальная — всё, что даёт прирост: питание без ограничений, мышь, таймеры (с A/B-замером)
              balanced  Баланс — исправления ошибок + питание, сеть и игровые параметры Windows (по умолчанию)
              min       Минимальная — только исправление ошибок и возврат стандартных значений Windows
            """));
        Console.WriteLine(Loc.T("Данные: ") + OptiPaths.DataDir);
        return 0;
    }

    // ------------------------------------------------------------------ общие помощники

    internal static UserGoal ParseGoal(string? s, UserGoal def = UserGoal.Balanced) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "" => def,
        "1" => UserGoal.Maximum,
        "2" => UserGoal.Balanced,
        "3" => UserGoal.Minimal,
        // старые цели (до 1.3) — для скриптов пользователей
        "latency" or "fps" => UserGoal.Maximum,
        "smooth" or "stream" => UserGoal.Balanced,
        "quiet" => UserGoal.Minimal,
        var x => GoalNames.FromLegacy(x) ?? throw new ArgumentException(Loc.T($"Неизвестный режим «{x}». Варианты: max, balanced, min.")),
    };

    internal static MeasureMode ParseMode(string? s, MeasureMode def) => (s ?? "").ToLowerInvariant() switch
    {
        "" => def,
        "game" => MeasureMode.Game,
        "synthetic" or "bench" => MeasureMode.Synthetic,
        "presentmon" or "pm" => MeasureMode.PresentMon,
        var x => throw new ArgumentException(Loc.T($"Неизвестный режим замера «{x}». Варианты: game, synthetic, presentmon.")),
    };

    private static void RequireWindows(string what)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(what + Loc.T(" возможно только в Windows."));
    }

    private static UserGoal AskGoal(Args a, OptiSettings settings)
    {
        if (a.Get("goal") is { } g) return ParseGoal(g);
        Console.WriteLine(Loc.T("\nРежим:"));
        int n = 0;
        foreach (var i in new[] { UserGoal.Maximum, UserGoal.Balanced, UserGoal.Minimal }.Select(GoalInfo.Of))
            Console.WriteLine($"  {++n} — {i.Title}: {i.Summary}");
        Console.Write(Loc.T($"Выберите [1–3, Enter — {settings.DefaultGoal.Ru()}]: "));
        return ParseGoal(Console.ReadLine()?.Trim(), settings.DefaultGoal);
    }

    private static MeasureMode AskMode(Args a)
    {
        if (a.Get("mode") is { } m) return ParseMode(m, MeasureMode.Game);
        bool bench = OperatingSystem.IsWindows() && SyntheticRunner.Locate() is not null;
        Console.WriteLine(Loc.T("\nКак замерять:\n  1 — в реальной игре (вы запускаете игру, программа пишет кадры)") +
                          (bench ? Loc.T("\n  2 — синтетический тест Opti (полностью воспроизводимо, без игры)") : "") +
                          Loc.T("\n  3 — через PresentMon (если установлен: + режим вывода и задержка)"));
        Console.Write(Loc.T("Выберите [1–3, Enter — 1]: "));
        return Console.ReadLine()?.Trim() switch { "2" when bench => MeasureMode.Synthetic, "3" => MeasureMode.PresentMon, _ => MeasureMode.Game };
    }

    /// <summary>Серия прогонов с подсказками. Для игры — пауза перед каждым прогоном.</summary>
    internal static async Task<List<MeasurementRun>> MeasureSeries(OptiSettings settings, MeasureMode mode, string labelPrefix, Args a, string? process)
    {
        RequireWindows(Loc.T("Замер"));
        var runner = MeasureRunnerFactory.Create(mode, settings);
        int runs = a.GetInt("runs", settings.MeasureRuns), duration = a.GetInt("duration", settings.MeasureDurationSec),
            warmup = a.GetInt("warmup", mode == MeasureMode.Synthetic ? 5 : settings.MeasureWarmupSec);
        var list = new List<MeasurementRun>();
        Console.WriteLine(Loc.T($"\n{runner.Name}: {runs} прогона по {duration} с (+{warmup} с прогрева не учитываются)."));
        if (mode != MeasureMode.Synthetic)
            Console.WriteLine(Loc.T("Совет: подключите зарядку, закройте лишнее, в игре стойте на одном месте или повторяйте один и тот же отрезок."));
        for (int i = 1; i <= runs; i++)
        {
            if (mode != MeasureMode.Synthetic)
            {
                Console.Write(Loc.T($"\nПрогон {i}/{runs}: переключитесь в игру и нажмите Enter здесь (замер начнётся через 5 с)… "));
                Console.ReadLine();
                await Task.Delay(5000);
            }
            var progress = new Progress<MeasureProgress>(pr =>
            {
                var fps = pr.RecentFrameTimesMs.Count > 10 ? $" · {1000.0 / pr.RecentFrameTimesMs.Average():0} FPS" : "";
                Console.Write(Loc.T($"\r  {(pr.Warmup ? "прогрев" : "запись ")} {pr.ElapsedSec,5:0}/{pr.TotalSec:0} с{(pr.Process is null ? "" : " · " + pr.Process)}{fps}      "));
            });
            try
            {
                var run = await runner.RunAsync(new MeasureRequest($"{labelPrefix} #{i}", duration, warmup, process ?? a.Get("process"),
                    mode == MeasureMode.Synthetic ? Loc.T("синтетика") : a.Get("scenario") ?? Loc.T("игра")), progress);
                Console.WriteLine();
                list.Add(run);
                var m = Metrics.ComputeAll(run.FrameTimesMs);
                Console.WriteLine(Loc.T($"  {run.Conditions.ProcessName}: {run.FrameTimesMs.Count} кадров · средний FPS {m[MetricId.AvgFps]:0.#} · 1 % low {m[MetricId.Low1Fps]:0.#} · p99 {m[MetricId.FrameTimeP99]:0.##} мс") +
                                  (run.IndependentFlipShare is { } f ? $" · Independent Flip {f * 100:0} %" : "") +
                                  (run.ThermalThrottled ? Loc.T("  [троттлинг — прогон ненадёжен]") : ""));
                foreach (var w in run.Warnings) Output.Color(ConsoleColor.DarkYellow, "  ! " + w + "\n");
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine();
                Output.Color(ConsoleColor.Red, "  " + ex.Message + "\n");
                if (!Output.Confirm(Loc.T("  Повторить прогон?"))) break;
                i--;
            }
        }
        return list;
    }

    private static TuneSession SessionForWork(CliHost host, Args a, bool needAnalysis = true)
    {
        if (a.Get("session") is { } id) return host.Workflow.Store.Load(id) ?? throw new ArgumentException(Loc.T($"Сессия {id} не найдена"));
        var open = host.Workflow.Store.LatestOpen();
        if (open is not null && open.Stage < SessionStage.Compared) return open;
        if (!needAnalysis) throw new InvalidOperationException(Loc.T("Нет открытой сессии. Начните с «opti-cli run»."));
        var analysis = AnalysisLoader.LoadOrCollect(a);
        return host.Workflow.StartSession(analysis, ParseGoal(a.Get("goal"), host.Settings.DefaultGoal));
    }

    private static void PrintApplyReport(ApplyReport report)
    {
        Output.Header(Loc.T($"Применено (сессия {report.SessionId})"));
        if (report.RestorePoint is not null) Console.WriteLine(Loc.T($"Точка восстановления: {report.RestorePoint}"));
        if (report.RestorePointWarning is not null) Output.Color(ConsoleColor.Yellow, report.RestorePointWarning + "\n");
        foreach (var res in report.Results)
        {
            var color = res.Outcome switch { FixOutcome.Applied => ConsoleColor.Green, FixOutcome.Failed => ConsoleColor.Red, _ => ConsoleColor.Gray };
            var word = res.Outcome switch { FixOutcome.Applied => Loc.T("применено"), FixOutcome.Failed => Loc.T("ошибка"), FixOutcome.NothingToChange => Loc.T("уже в нужном состоянии"), _ => Loc.T("пропущено") };
            Output.Color(color, $"  {res.FixId}: {word}");
            Console.WriteLine(res.Error is null ? "" : " — " + res.Error);
            foreach (var c in res.Changes) Output.Color(ConsoleColor.DarkGray, $"     {c.Type} {c.Target}: {c.Before} → {c.After}\n");
        }
    }

    private static string WriteReport(CliHost host, TuneSession s, CompareOutcome? cmp, string? outPath = null)
    {
        var data = new ReportData
        {
            Session = s,
            Comparison = cmp?.Result ?? (s.Before.Count > 0 && s.After.Count > 0 ? new Comparer().Compare(s.Before, s.After) : null),
            CompareMessage = cmp?.Message,
            Changes = host.Journal.GetAll().Where(r => r.SessionId.StartsWith(s.Id, StringComparison.Ordinal)).ToList(),
            Catalog = host.Workflow.Catalog,
            AppVersion = AppInfo.Version,
        };
        outPath ??= Path.Combine(OptiPaths.ReportsDir, $"report-{s.Id}.html");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, HtmlReport.Build(data));
        return outPath;
    }

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { /* нет ассоциации */ }
    }

    private static bool OfferReboot(Args a)
    {
        Output.Color(ConsoleColor.Yellow, Loc.T("\nДля части изменений нужна перезагрузка. После входа в систему Opti продолжит автоматически.\n"));
        if (a.Has("reboot") || Output.Confirm(Loc.T("Перезагрузить сейчас?")))
        {
            if (OperatingSystem.IsWindows()) RebootHelper.RestartNow();
            Console.WriteLine(Loc.T("Перезагрузка через 10 секунд…"));
            return true;
        }
        Console.WriteLine(Loc.T("Перезагрузитесь, когда будет удобно. Продолжение — «opti-cli resume» (запустится само при входе)."));
        return false;
    }
}
