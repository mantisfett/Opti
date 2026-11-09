using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Opti.Core.Fixes;
using Opti.Native;
using Opti.Core.Localization;

namespace Opti.Fixes.Backends;

/// <summary>
/// Питание через powercfg. Targets: «activescheme» (значение — GUID схемы) или
/// «scheme|subgroup|setting|AC» (значение — индекс, десятичный).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsPowerBackend : IStateBackend
{
    private readonly ProcessRunner _runner;
    public WindowsPowerBackend(ProcessRunner? runner = null) => _runner = runner ?? ProcessRunner.Instance;
    public ChangeType Type => ChangeType.PowerSetting;

    public StateValue Read(string target)
    {
        if (target == Targets.ActiveScheme)
            return StateValue.Str(GetActiveScheme());
        if (target == Targets.PowerOverlay)
            return PowerNative.GetOverlayGuid() is { } g ? StateValue.Str(g.ToString()) : StateValue.Missing;

        var (scheme, sub, setting) = ParseSetting(target);
        var r = _runner.RunConsoleUtf8($"powercfg /query {scheme} {sub} {setting}");
        if (!r.Ok) return StateValue.Missing; // параметр отсутствует на этой системе
        var (ac, _) = ParseQueryIndexes(r.StdOut);
        return ac is null ? StateValue.Missing : StateValue.Str(ac.Value.ToString(CultureInfo.InvariantCulture));
    }

    public void Write(string target, StateValue value)
    {
        if (target == Targets.ActiveScheme)
        {
            var guid = Guid.Parse(value.Value ?? throw new ArgumentException(Loc.T("Нужен GUID схемы"))).ToString();
            Check(_runner.RunConsoleUtf8($"powercfg /setactive {guid}"), "powercfg /setactive");
            return;
        }
        if (target == Targets.PowerOverlay)
        {
            if (!value.Exists) return;
            if (PowerNative.GetStatus().OnAc == false)
                throw new IOException(Loc.T("Компьютер работает от батареи — режим питания для сети сейчас изменить нельзя. Подключите зарядку и повторите."));
            PowerNative.SetOverlay(Guid.Parse(value.Value!));
            return;
        }
        var (scheme, sub, setting) = ParseSetting(target);
        if (!value.Exists) return; // у параметров схемы нет «отсутствия», откат пишет исходный индекс
        uint idx = uint.Parse(value.Value!, CultureInfo.InvariantCulture);
        Check(_runner.RunConsoleUtf8($"powercfg /setacvalueindex {scheme} {sub} {setting} {idx}"), "powercfg /setacvalueindex");
        if (string.Equals(GetActiveScheme(), scheme, StringComparison.OrdinalIgnoreCase))
            Check(_runner.RunConsoleUtf8($"powercfg /setactive {scheme}"), "powercfg /setactive");
    }

    public string GetActiveScheme()
    {
        var r = _runner.RunConsoleUtf8("powercfg /getactivescheme");
        Check(r, "powercfg /getactivescheme");
        var m = GuidRegex().Match(r.StdOut);
        if (!m.Success) throw new IOException(Loc.T("Не удалось определить активную схему питания"));
        return m.Value.ToLowerInvariant();
    }

    public static (uint? Ac, uint? Dc) ParseQueryIndexes(string output) => ToolParsers.ParsePowercfgIndexes(output);

    private static (string, string, string) ParseSetting(string target)
    {
        var parts = target.Split(Targets.Sep);
        if (parts.Length != 4 || parts[3] != "AC") throw new FormatException(Loc.T($"Неверный адрес параметра питания: {target}"));
        foreach (var p in parts.Take(3))
            if (!Guid.TryParse(p, out _)) throw new FormatException(Loc.T($"Ожидался GUID: {p}"));
        return (parts[0], parts[1], parts[2]);
    }

    private static void Check(ProcessResult r, string what)
    {
        if (!r.Ok) throw new IOException($"{what}: {r.StdOut}{r.StdErr}".Trim());
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")] private static partial Regex GuidRegex();
}
