using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Opti.Core.Fixes;
using Opti.Native;
using Opti.Core.Localization;

namespace Opti.Fixes.Backends;

/// <summary>
/// BCD через bcdedit. Target: «{current}|useplatformclock».
/// Имена элементов bcdedit выводит по-английски и в локализованной Windows; логические значения нормализуются в Yes/No.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsBcdBackend : IStateBackend
{
    private readonly ProcessRunner _runner;
    public WindowsBcdBackend(ProcessRunner? runner = null) => _runner = runner ?? ProcessRunner.Instance;
    public ChangeType Type => ChangeType.Bcd;

    public StateValue Read(string target)
    {
        var (entry, element) = Targets.Split2(target);
        var r = _runner.RunConsoleUtf8($"bcdedit /enum {entry}");
        if (!r.Ok) throw new IOException($"bcdedit /enum: {r.StdOut}{r.StdErr}".Trim());
        var values = ParseEnum(r.StdOut);
        return values.TryGetValue(element, out var v) ? StateValue.Str(v) : StateValue.Missing;
    }

    public void Write(string target, StateValue value)
    {
        var (entry, element) = Targets.Split2(target);
        if (!ElementRegex().IsMatch(element)) throw new ArgumentException(Loc.T($"Недопустимое имя элемента BCD: {element}"));
        string cmd = value.Exists
            ? $"bcdedit /set {entry} {element} {SanitizeValue(value.Value)}"
            : $"bcdedit /deletevalue {entry} {element}";
        var r = _runner.RunConsoleUtf8(cmd);
        if (!r.Ok) throw new IOException($"{cmd}: {r.StdOut}{r.StdErr}".Trim());
    }

    public static Dictionary<string, string> ParseEnum(string output) => ToolParsers.ParseBcdEnum(output);

    private static string SanitizeValue(string? v)
    {
        v ??= "";
        if (!ValueRegex().IsMatch(v)) throw new ArgumentException(Loc.T($"Недопустимое значение BCD: {v}"));
        return v;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9]+$")] private static partial Regex ElementRegex();
    [GeneratedRegex(@"^[A-Za-z0-9{}\-]+$")] private static partial Regex ValueRegex();
}
