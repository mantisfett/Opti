using System.Runtime.Versioning;
using Opti.Analysis;
using Opti.Core.Json;
using Opti.Core.Profile;
using Opti.Fixes.Backends;
using Opti.Fixes.Journal;
using Opti.Fixes.Services;
using Opti.Workflow;
using Opti.Workflow.Sessions;
using Opti.Core.Localization;

namespace Opti.Cli;

/// <summary>Сборка рабочего окружения CLI: настройки, журнал, бэкенды Windows, сессии.</summary>
[SupportedOSPlatform("windows")]
internal sealed class CliHost : IDisposable
{
    public OptiSettings Settings { get; } = OptiSettings.Load();
    public SqliteChangeJournal Journal { get; } = new(OptiPaths.JournalPath);
    public TuneWorkflow Workflow { get; }

    public CliHost()
    {
        Workflow = new TuneWorkflow(Settings, new SessionStore(), WindowsBackends.Create(), Journal,
            new WindowsRestorePointService(), new WindowsResumeScheduler(), new WindowsCrashEventSource());
    }

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "opti-cli.exe");

    public void Dispose() => Journal.Dispose();
}

internal static class AnalysisLoader
{
    public static AnalysisResult LoadOrCollect(Args a)
    {
        if (a.Get("profile") is { } path)
        {
            var text = File.ReadAllText(path);
            // Файл отчёта analyze содержит { profile, findings } — поддерживаем оба формата
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var profileJson = doc.RootElement.TryGetProperty("profile", out var p) ? p.GetRawText() : text;
            return SystemAnalyzer.FromProfile(OptiJson.Deserialize<SystemProfile>(profileJson));
        }
        Console.Write(Loc.T("Анализ системы: "));
        var r = SystemAnalyzer.Collect(SystemAnalyzer.DefaultCollectors(Opti.Workflow.OptiSettings.Load().ObsConfigDir), name => Console.Write(name + "… "));
        Console.WriteLine(Loc.T("готово."));
        return r;
    }
}
