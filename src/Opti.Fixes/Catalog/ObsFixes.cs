using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Fixes.Backends;
using Opti.Core.Localization;

namespace Opti.Fixes.Catalog;

/// <summary>Заменяет источники «Захват экрана» на «Захват игры» в коллекциях сцен OBS (с резервной копией).</summary>
public sealed class ObsGameCaptureFix : FixBase
{
    public override string Id => FixIds.ObsGameCapture;
    public override string Title => Loc.T("OBS: «Захват экрана» → «Захват игры»");
    public override string Explanation => Loc.T("Меняет тип источника в сценах OBS на «Захват игры» (режим: любое полноэкранное приложение). Имя источника, фильтры и положение сохраняются. ") +
                                          Loc.T("Обратите внимание: «Захват игры» показывает только игру, а не рабочий стол. Перед изменением OBS нужно закрыть.");
    public override RiskLevel Risk => RiskLevel.Moderate;
    public override FixCategory Category => FixCategory.Obs;

    public override bool IsApplicable(SystemProfile p) => p.Obs is { Installed: true } && p.Hybrid.IsHybrid;
    public override bool IsAlreadyApplied(SystemProfile p) => p.Obs?.UsesDisplayCapture != true;

    protected override void Execute(ChangeContext ctx)
    {
        if (OperatingSystem.IsWindows() && Process.GetProcessesByName("obs64").Length > 0)
            throw new ChangeAbortedException(Loc.T("OBS запущен — закройте его, иначе при выходе он перезапишет сцены."));
        foreach (var file in ctx.Profile.Obs!.SceneFilesWithDisplayCapture)
        {
            var before = ctx.Read(ChangeType.File, file);
            if (!before.Exists) continue;
            var json = FileBackend.ReadText(before);
            var (updated, count) = ReplaceDisplayCapture(json);
            if (count > 0) ctx.Set(ChangeType.File, file, new StateValue(true, "Text", updated));
        }
    }

    public static (string Json, int Replaced) ReplaceDisplayCapture(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidDataException(Loc.T("Пустой файл сцен OBS"));
        int n = 0;
        if (root["sources"] is JsonArray sources)
        {
            foreach (var s in sources.OfType<JsonObject>())
            {
                if (s["id"]?.GetValue<string>() != "monitor_capture") continue;
                s["id"] = "game_capture";
                s["versioned_id"] = "game_capture";
                s["settings"] = new JsonObject { ["capture_mode"] = "any_fullscreen", ["capture_cursor"] = true };
                n++;
            }
        }
        // OBS хранит сцены в UTF-8 без \u-экранирования — сохраняем так же, чтобы имена остались читаемыми.
        var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return (root.ToJsonString(opts), n);
    }
}

/// <summary>Убирает флаг совместимости RUNASADMIN у obs64.exe (HKCU и HKLM).</summary>
public sealed class ObsNoAdminFix : FixBase
{
    public const string LayersKeyHkcu = @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    public const string LayersKeyHklm = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    public override string Id => FixIds.ObsNoAdmin;
    public override string Title => Loc.T("OBS: не запускать от администратора");
    public override string Explanation => Loc.T("Снимает галочку «Запускать от имени администратора» для OBS. Остальные флаги совместимости сохраняются.");
    public override FixCategory Category => FixCategory.Obs;

    public override bool IsApplicable(SystemProfile p) => p.Obs is { Installed: true, ExePath: not null };
    public override bool IsAlreadyApplied(SystemProfile p) => p.Obs?.RunsAsAdmin != true;

    protected override void Execute(ChangeContext ctx)
    {
        var exe = ctx.Profile.Obs!.ExePath!;
        foreach (var key in new[] { LayersKeyHkcu, LayersKeyHklm })
        {
            var target = Targets.Reg(key, exe);
            var cur = ctx.Read(ChangeType.Registry, target);
            if (!cur.Exists || cur.Value is null) continue;
            var cleaned = RemoveRunAsAdmin(cur.Value);
            if (cleaned == cur.Value) continue;
            if (cleaned is null) ctx.Delete(ChangeType.Registry, target);
            else ctx.Set(ChangeType.Registry, target, cur with { Value = cleaned });
        }
    }

    /// <summary>«~ RUNASADMIN HIGHDPIAWARE» → «~ HIGHDPIAWARE»; если флагов не осталось — null.</summary>
    public static string? RemoveRunAsAdmin(string flags)
    {
        var parts = flags.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!parts.Remove("RUNASADMIN")) return flags;
        var rest = parts.Where(p => p != "~").ToList();
        if (rest.Count == 0) return null;
        return string.Join(' ', parts);
    }
}
