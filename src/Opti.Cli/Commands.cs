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

namespace Opti.Cli;

internal static class Commands
{
    public static int Help()
    {
        Console.WriteLine($"""
            Opti {AppInfo.Version} — диагностика лагов, точечные исправления и честный замер «до / после».

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
              net      [--target 1.1.1.1] [--no-load]                        Пинг, джиттер, bufferbloat
              version                                                        Версия и проверка обновлений
              doctor                                                         Проверка окружения (права, ETW, утилиты, PresentMon)
              verify   [--reapply] [--all]                                   На месте ли изменения Opti (обновления Windows и драйверов
                                                                             их иногда возвращают); --reapply — применить снова
                                                                             возвращённое Windows, --all — и изменённое после Opti
              uninstall [--yes]                                              Удалить Opti: откат всего, задача продолжения, папка данных
              bugreport [--out папка]                                        Архив для обращения: журнал работы, сессии, журнал изменений
                                                                             (никуда не отправляется)

            Режимы (--goal):
              max       Максимальная — всё, что даёт прирост: питание без ограничений, мышь, таймеры (с A/B-замером)
              balanced  Баланс — исправления ошибок + питание, сеть и игровые параметры Windows (по умолчанию)
              min       Минимальная — только исправление ошибок и возврат стандартных значений Windows
            Данные: {OptiPaths.DataDir}
            """);
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
        var x => GoalNames.FromLegacy(x) ?? throw new ArgumentException($"Неизвестный режим «{x}». Варианты: max, balanced, min."),
    };

    internal static MeasureMode ParseMode(string? s, MeasureMode def) => (s ?? "").ToLowerInvariant() switch
    {
        "" => def,
        "game" => MeasureMode.Game,
        "synthetic" or "bench" => MeasureMode.Synthetic,
        "presentmon" or "pm" => MeasureMode.PresentMon,
        var x => throw new ArgumentException($"Неизвестный режим замера «{x}». Варианты: game, synthetic, presentmon."),
    };

    private static void RequireWindows(string what)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(what + " возможно только в Windows.");
    }

    private static UserGoal AskGoal(Args a, OptiSettings settings)
    {
        if (a.Get("goal") is { } g) return ParseGoal(g);
        Console.WriteLine("\nРежим:");
        int n = 0;
        foreach (var i in new[] { UserGoal.Maximum, UserGoal.Balanced, UserGoal.Minimal }.Select(GoalInfo.Of))
            Console.WriteLine($"  {++n} — {i.Title}: {i.Summary}");
        Console.Write($"Выберите [1–3, Enter — {settings.DefaultGoal.Ru()}]: ");
        return ParseGoal(Console.ReadLine()?.Trim(), settings.DefaultGoal);
    }

    private static MeasureMode AskMode(Args a)
    {
        if (a.Get("mode") is { } m) return ParseMode(m, MeasureMode.Game);
        bool bench = OperatingSystem.IsWindows() && SyntheticRunner.Locate() is not null;
        Console.WriteLine("\nКак замерять:\n  1 — в реальной игре (вы запускаете игру, программа пишет кадры)" +
                          (bench ? "\n  2 — синтетический тест Opti (полностью воспроизводимо, без игры)" : "") +
                          "\n  3 — через PresentMon (если установлен: + режим вывода и задержка)");
        Console.Write("Выберите [1–3, Enter — 1]: ");
        return Console.ReadLine()?.Trim() switch { "2" when bench => MeasureMode.Synthetic, "3" => MeasureMode.PresentMon, _ => MeasureMode.Game };
    }

    /// <summary>Серия прогонов с подсказками. Для игры — пауза перед каждым прогоном.</summary>
    internal static async Task<List<MeasurementRun>> MeasureSeries(OptiSettings settings, MeasureMode mode, string labelPrefix, Args a, string? process)
    {
        RequireWindows("Замер");
        var runner = MeasureRunnerFactory.Create(mode, settings);
        int runs = a.GetInt("runs", settings.MeasureRuns), duration = a.GetInt("duration", settings.MeasureDurationSec),
            warmup = a.GetInt("warmup", mode == MeasureMode.Synthetic ? 5 : settings.MeasureWarmupSec);
        var list = new List<MeasurementRun>();
        Console.WriteLine($"\n{runner.Name}: {runs} прогона по {duration} с (+{warmup} с прогрева не учитываются).");
        if (mode != MeasureMode.Synthetic)
            Console.WriteLine("Совет: подключите зарядку, закройте лишнее, в игре стойте на одном месте или повторяйте один и тот же отрезок.");
        for (int i = 1; i <= runs; i++)
        {
            if (mode != MeasureMode.Synthetic)
            {
                Console.Write($"\nПрогон {i}/{runs}: переключитесь в игру и нажмите Enter здесь (замер начнётся через 5 с)… ");
                Console.ReadLine();
                await Task.Delay(5000);
            }
            var progress = new Progress<MeasureProgress>(pr =>
            {
                var fps = pr.RecentFrameTimesMs.Count > 10 ? $" · {1000.0 / pr.RecentFrameTimesMs.Average():0} FPS" : "";
                Console.Write($"\r  {(pr.Warmup ? "прогрев" : "запись ")} {pr.ElapsedSec,5:0}/{pr.TotalSec:0} с{(pr.Process is null ? "" : " · " + pr.Process)}{fps}      ");
            });
            try
            {
                var run = await runner.RunAsync(new MeasureRequest($"{labelPrefix} #{i}", duration, warmup, process ?? a.Get("process"),
                    mode == MeasureMode.Synthetic ? "синтетика" : a.Get("scenario") ?? "игра"), progress);
                Console.WriteLine();
                list.Add(run);
                var m = Metrics.ComputeAll(run.FrameTimesMs);
                Console.WriteLine($"  {run.Conditions.ProcessName}: {run.FrameTimesMs.Count} кадров · средний FPS {m[MetricId.AvgFps]:0.#} · 1 % low {m[MetricId.Low1Fps]:0.#} · p99 {m[MetricId.FrameTimeP99]:0.##} мс" +
                                  (run.IndependentFlipShare is { } f ? $" · Independent Flip {f * 100:0} %" : "") +
                                  (run.ThermalThrottled ? "  [троттлинг — прогон ненадёжен]" : ""));
                foreach (var w in run.Warnings) Output.Color(ConsoleColor.DarkYellow, "  ! " + w + "\n");
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine();
                Output.Color(ConsoleColor.Red, "  " + ex.Message + "\n");
                if (!Output.Confirm("  Повторить прогон?")) break;
                i--;
            }
        }
        return list;
    }

    private static TuneSession SessionForWork(CliHost host, Args a, bool needAnalysis = true)
    {
        if (a.Get("session") is { } id) return host.Workflow.Store.Load(id) ?? throw new ArgumentException($"Сессия {id} не найдена");
        var open = host.Workflow.Store.LatestOpen();
        if (open is not null && open.Stage < SessionStage.Compared) return open;
        if (!needAnalysis) throw new InvalidOperationException("Нет открытой сессии. Начните с «opti-cli run».");
        var analysis = AnalysisLoader.LoadOrCollect(a);
        return host.Workflow.StartSession(analysis, ParseGoal(a.Get("goal"), host.Settings.DefaultGoal));
    }

    private static void PrintApplyReport(ApplyReport report)
    {
        Output.Header($"Применено (сессия {report.SessionId})");
        if (report.RestorePoint is not null) Console.WriteLine($"Точка восстановления: {report.RestorePoint}");
        if (report.RestorePointWarning is not null) Output.Color(ConsoleColor.Yellow, report.RestorePointWarning + "\n");
        foreach (var res in report.Results)
        {
            var color = res.Outcome switch { FixOutcome.Applied => ConsoleColor.Green, FixOutcome.Failed => ConsoleColor.Red, _ => ConsoleColor.Gray };
            var word = res.Outcome switch { FixOutcome.Applied => "применено", FixOutcome.Failed => "ошибка", FixOutcome.NothingToChange => "уже в нужном состоянии", _ => "пропущено" };
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
        Output.Color(ConsoleColor.Yellow, "\nДля части изменений нужна перезагрузка. После входа в систему Opti продолжит автоматически.\n");
        if (a.Has("reboot") || Output.Confirm("Перезагрузить сейчас?"))
        {
            if (OperatingSystem.IsWindows()) RebootHelper.RestartNow();
            Console.WriteLine("Перезагрузка через 10 секунд…");
            return true;
        }
        Console.WriteLine("Перезагрузитесь, когда будет удобно. Продолжение — «opti-cli resume» (запустится само при входе).");
        return false;
    }

    // ------------------------------------------------------------------ run (мастер)

    public static async Task<int> Run(Args a)
    {
        RequireWindows("Оптимизация");
        using var host = new CliHost();
        var wf = host.Workflow;

        var open = wf.Store.LatestOpen();
        if (open is not null && open.Stage is SessionStage.AwaitingReboot or SessionStage.Applied or SessionStage.VerifiedAfterReboot or SessionStage.MeasuredAfter)
        {
            Console.WriteLine($"Есть незавершённая сессия {open.Id} ({open.Stage}). Продолжаю её.");
            return await ContinueSession(host, open, a);
        }

        var goal = AskGoal(a, host.Settings);
        var analysis = AnalysisLoader.LoadOrCollect(a);
        Output.PrintFindings(analysis.Findings);
        var s = wf.StartSession(analysis, goal);
        s.MeasureMode = AskMode(a);
        wf.Store.Save(s);

        Output.Header("Шаг 1. Замер «ДО»");
        if (Output.Confirm("Сделать замер «до»? (без него программа не сможет честно показать результат)"))
            wf.RecordBefore(s, await MeasureSeries(host.Settings, s.MeasureMode, "до", a, s.GameProcess));
        s.GameProcess = s.Before.FirstOrDefault()?.Conditions.ProcessName is { } pn && s.MeasureMode != MeasureMode.Synthetic ? pn : s.GameProcess;

        Output.Header("Шаг 2. Стратегия");
        var plan = wf.BuildPlan(s);
        Output.PrintPlan(plan);
        var chosen = plan.Fixes.Where(p => p.Selected && !p.ApplySeparately).Select(p => p.Fix).ToList();
        var abCandidates = plan.Fixes.Where(p => p.ApplySeparately).Select(p => p.Fix.Id).ToList();
        if (chosen.Count == 0)
        {
            Console.WriteLine("\nАвтоматически применять нечего. Советы — в отчёте.");
            Console.WriteLine($"Отчёт: {WriteReport(host, s, null)}");
            wf.Close(s);
            return 0;
        }
        Console.WriteLine("\nБудут применены: " + string.Join(", ", chosen.Select(f => f.Id)));
        if (abCandidates.Count > 0) Console.WriteLine("Спорные настройки проверяются отдельно: " + string.Join(", ", abCandidates.Select(id => $"opti-cli ab --fix {id}")));
        if (!a.Has("yes") && !Output.Confirm("Применить? Перед изменениями создаётся точка восстановления, всё записывается в журнал для отката.")) return 1;

        Output.Header("Шаг 3. Применение");
        var report = wf.Apply(s, chosen, CliHost.ExePath, "resume");
        PrintApplyReport(report);
        if (report.RebootRequired) { OfferReboot(a); return 0; }
        return await ContinueSession(host, s, a);
    }

    /// <summary>Продолжение сессии: самопроверка → замер «после» → сравнение → отчёт → решение.</summary>
    private static async Task<int> ContinueSession(CliHost host, TuneSession s, Args a)
    {
        var wf = host.Workflow;
        if (s.Stage == SessionStage.AwaitingReboot)
        {
            var check = wf.ResumeIfRebooted(s);
            if (check is null) { Console.WriteLine("Сессия ждёт перезагрузки. Перезагрузите компьютер — Opti продолжит сам."); return 0; }
            PrintPostReboot(check);
            if (check.SuggestRollback && Output.Confirm("После изменений были сбои системы. Откатить изменения этой сессии?"))
            {
                PrintRollback(wf.RollbackSession(s));
                wf.Close(s);
                return 0;
            }
        }

        if (s.Stage is SessionStage.Applied or SessionStage.VerifiedAfterReboot && s.Before.Count > 0)
        {
            Output.Header("Шаг 4. Замер «ПОСЛЕ» — в тех же условиях");
            wf.RecordAfter(s, await MeasureSeries(host.Settings, s.MeasureMode, "после", a, s.GameProcess));
        }
        if (s.Before.Count == 0 || s.After.Count == 0)
        {
            Console.WriteLine($"Замера «до» нет — сравнение невозможно. Отчёт: {WriteReport(host, s, null)}");
            wf.Close(s);
            return 0;
        }

        Output.Header("Шаг 5. Сравнение");
        var cmp = wf.Compare(s);
        PrintComparison(cmp.Result);
        Console.WriteLine("\n" + cmp.Message);
        var path = WriteReport(host, s, cmp);
        Console.WriteLine($"Отчёт: {path}");
        if (cmp.Recommendation == Recommendation.SuggestRollback && (a.Has("yes") || Output.Confirm("Откатить изменения этой сессии?")))
        {
            PrintRollback(wf.RollbackSession(s));
            WriteReport(host, s, cmp, path);
        }
        wf.Close(s);
        if (!a.Has("no-open")) OpenFile(path);
        return 0;
    }

    private static void PrintPostReboot(PostRebootReport check)
    {
        Output.Header("Самопроверка после перезагрузки");
        Console.WriteLine($"  Подтверждено изменений: {check.Verified}");
        foreach (var n in check.NotApplied) Output.Color(ConsoleColor.Yellow, "  Не применилось: " + n + "\n");
        foreach (var e in check.ReadErrors) Output.Color(ConsoleColor.DarkGray, "  " + e + "\n");
        foreach (var c in check.Crashes) Output.Color(ConsoleColor.Red, $"  Сбой системы {c.Time.LocalDateTime:dd.MM HH:mm}: событие {c.EventId} {c.Source}\n");
        if (check.Healthy) Output.Color(ConsoleColor.Green, "  Всё применилось, сбоев не было.\n");
    }

    private static void PrintComparison(ComparisonResult result)
    {
        foreach (var m in result.Metrics)
        {
            var color = m.Verdict switch { Verdict.Better => ConsoleColor.Green, Verdict.Worse => ConsoleColor.Red, _ => ConsoleColor.Gray };
            Output.Color(color, "  " + m.Format() + "\n");
            Output.Color(ConsoleColor.DarkGray, $"     95 % ДИ: до [{m.BeforeCi.Low:0.##}; {m.BeforeCi.High:0.##}], после [{m.AfterCi.Low:0.##}; {m.AfterCi.High:0.##}], разность [{m.DiffCi.Low:+0.##;-0.##;0}; {m.DiffCi.High:+0.##;-0.##;0}], разброс прогонов {m.RunSpread:0.##}\n");
        }
        foreach (var w in result.Warnings) Output.Color(ConsoleColor.Yellow, "  ! " + w + "\n");
    }

    private static void PrintRollback(RollbackReport rep)
    {
        foreach (var r in rep.Restored) Output.Color(ConsoleColor.Green, $"  восстановлено {r.Type} {r.Target} = {r.Before}\n");
        foreach (var r in rep.SkippedSupersededBy) Output.Color(ConsoleColor.Yellow, $"  оставлено {r.Type} {r.Target}: позже изменено другим исправлением\n");
        foreach (var d in rep.ChangedSinceOpti)
            Output.Color(ConsoleColor.Yellow, $"  оставлено {d.Record.Type} {d.Record.Target}: изменено после Opti (сейчас {d.Current}, Opti ставил {d.Record.After}, до Opti было {d.Record.Before}). Вернуть исходное: --force\n");
        foreach (var e in rep.Errors) Output.Color(ConsoleColor.Red, $"  ошибка: {e}\n");
        if (rep.Restored.Count + rep.SkippedSupersededBy.Count + rep.ChangedSinceOpti.Count + rep.Errors.Count == 0) Console.WriteLine("  Нечего откатывать.");
        if (rep.Restored.Any(r => r.Type is ChangeType.Bcd) || rep.Restored.Any(r => r.Target.Contains("GraphicsDrivers") || r.Target.Contains("Dwm") || r.Target.Contains("kernel")))
            Output.Color(ConsoleColor.Yellow, "  Часть возвращённых настроек вступит в силу после перезагрузки.\n");
    }

    // ------------------------------------------------------------------ resume

    public static async Task<int> Resume(Args a)
    {
        RequireWindows("Продолжение");
        using var host = new CliHost();
        var wf = host.Workflow;
        var s = wf.Store.LatestOpen();
        if (s is null) { Console.WriteLine("Нет незавершённых сессий."); try { new WindowsResumeScheduler().Cancel(); } catch { } return 0; }
        Console.WriteLine($"Opti {AppInfo.Version}: продолжаю сессию {s.Id} (режим «{s.Goal.Ru()}»).");

        int rc;
        if (s.AbTests.Any(x => x.Phase == AbPhase.AwaitRebootB))
            rc = await ContinueAb(host, s, a);
        else
            rc = await ContinueSession(host, s, a);

        if (!a.Has("yes")) { Console.Write("\nНажмите Enter, чтобы закрыть окно…"); Console.ReadLine(); }
        return rc;
    }

    // ------------------------------------------------------------------ ab

    public static async Task<int> Ab(Args a)
    {
        RequireWindows("A/B-проверка");
        var fixId = a.Get("fix") ?? throw new ArgumentException("Укажите --fix (например, HAGS_AB).");
        using var host = new CliHost();
        var wf = host.Workflow;
        var s = SessionForWork(host, a);
        s.MeasureMode = ParseMode(a.Get("mode"), SyntheticRunner.Locate() is not null ? MeasureMode.Synthetic : MeasureMode.Game);
        var ab = wf.StartAb(s, fixId, a.Get("variant"));
        Output.Header($"A/B: {ab.VariantB}");
        Console.WriteLine("Вариант A — текущее состояние, вариант B — с изменением. Останется тот, что достоверно лучше; при равенстве — исходный.");

        Output.Header("Замер варианта A");
        wf.RecordAbA(s, await MeasureSeries(host.Settings, s.MeasureMode, "A", a, s.GameProcess));
        Output.Header("Применение варианта B");
        var rep = wf.ApplyAbB(s, CliHost.ExePath);
        PrintApplyReport(rep);
        if (rep.RebootRequired) { OfferReboot(a); return 0; }
        return await ContinueAb(host, s, a);
    }

    private static async Task<int> ContinueAb(CliHost host, TuneSession s, Args a)
    {
        var wf = host.Workflow;
        if (s.AbTests.Any(x => x.Phase == AbPhase.AwaitRebootB) && !wf.ResumeAbIfRebooted(s))
        {
            Console.WriteLine("A/B-проверка ждёт перезагрузки.");
            return 0;
        }
        Output.Header("Замер варианта B");
        var runs = await MeasureSeries(host.Settings, s.MeasureMode, "B", a, s.GameProcess);
        var (ab, result, rb) = wf.DecideAb(s, runs);
        PrintComparison(result);
        Output.Color(ab.KeptB ? ConsoleColor.Green : ConsoleColor.Yellow, "\n" + ab.Summary + "\n");
        if (rb is not null) PrintRollback(rb);
        Console.WriteLine($"Отчёт: {WriteReport(host, s, null)}");
        return 0;
    }

    // ------------------------------------------------------------------ analyze / plan

    public static int Analyze(Args a)
    {
        var result = AnalysisLoader.LoadOrCollect(a);
        if (a.Get("dpc") is { } dpc && OperatingSystem.IsWindows())
        {
            Console.WriteLine($"Тест DPC/ISR {dpc} с — не нагружайте компьютер…");
            result.Profile.Dpc = EtwDpcCapture.Run(TimeSpan.FromSeconds(int.Parse(dpc)));
        }
        if (a.Has("net"))
        {
            Console.WriteLine("Тест сети (~20 с)…");
            result.Profile.NetQuality = NetworkTest.RunAsync(OptiSettings.Load().NetworkTarget).GetAwaiter().GetResult();
        }
        if (a.Has("dpc") || a.Has("net")) result = SystemAnalyzer.FromProfile(result.Profile);

        PrintProfileSummary(result.Profile);
        Output.PrintFindings(result.Findings);
        if (result.Profile.CollectorErrors.Count > 0)
        {
            Output.Header("Не удалось собрать");
            foreach (var e in result.Profile.CollectorErrors) Output.Color(ConsoleColor.DarkGray, "  " + e + "\n");
        }

        var outPath = a.Get("out") ?? Path.Combine(OptiPaths.ReportsDir, $"analysis-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, OptiJson.Serialize(new { profile = result.Profile, findings = result.Findings, timings = result.Timings }));
        Console.WriteLine($"\nПрофиль и находки: {outPath}");
        if (a.Get("html") is { } html)
        {
            File.WriteAllText(html, HtmlReport.Build(new ReportData
            {
                Session = new TuneSession { Profile = result.Profile, Findings = result.Findings, Goal = OptiSettings.Load().DefaultGoal },
                Catalog = FixCatalog.All(), AppVersion = AppInfo.Version,
            }));
            Console.WriteLine($"HTML-отчёт: {html}");
        }
        return 0;
    }

    private static void PrintProfileSummary(SystemProfile p)
    {
        Output.Header("Профиль ПК");
        Console.WriteLine($"  {DeviceClassification.From(p).Describe()}");
        Console.WriteLine($"  {p.Device.Manufacturer} {p.Device.Model}");
        Console.WriteLine($"  {p.Os.ProductName} {p.Os.DisplayVersion} (сборка {p.Os.Build}.{p.Os.Ubr}){(p.Os.ModifiedBuild is { } m ? $", модифицированная: {m}" : "")}");
        Console.WriteLine($"  CPU: {p.Cpu.Name} — {p.Cpu.Cores} ядер / {p.Cpu.Threads} потоков");
        foreach (var g in p.Gpus)
            Console.WriteLine($"  GPU: {g.Name} ({(g.IsIntegrated ? "встроенная" : "дискретная")}, драйвер {g.DriverVersion} от {g.DriverDate:yyyy-MM-dd})");
        foreach (var d in p.Displays)
            Console.WriteLine($"  Экран: {d.FriendlyName ?? d.DeviceName} {d.Width}×{d.Height} @ {d.CurrentHz} Гц (макс. {d.MaxHzAtCurrentResolution}){(d.IsInternal == true ? ", встроенный" : "")}");
        Console.WriteLine($"  ОЗУ: {p.Memory.TotalMb / 1024.0:0.#} ГБ, свободно {p.Memory.AvailablePercent:0} %, модулей {p.Memory.Modules.Count}");
        if (p.SystemDisk is { } sd) Console.WriteLine($"  Системный диск: {sd.Model} ({sd.MediaType}), свободно {sd.FreeGb:0} ГБ");
        Console.WriteLine($"  Питание: {(p.Power.OnAcPower == true ? "от сети" : p.Power.OnAcPower == false ? "от батареи" : "?")}, схема {p.Power.ActiveSchemeName ?? p.Power.ActiveSchemeGuid}");
        Console.WriteLine($"  Игр найдено: {p.Games.Count}");
    }

    public static int Plan(Args a)
    {
        var r = AnalysisLoader.LoadOrCollect(a);
        var settings = OptiSettings.Load();
        var wf = new TuneWorkflow(settings, new SessionStore(Path.Combine(Path.GetTempPath(), "opti-plan")), InMemoryBackend.CreateRegistry(out _), new InMemoryChangeJournal());
        var session = new TuneSession { Profile = r.Profile, Findings = r.Findings, Goal = ParseGoal(a.Get("goal"), settings.DefaultGoal) };
        var plan = wf.BuildPlan(session);
        Output.PrintPlan(plan);

        bool emulate = true;
        IBackendRegistry backends;
        if (!a.Has("profile") && OperatingSystem.IsWindows())
        {
            emulate = false;
            backends = new DryRunBackendRegistry(WindowsBackends.Create());
        }
        else backends = ProfileSeeder.CreateEmulation(r.Profile, out _);
        var preview = new ApplyService(backends, new InMemoryChangeJournal(), restore: null)
            .Apply(r.Profile, plan.Fixes.Where(f => f.Selected).Select(f => f.Fix), plan.Goal);
        Output.Header(emulate ? "Что будет изменено (эмуляция на сохранённом профиле)" : "Что будет изменено (сухой прогон, ничего не записано)");
        foreach (var res in preview.Results)
        {
            Console.WriteLine($"  {res.FixId}: {res.Outcome}{(res.Error is null ? "" : " — " + res.Error)}");
            foreach (var c in res.Changes) Output.Color(ConsoleColor.DarkGray, $"     {c.Type} {c.Target}: {c.Before} → {c.After}\n");
        }
        return 0;
    }

    // ------------------------------------------------------------------ measure / apply / compare

    public static async Task<int> Measure(Args a)
    {
        RequireWindows("Замер");
        var settings = OptiSettings.Load();
        var mode = ParseMode(a.Get("mode"), MeasureMode.Game);
        if (a.Get("out") is { } outPath)
        {
            var runs = await MeasureSeries(settings, mode, Path.GetFileNameWithoutExtension(outPath), a, null);
            File.WriteAllText(outPath, OptiJson.Serialize(runs));
            Console.WriteLine($"\nЗамер сохранён: {outPath}");
            return 0;
        }
        using var host = new CliHost();
        var s = SessionForWork(host, a);
        if (a.Has("mode")) s.MeasureMode = mode;
        bool after = a.Get("phase") is { } ph ? ph.Equals("after", StringComparison.OrdinalIgnoreCase) : s.Stage >= SessionStage.Applied;
        var series = await MeasureSeries(settings, s.MeasureMode, after ? "после" : "до", a, s.GameProcess);
        if (after) host.Workflow.RecordAfter(s, series); else host.Workflow.RecordBefore(s, series);
        Console.WriteLine($"\nЗаписано в сессию {s.Id} как «{(after ? "после" : "до")}».");
        return 0;
    }

    public static int Apply(Args a)
    {
        RequireWindows("Применение исправлений");
        using var host = new CliHost();
        var wf = host.Workflow;
        var s = SessionForWork(host, a);
        if (a.Get("goal") is { } g) s.Goal = ParseGoal(g);
        var plan = wf.BuildPlan(s);
        Output.PrintPlan(plan);

        var requested = a.Get("fix")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chosen = plan.Fixes.Where(p => requested is null ? p.Selected : requested.Contains(p.Fix.Id)).ToList();
        if (requested is not null)
            foreach (var id in requested.Where(id => chosen.All(c => !c.Fix.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
                Output.Color(ConsoleColor.Yellow, $"{id}: не входит в план для этого ПК и цели — пропущено.\n");
        var separate = chosen.Where(c => c.ApplySeparately).ToList();
        if (separate.Count > 0 && chosen.Count > 1)
        {
            Output.Color(ConsoleColor.Yellow, "Спорные настройки проверяются отдельно: " + string.Join(", ", separate.Select(x => $"opti-cli ab --fix {x.Fix.Id}")) + "\n");
            chosen = chosen.Except(separate).ToList();
        }
        if (chosen.Count == 0) { Console.WriteLine("Нечего применять."); return 0; }

        foreach (var (fix, opts) in ParseOptions(a)) s.FixOptions[fix] = opts;
        Console.WriteLine("\nБудут применены: " + string.Join(", ", chosen.Select(c => c.Fix.Id)));
        if (!a.Has("yes") && !Output.Confirm("Продолжить? Перед изменениями будет создана точка восстановления.")) return 1;

        var report = wf.Apply(s, chosen.Select(c => c.Fix).ToList(), CliHost.ExePath, "resume");
        PrintApplyReport(report);
        Console.WriteLine($"Откат этой сессии: opti-cli rollback --session {s.Id}");
        if (report.RebootRequired) OfferReboot(a);
        else Console.WriteLine("Дальше: «opti-cli measure» (замер «после»), затем «opti-cli compare».");
        return report.Results.Any(x => x.Outcome == FixOutcome.Failed) ? 3 : 0;
    }

    private static Dictionary<string, Dictionary<string, string>> ParseOptions(Args a)
    {
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in a.GetAll("option"))
        {
            int c = o.IndexOf(':'), e = o.IndexOf('=');
            if (c <= 0 || e <= c) throw new ArgumentException($"Неверная опция «{o}». Формат: FIX_ID:ключ=значение");
            var fix = o[..c];
            if (!map.TryGetValue(fix, out var d)) map[fix] = d = new(StringComparer.OrdinalIgnoreCase);
            d[o[(c + 1)..e]] = o[(e + 1)..].Replace(';', '\n');
        }
        return map;
    }

    public static int Compare(Args a)
    {
        if (a.Positional.Count >= 2)
        {
            var before = OptiJson.Deserialize<List<MeasurementRun>>(File.ReadAllText(a.Positional[0]));
            var after = OptiJson.Deserialize<List<MeasurementRun>>(File.ReadAllText(a.Positional[1]));
            var result = new Comparer().Compare(before, after);
            Output.Header("Сравнение «до / после»");
            PrintComparison(result);
            Console.WriteLine($"\nИтог: {result.Overall switch { Verdict.Better => "стало лучше", Verdict.Worse => "стало хуже", Verdict.NoChange => "без изменений (в пределах шума)", _ => "недостаточно данных" }}");
            if (a.Get("csv") is { } csv) WriteCsv(csv, before, after);
            if (a.Get("html") is { } html)
            {
                var s = new TuneSession { Before = before, After = after };
                File.WriteAllText(html, HtmlReport.Build(new ReportData { Session = s, Comparison = result, AppVersion = AppInfo.Version }));
                Console.WriteLine($"HTML-отчёт: {html}");
            }
            return 0;
        }
        RequireWindows("Сравнение сессии");
        using var host = new CliHost();
        var session = SessionForWork(host, a, needAnalysis: false);
        var cmp = host.Workflow.Compare(session);
        Output.Header("Сравнение «до / после»");
        PrintComparison(cmp.Result);
        Console.WriteLine("\n" + cmp.Message);
        var path = WriteReport(host, session, cmp, a.Get("html"));
        Console.WriteLine($"Отчёт: {path}");
        if (a.Get("csv") is { } csv2) WriteCsv(csv2, session.Before, session.After);
        if (cmp.Recommendation == Recommendation.SuggestRollback && Output.Confirm("Откатить изменения этой сессии?"))
            PrintRollback(host.Workflow.RollbackSession(session));
        return 0;
    }

    private static void WriteCsv(string path, IReadOnlyList<MeasurementRun> before, IReadOnlyList<MeasurementRun> after)
    {
        using var w = new StreamWriter(path);
        w.WriteLine("set,run,frame,frametime_ms");
        foreach (var (set, runs) in new[] { ("before", before), ("after", after) })
            for (int r = 0; r < runs.Count; r++)
                for (int f = 0; f < runs[r].FrameTimesMs.Count; f++)
                    w.WriteLine(FormattableString.Invariant($"{set},{r + 1},{f},{runs[r].FrameTimesMs[f]:0.###}"));
        Console.WriteLine($"Сырые фреймтаймы: {path}");
    }

    // ------------------------------------------------------------------ report / rollback / history

    public static int Report(Args a)
    {
        RequireWindows("Отчёт по сессии");
        using var host = new CliHost();
        var s = a.Get("session") is { } id ? host.Workflow.Store.Load(id) ?? throw new ArgumentException($"Сессия {id} не найдена")
                                           : host.Workflow.Store.All().FirstOrDefault() ?? throw new InvalidOperationException("Сессий ещё нет.");
        var path = WriteReport(host, s, null, a.Get("out"));
        Console.WriteLine($"Отчёт: {path}");
        if (a.Has("open")) OpenFile(path);
        return 0;
    }

    public static int Rollback(Args a)
    {
        RequireWindows("Откат");
        using var host = new CliHost();
        var svc = new RollbackService(WindowsBackends.Create(), host.Journal);
        bool force = a.Has("force");
        RollbackReport rep;
        if (a.Get("fix") is { } fix)
        {
            rep = a.Get("session") is { } sid && host.Workflow.Store.Load(sid) is { } s1
                ? host.Workflow.RollbackFix(s1, fix, force)
                : svc.RollbackFix(fix, a.Get("session"), force);
        }
        else if (a.Get("session") is { } s)
        {
            rep = host.Workflow.Store.Load(s) is { } session ? host.Workflow.RollbackSession(session, force)
                : svc.RollbackRecords(host.Journal.GetAll().Where(r => r.SessionId.StartsWith(s, StringComparison.Ordinal)), force);
        }
        else if (a.Has("all"))
        {
            if (!a.Has("yes") && !Output.Confirm("Откатить ВСЕ изменения Opti?")) return 1;
            rep = svc.RollbackAll(force);
        }
        else throw new ArgumentException("Укажите --fix ID, --session ID или --all");
        PrintRollback(rep);
        return rep.Errors.Count > 0 ? 3 : 0;
    }

    public static int Verify(Args a)
    {
        RequireWindows("Проверка изменений");
        using var host = new CliHost();
        var wf = host.Workflow;
        var report = wf.Verify();
        Output.Header(report.Summary());
        foreach (var i in report.Items.Where(i => i.Status != VerifyStatus.InPlace))
        {
            var (text, color) = i.Status switch
            {
                VerifyStatus.Reverted => ("возвращено Windows/драйвером", ConsoleColor.Yellow),
                VerifyStatus.Changed => ("изменено после Opti", ConsoleColor.Gray),
                _ => ("не проверено: " + i.Error, ConsoleColor.Red),
            };
            Output.Color(color, $"  {wf.FixTitle(i.Record.FixId)} — {text}\n");
            Output.Color(ConsoleColor.DarkGray, $"     {i.Record.Type} {i.Record.Target}: сейчас {i.Current}, Opti ставил {i.Record.After}\n");
        }
        if (a.Has("reapply"))
        {
            var items = report.Items.Where(i => i.Status == VerifyStatus.Reverted || (a.Has("all") && i.Status == VerifyStatus.Changed)).ToList();
            var (done, errors) = wf.Reapply(items);
            Output.Color(ConsoleColor.Green, $"Применено снова: {done}\n");
            foreach (var e in errors) Output.Color(ConsoleColor.Red, "  ошибка: " + e + "\n");
            return errors.Count > 0 ? 3 : 0;
        }
        return report.AllInPlace ? 0 : 1;
    }

    public static int Uninstall(Args a)
    {
        RequireWindows("Удаление");
        if (!a.Has("yes") && !Output.Confirm("Откатить ВСЕ изменения Opti и удалить его папку данных?")) return 1;
        UninstallReport r;
        using (var host = new CliHost())
            r = Uninstaller.Run(host.Workflow, host.Journal, new WindowsResumeScheduler(), OptiPaths.DataDir);
        PrintRollback(r.Rollback);
        if (r.Rollback.Errors.Count > 0)
        {
            Output.Color(ConsoleColor.Red, "Часть изменений откатить не удалось — папка данных оставлена, без журнала их не вернуть.\n");
            return 3;
        }
        Console.WriteLine(r.DataRemovalScheduled
            ? $"Папка {r.DataDir} будет удалена через несколько секунд. Файл opti-cli.exe удалите сами. Перезагрузите компьютер."
            : $"Удалите папку {r.DataDir} вручную. Перезагрузите компьютер.");
        return 0;
    }

    public static int BugReport(Args a)
    {
        using var journal = new SqliteChangeJournal(OptiPaths.JournalPath);
        var path = ProblemReport.Create(OptiPaths.DataDir, journal, a.Get("out") ?? Environment.CurrentDirectory, AppInfo.Version);
        Console.WriteLine($"Отчёт о проблеме: {path}");
        Console.WriteLine("Он никуда не отправляется — приложите его к обращению сами.");
        return 0;
    }

    public static int History(Args a)
    {
        RequireWindows("История");
        using var host = new CliHost();
        var all = host.Journal.GetAll();
        var sessions = host.Workflow.Store.All();
        if (sessions.Count == 0 && all.Count == 0) { Console.WriteLine("Истории пока нет."); return 0; }
        foreach (var s in sessions)
        {
            var outcome = s.Outcome switch
            {
                SessionOutcome.Improved => "стало лучше", SessionOutcome.Worse => "стало хуже", SessionOutcome.NoEffect => "без эффекта",
                SessionOutcome.RolledBack => "откачено", _ => "без вердикта",
            };
            Output.Header($"{s.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm} · {s.Id} · {s.Goal.Ru()} · {outcome} · {s.Stage}");
            foreach (var r in all.Where(r => r.SessionId.StartsWith(s.Id, StringComparison.Ordinal)))
                Console.WriteLine($"  {(r.RolledBack ? "↩" : r.Committed ? "✓" : "?")} {r.FixId} {r.Type} {r.Target}: {r.Before} → {r.After}{(r.Committed ? "" : " (не подтверждено: программа завершилась во время изменения)")}");
            foreach (var ab in s.AbTests) Console.WriteLine($"  A/B {ab.FixId}: {ab.Summary ?? ab.Phase.ToString()}");
        }
        var orphan = all.Where(r => sessions.All(s => !r.SessionId.StartsWith(s.Id, StringComparison.Ordinal))).ToList();
        if (orphan.Count > 0)
        {
            Output.Header("Изменения вне сессий");
            foreach (var r in orphan) Console.WriteLine($"  {(r.RolledBack ? "↩" : "✓")} {r.SessionId} {r.FixId} {r.Type} {r.Target}: {r.Before} → {r.After}");
        }
        return 0;
    }

    // ------------------------------------------------------------------ latency / net / version

    public static int Latency(Args a)
    {
        RequireWindows("Тест DPC/ISR");
        int seconds = a.GetInt("seconds", 60);
        Console.WriteLine($"Тест задержки системы, {seconds} с. Не запускайте тяжёлые программы.");
        var dpc = EtwDpcCapture.Run(TimeSpan.FromSeconds(seconds));
        var timer = TimerAccuracyTest.Run();
        Output.Header("DPC/ISR — худшие драйверы");
        foreach (var d in dpc.Drivers.Take(10))
        {
            var bad = Math.Max(d.MaxDpcUs, d.MaxIsrUs) > 1000;
            Output.Color(bad ? ConsoleColor.Red : ConsoleColor.Gray, $"  {d.Driver,-28} DPC {d.MaxDpcUs,8:0} мкс   ISR {d.MaxIsrUs,8:0} мкс\n");
        }
        Output.Header("Точность таймера (Sleep(1))");
        Console.WriteLine($"  среднее {timer.MeanMs:0.###} мс, p99 {timer.P99Ms:0.###} мс, максимум {timer.MaxMs:0.###} мс" +
                          (timer.CurrentResolutionMs is { } res ? $", текущее разрешение таймера {res:0.###} мс" : ""));
        Output.PrintFindings(DetectorCatalog.RunAll(new SystemProfile { Dpc = dpc }, new[] { new DpcLatencyDetector() }));
        return 0;
    }

    public static async Task<int> Net(Args a)
    {
        var target = a.Get("target") ?? OptiSettings.Load().NetworkTarget;
        Console.WriteLine($"Пинг до {target} в простое{(a.Has("no-load") ? "" : " и под нагрузкой (скачивание ~200 МБ)")}…");
        var q = await NetworkTest.RunAsync(target, a.Has("no-load") ? null : NetworkTest.DefaultLoadUrl);
        Console.WriteLine($"  В простое: {q.IdlePingMs:0.#} мс, джиттер {q.IdleJitterMs:0.#} мс, потери {q.PacketLossPercent:0.#} %");
        if (q.LoadedPingMs is { } l) Console.WriteLine($"  Под нагрузкой: {l:0.#} мс (+{l - q.IdlePingMs:0.#} мс)");
        Output.PrintFindings(DetectorCatalog.RunAll(new SystemProfile { NetQuality = q }, new[] { new BufferbloatDetector() }));
        return 0;
    }

    public static async Task<int> Version(Args a)
    {
        Console.WriteLine($"Opti {AppInfo.Version}");
        if (OptiSettings.Load().CheckForUpdates && !a.Has("offline"))
        {
            var u = await UpdateChecker.CheckAsync(AppInfo.Version);
            if (u is null) Console.WriteLine("Не удалось проверить обновления.");
            else if (u.IsNewer) Output.Color(ConsoleColor.Green, $"Доступна версия {u.LatestVersion}: {u.Url}\n");
            else Console.WriteLine("Установлена последняя версия.");
        }
        return 0;
    }
}
