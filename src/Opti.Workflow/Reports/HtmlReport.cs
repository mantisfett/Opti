using System.Globalization;
using System.Net;
using System.Text;
using Opti.Core.Findings;
using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Core.Strategy;
using Opti.Measure.Stats;
using Opti.Workflow.Sessions;
using Opti.Core.Localization;

namespace Opti.Workflow.Reports;

/// <summary>Данные для отчёта. Всё, кроме сессии, необязательно — отчёт строится из того, что есть.</summary>
public sealed class ReportData
{
    public required TuneSession Session { get; init; }
    public ComparisonResult? Comparison { get; init; }
    public string? CompareMessage { get; init; }
    public IReadOnlyList<ChangeRecord> Changes { get; init; } = Array.Empty<ChangeRecord>();
    public IReadOnlyList<IFix> Catalog { get; init; } = Array.Empty<IFix>();
    public string AppVersion { get; init; } = "";
}

/// <summary>Самодостаточный HTML-отчёт (один файл, без внешних ресурсов), светлая и тёмная темы.</summary>
public static class HtmlReport
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string F(double v, string fmt = "0.#") => double.IsNaN(v) ? "—" : v.ToString(fmt, Opti.Core.Localization.Loc.Culture);

    public static string Build(ReportData d)
    {
        var s = d.Session;
        var p = s.Profile ?? new SystemProfile();
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append(Loc.T($"<title>Отчёт Opti</title><style>{Css}</style></head><body><main>"));

        // ---------- Шапка ----------
        var verdictBadge = s.Outcome switch
        {
            SessionOutcome.Improved => Loc.T("<span class=\"badge good\">Стало лучше</span>"),
            SessionOutcome.Worse => Loc.T("<span class=\"badge bad\">Стало хуже</span>"),
            SessionOutcome.NoEffect => Loc.T("<span class=\"badge neutral\">Без изменений</span>"),
            SessionOutcome.RolledBack => Loc.T("<span class=\"badge neutral\">Изменения откачены</span>"),
            _ => "",
        };
        sb.Append(Loc.T($"<header><div><h1>Отчёт Opti</h1><p class=\"muted\">{E(p.Device.Manufacturer)} {E(p.Device.Model)} · {E(DeviceClassification.From(p).Describe())}</p>"));
        sb.Append(Loc.T($"<p class=\"muted\">Режим: <b>{E(s.Goal.Ru())}</b> · сессия {E(s.Id)} · {s.CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm}</p></div>{verdictBadge}</header>"));

        if (d.CompareMessage is not null) sb.Append($"<p class=\"lead\">{E(d.CompareMessage)}</p>");

        // ---------- Сравнение ----------
        if (d.Comparison is { } cmp && cmp.Metrics.Count > 0)
        {
            sb.Append(Loc.T("<section><h2>Сравнение «до / после»</h2><div class=\"cards\">"));
            foreach (var m in cmp.Metrics)
            {
                var cls = m.Verdict switch { Verdict.Better => "good", Verdict.Worse => "bad", _ => "neutral" };
                var word = m.Verdict switch { Verdict.Better => Loc.T("лучше, достоверно"), Verdict.Worse => Loc.T("хуже, достоверно"), Verdict.NoChange => Loc.T("в пределах шума"), _ => Loc.T("мало данных") };
                sb.Append($"<div class=\"card {cls}\"><div class=\"label\">{E(m.Metric.Name)}</div>");
                sb.Append($"<div class=\"value\">{F(m.Before)} → <b>{F(m.After)}</b> <small>{E(m.Metric.Unit)}</small></div>");
                sb.Append($"<div class=\"delta\">{(m.DeltaPercent >= 0 ? "+" : "")}{F(m.DeltaPercent)} % · {word}</div>");
                sb.Append(Loc.T($"<div class=\"ci\">95 % ДИ: {F(m.BeforeCi.Low, "0.##")}–{F(m.BeforeCi.High, "0.##")} → {F(m.AfterCi.Low, "0.##")}–{F(m.AfterCi.High, "0.##")} · разность {F(m.DiffCi.Low, "+0.##;−0.##;0")}…{F(m.DiffCi.High, "+0.##;−0.##;0")}</div></div>"));
            }
            sb.Append("</div>");
            foreach (var w in cmp.Warnings) sb.Append($"<p class=\"warn\">⚠ {E(w)}</p>");
            sb.Append("</section>");
        }

        // ---------- Графики ----------
        var b = Representative(s.Before);
        var a = Representative(s.After);
        if (b is not null || a is not null)
        {
            var series = new List<ChartSeries>();
            if (b is not null) series.Add(new ChartSeries(Loc.T("До: ") + b.Label, "--before", b.FrameTimesMs));
            if (a is not null) series.Add(new ChartSeries(Loc.T("После: ") + a.Label, "--after", a.FrameTimesMs));
            sb.Append(Loc.T("<section><h2>Фреймтайм</h2>"));
            sb.Append(Legend(series));
            sb.Append(SvgCharts.FrameTimeLines(series));
            sb.Append(Loc.T("<p class=\"muted small\">Показан прогон, ближайший к медиане по среднему FPS. Пики вверх — это заметные глазу рывки.</p>"));
            sb.Append(Loc.T("<h2>Распределение фреймтайма</h2>"));
            sb.Append(Legend(series));
            sb.Append(SvgCharts.Histogram(series));
            sb.Append(Loc.T("<p class=\"muted small\">Чем уже и левее «горб», тем ровнее и быстрее кадры.</p></section>"));
        }

        // ---------- Режим вывода ----------
        var modesB = Modes(s.Before);
        var modesA = Modes(s.After);
        if (modesB.Count > 0 || modesA.Count > 0)
        {
            sb.Append(Loc.T("<section><h2>Режим вывода кадров</h2><table><thead><tr><th>Режим</th><th>До</th><th>После</th></tr></thead><tbody>"));
            foreach (var mode in modesB.Keys.Union(modesA.Keys).OrderBy(k => k))
                sb.Append($"<tr><td>{E(mode)}</td><td>{F(modesB.GetValueOrDefault(mode))} %</td><td>{F(modesA.GetValueOrDefault(mode))} %</td></tr>");
            sb.Append(Loc.T("</tbody></table><p class=\"muted small\">«Independent Flip» — кадр идёт на экран напрямую, минимальная задержка. «Composed» — через композитор Windows (оверлеи, окно, захват экрана).</p>"));
            var latB = s.Before.Where(r => r.AvgPcLatencyMs is not null).Select(r => r.AvgPcLatencyMs!.Value).DefaultIfEmpty(double.NaN).Average();
            var latA = s.After.Where(r => r.AvgPcLatencyMs is not null).Select(r => r.AvgPcLatencyMs!.Value).DefaultIfEmpty(double.NaN).Average();
            if (!double.IsNaN(latB) || !double.IsNaN(latA))
                sb.Append(Loc.T($"<p>Оценка задержки ПК: {F(latB)} → <b>{F(latA)}</b> мс</p>"));
            sb.Append("</section>");
        }

        // ---------- A/B ----------
        if (s.AbTests.Count > 0)
        {
            sb.Append(Loc.T("<section><h2>A/B-проверка спорных настроек</h2><table><thead><tr><th>Настройка</th><th>Результат</th><th>Средний FPS A → B</th><th>1 % low A → B</th></tr></thead><tbody>"));
            foreach (var ab in s.AbTests)
            {
                string fps = "—", low = "—";
                if (ab.RunsA.Count > 0 && ab.RunsB.Count > 0)
                {
                    double Avg(List<MeasurementRun> runs, MetricId id) => runs.Select(r => Metrics.Compute(id, r.FrameTimesMs)).Average();
                    fps = $"{F(Avg(ab.RunsA, MetricId.AvgFps))} → {F(Avg(ab.RunsB, MetricId.AvgFps))}";
                    low = $"{F(Avg(ab.RunsA, MetricId.Low1Fps))} → {F(Avg(ab.RunsB, MetricId.Low1Fps))}";
                }
                sb.Append($"<tr><td>{E(Title(d, ab.FixId))}</td><td>{E(ab.Summary ?? ab.Phase.ToString())}</td><td>{fps}</td><td>{low}</td></tr>");
            }
            sb.Append("</tbody></table></section>");
        }

        // ---------- Изменения ----------
        if (d.Changes.Count > 0)
        {
            sb.Append(Loc.T("<section><h2>Что изменено</h2><table><thead><tr><th>Исправление</th><th>Настройка</th><th>Было</th><th>Стало</th><th>Статус</th></tr></thead><tbody>"));
            foreach (var c in d.Changes)
                sb.Append(Loc.T($"<tr><td>{E(Title(d, c.FixId))}</td><td class=\"mono\">{E(c.Type.ToString())} {E(c.Target)}</td><td class=\"mono\">{E(c.Before.ToString())}</td><td class=\"mono\">{E(c.After.ToString())}</td><td>{(c.RolledBack ? "откачено" : "действует")}</td></tr>"));
            sb.Append("</tbody></table>");
            sb.Append(Loc.T($"<p class=\"muted small\">Откат всей сессии: <code>opti-cli rollback --session {E(s.Id)}</code>, одного исправления: <code>opti-cli rollback --fix ID --session {E(s.Id)}</code>, или кнопка «Откатить» в программе.</p></section>"));
        }

        if (s.PostReboot is { } pr)
        {
            sb.Append(Loc.T("<section><h2>Самопроверка после перезагрузки</h2>"));
            sb.Append(Loc.T($"<p>Подтверждено изменений: <b>{pr.Verified}</b>.</p>"));
            foreach (var n in pr.NotApplied) sb.Append(Loc.T($"<p class=\"warn\">Не применилось: {E(n)}</p>"));
            foreach (var c in pr.Crashes) sb.Append(Loc.T($"<p class=\"bad-text\">Сбой системы {c.Time.LocalDateTime:dd.MM HH:mm}: событие {c.EventId} ({E(c.Source)}) {E(c.Description)}</p>"));
            sb.Append("</section>");
        }

        // ---------- Найденные проблемы ----------
        if (s.Findings.Count > 0)
        {
            sb.Append(Loc.T($"<section><h2>Найденные проблемы ({s.Findings.Count})</h2>"));
            foreach (var f in s.Findings.Select(x => x.Translated()))
            {
                var cls = f.Severity switch { Severity.Critical => "crit", Severity.Important => "imp", _ => "adv" };
                var sev = f.Severity switch { Severity.Critical => Loc.T("Критично"), Severity.Important => Loc.T("Важно"), _ => Loc.T("Совет") };
                sb.Append($"<details class=\"finding {cls}\"><summary><span class=\"sev\">{sev}</span> {E(f.Title)}");
                if (f.IsHardwareLimitation) sb.Append(Loc.T(" <span class=\"tag\">железо</span>"));
                sb.Append($"</summary><p>{E(f.Explanation)}</p>");
                if (f.Evidence.Count > 0) sb.Append("<ul class=\"evidence\">" + string.Concat(f.Evidence.Select(e => $"<li>{E(e)}</li>")) + "</ul>");
                if (f.ManualSteps.Count > 0) sb.Append("<ol>" + string.Concat(f.ManualSteps.Select(e => $"<li>{E(e)}</li>")) + "</ol>");
                foreach (var l in f.Links) sb.Append($"<p><a href=\"{E(l)}\" rel=\"noopener\">{E(l)}</a></p>");
                sb.Append("</details>");
            }
            sb.Append("</section>");
        }

        // ---------- Профиль ----------
        sb.Append(Loc.T("<section><h2>Профиль ПК</h2><table class=\"kv\"><tbody>"));
        void Row(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) sb.Append($"<tr><th>{E(k)}</th><td>{E(v)}</td></tr>"); }
        Row("Windows", Loc.T($"{p.Os.ProductName} {p.Os.DisplayVersion} (сборка {p.Os.Build}.{p.Os.Ubr})") + (p.Os.ModifiedBuild is { } mb ? $", {mb}" : ""));
        Row(Loc.T("Процессор"), p.Cpu.Name is null ? null : Loc.T($"{p.Cpu.Name} — {p.Cpu.Cores} ядер / {p.Cpu.Threads} потоков"));
        foreach (var g in p.Gpus) Row(g.IsIntegrated ? Loc.T("Встроенная графика") : Loc.T("Видеокарта"), Loc.T($"{g.Name}, драйвер {g.DriverVersion} от {g.DriverDate:dd.MM.yyyy}"));
        foreach (var disp in p.Displays) Row(Loc.T("Экран"), Loc.T($"{disp.FriendlyName ?? disp.DeviceName}: {disp.Width}×{disp.Height} @ {disp.CurrentHz} Гц (макс. {disp.MaxHzAtCurrentResolution})"));
        Row(Loc.T("Память"), p.Memory.TotalMb > 0 ? Loc.T($"{F(p.Memory.TotalMb / 1024.0)} ГБ, модулей: {p.Memory.Modules.Count}") : null);
        if (p.SystemDisk is { } sd) Row(Loc.T("Системный диск"), Loc.T($"{sd.Model} ({sd.MediaType}), свободно {F(sd.FreeGb, "0")} ГБ"));
        Row(Loc.T("Питание"), Loc.T($"{(p.Power.OnAcPower == true ? "от сети" : p.Power.OnAcPower == false ? "от батареи" : "?")}, схема {p.Power.ActiveSchemeName ?? p.Power.ActiveSchemeGuid}"));
        sb.Append("</tbody></table></section>");

        foreach (var n in s.Notes) sb.Append($"<p class=\"muted\">{E(n)}</p>");
        sb.Append(Loc.T($"<footer>Opti {E(d.AppVersion)} · отчёт создан {DateTime.Now:dd.MM.yyyy HH:mm} · все цифры — из реальных замеров на этом ПК</footer>"));
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    /// <summary>Прогон, ближайший к медиане по среднему FPS (надёжные — в приоритете).</summary>
    public static MeasurementRun? Representative(IReadOnlyList<MeasurementRun> runs)
    {
        var pool = runs.Where(r => r.IsReliable).ToList();
        if (pool.Count == 0) pool = runs.Where(r => r.FrameTimesMs.Count > 0).ToList();
        if (pool.Count == 0) return null;
        var ordered = pool.OrderBy(r => Metrics.Compute(MetricId.AvgFps, r.FrameTimesMs)).ToList();
        return ordered[ordered.Count / 2];
    }

    private static Dictionary<string, double> Modes(IReadOnlyList<MeasurementRun> runs)
    {
        var total = new Dictionary<string, int>();
        foreach (var r in runs)
            foreach (var (k, v) in r.PresentModes) total[k] = total.GetValueOrDefault(k) + v;
        int sum = total.Values.Sum();
        return sum == 0 ? new() : total.ToDictionary(kv => kv.Key, kv => kv.Value * 100.0 / sum);
    }

    private static string Title(ReportData d, string fixId) =>
        d.Catalog.FirstOrDefault(f => f.Id.Equals(fixId, StringComparison.OrdinalIgnoreCase))?.Title ?? fixId;

    private static string Legend(IEnumerable<ChartSeries> series) =>
        "<div class=\"legend\">" + string.Concat(series.Select(s => $"<span><i style=\"background:var({s.CssColorVar})\"></i>{E(s.Name)}</span>")) + "</div>";

    private const string Css = """
        :root{--bg:#f6f7f9;--panel:#fff;--text:#16181d;--muted:#5d6470;--border:#e2e5ea;--grid:#e8ebef;
          --good:#1a7f4b;--good-bg:#e6f4ec;--bad:#b3261e;--bad-bg:#fbeaea;--neutral-bg:#eef0f3;--warn:#8a5a00;
          --before:#8a94a6;--after:#2f6fde;--crit:#b3261e;--imp:#b26a00;--adv:#5d6470;}
        @media (prefers-color-scheme:dark){:root:not([data-theme="light"]){--bg:#111317;--panel:#1a1d23;--text:#e8eaee;--muted:#9aa2ae;
          --border:#2b3038;--grid:#262a31;--good:#4cc38a;--good-bg:#173226;--bad:#f2766b;--bad-bg:#3a1d1b;--neutral-bg:#23272e;--warn:#f0b54a;
          --before:#7d8696;--after:#5b9bff;--crit:#f2766b;--imp:#f0b54a;--adv:#9aa2ae;}}
        :root[data-theme="dark"]{--bg:#111317;--panel:#1a1d23;--text:#e8eaee;--muted:#9aa2ae;--border:#2b3038;--grid:#262a31;--good:#4cc38a;
          --good-bg:#173226;--bad:#f2766b;--bad-bg:#3a1d1b;--neutral-bg:#23272e;--warn:#f0b54a;--before:#7d8696;--after:#5b9bff;--crit:#f2766b;--imp:#f0b54a;--adv:#9aa2ae;}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif}
        main{max-width:1040px;margin:0 auto;padding:24px 16px 48px}
        header{display:flex;justify-content:space-between;align-items:flex-start;gap:16px;flex-wrap:wrap}
        h1{margin:0 0 4px;font:600 28px/1.25 "Segoe UI Variable Display","Segoe UI",system-ui,sans-serif}h2{font:600 18px/1.3 "Segoe UI Variable Display","Segoe UI",system-ui,sans-serif;margin:28px 0 12px}
        section{background:var(--panel);border:1px solid var(--border);border-radius:12px;padding:4px 20px 16px;margin-top:16px;overflow-x:auto}
        .muted{color:var(--muted);margin:2px 0}.small{font-size:13px}.lead{font-size:17px;margin:16px 0 0}
        .badge{padding:6px 14px;border-radius:999px;font-weight:600;white-space:nowrap}
        .good{background:var(--good-bg);color:var(--good)}.bad{background:var(--bad-bg);color:var(--bad)}.neutral{background:var(--neutral-bg);color:var(--muted)}
        .cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:12px}
        .card{border-radius:10px;padding:12px 14px;border:1px solid var(--border)}.card .label{font-size:13px;opacity:.85}
        .card .value{font-size:20px;margin:4px 0;color:var(--text)}.card .delta{font-weight:600}.card .ci{font-size:12px;color:var(--muted);margin-top:4px}
        .warn{color:var(--warn)}.bad-text{color:var(--bad)}
        .chart{width:100%;height:auto;display:block}.chart .grid{stroke:var(--grid);stroke-width:1}.chart .axis{fill:var(--muted);font-size:11px}
        .legend{display:flex;gap:16px;flex-wrap:wrap;font-size:13px;color:var(--muted);margin-bottom:6px}
        .legend i{display:inline-block;width:12px;height:12px;border-radius:3px;margin-right:6px;vertical-align:-1px}
        table{border-collapse:collapse;width:100%;font-size:14px}th,td{text-align:left;padding:7px 8px;border-bottom:1px solid var(--border);vertical-align:top}
        th{color:var(--muted);font-weight:600}.kv th{width:200px}.mono{font-family:Consolas,ui-monospace,monospace;font-size:12px;word-break:break-all}
        details.finding{border-left:4px solid var(--adv);padding:6px 12px;margin:8px 0;background:var(--neutral-bg);border-radius:6px}
        details.crit{border-color:var(--crit)}details.imp{border-color:var(--imp)}
        summary{cursor:pointer;font-weight:600}.sev{font-size:12px;text-transform:uppercase;letter-spacing:.04em;color:var(--muted);margin-right:6px}
        .crit .sev{color:var(--crit)}.imp .sev{color:var(--imp)}.tag{font-size:11px;padding:1px 8px;border-radius:999px;background:var(--panel);border:1px solid var(--border);color:var(--muted);font-weight:500}
        .evidence{color:var(--muted);font-size:13px}code{font-family:Consolas,ui-monospace,monospace;background:var(--neutral-bg);padding:1px 5px;border-radius:4px}
        footer{margin-top:24px;color:var(--muted);font-size:12px;text-align:center}
        a{color:var(--after)}
        """;
}
