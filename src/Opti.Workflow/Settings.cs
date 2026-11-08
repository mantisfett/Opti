using Opti.Core.Json;
using Opti.Core.Strategy;
using Opti.Fixes.Backends;

namespace Opti.Workflow;

/// <summary>Настройки программы (C:\ProgramData\Opti\settings.json).</summary>
public sealed class OptiSettings
{
    public UserGoal DefaultGoal { get; set; } = UserGoal.Balanced;
    /// <summary>Исправления, которые никогда не предлагать.</summary>
    public List<string> ExcludedFixIds { get; set; } = new();
    /// <summary>Свой путь к папке конфигурации OBS (если OBS портативный).</summary>
    public string? ObsConfigDir { get; set; }
    public int MeasureRuns { get; set; } = 3;
    public int MeasureDurationSec { get; set; } = 60;
    public int MeasureWarmupSec { get; set; } = 10;
    /// <summary>Длительность прогона синтетического теста: сцена одинаковая, поэтому хватает 30 секунд.</summary>
    public int SyntheticDurationSec { get; set; } = 30;
    /// <summary>Путь к PresentMon.exe. Если пусто — ищется рядом с программой.</summary>
    public string? PresentMonPath { get; set; }
    public string NetworkTarget { get; set; } = "1.1.1.1";
    /// <summary>Откатывать изменения, если замер не показал улучшения (принцип 2 ТЗ). Иначе — только пометка.</summary>
    public bool RollbackWhenNoEffect { get; set; }
    /// <summary>Проверять наличие новой версии на GitHub (только уведомление, ничего не скачивается).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>При запуске проверять, не вернули ли Windows или драйверы настройки, изменённые Opti.</summary>
    public bool VerifyOnStartup { get; set; } = true;
    /// <summary>Язык интерфейса: "auto" (по Windows), "ru" или "en". Вступает в силу после перезапуска.</summary>
    public string Language { get; set; } = "auto";

    public static OptiSettings Load(string? path = null)
    {
        path ??= OptiPaths.SettingsPath;
        try { return File.Exists(path) ? OptiJson.Deserialize<OptiSettings>(File.ReadAllText(path)) : new(); }
        catch { return new(); }
    }

    public void Save(string? path = null)
    {
        path ??= OptiPaths.SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, OptiJson.Serialize(this));
    }
}
