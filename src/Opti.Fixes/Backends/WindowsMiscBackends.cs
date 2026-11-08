using System.Runtime.Versioning;
using Opti.Core.Fixes;
using Opti.Native;
using Opti.Core.Localization;

namespace Opti.Fixes.Backends;

/// <summary>Частота экрана. Target: «\\.\DISPLAY1|refresh», значение — Гц.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDisplayBackend : IStateBackend
{
    public ChangeType Type => ChangeType.Display;

    public StateValue Read(string target)
    {
        var (device, prop) = Targets.Split2(target);
        if (prop != "refresh") throw new NotSupportedException(prop);
        var mode = DisplayNative.GetCurrentMode(device) ?? throw new IOException(Loc.T($"Экран {device} не найден"));
        return StateValue.Str(mode.Hz.ToString());
    }

    public void Write(string target, StateValue value)
    {
        var (device, _) = Targets.Split2(target);
        if (!value.Exists) return;
        DisplayNative.SetRefreshRate(device, int.Parse(value.Value!));
    }
}

/// <summary>Управление питанием сетевого адаптера через NetAdapter (PowerShell). Target: «Имя|AllowComputerToTurnOffDevice».</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNetAdapterBackend : IStateBackend
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
        { "AllowComputerToTurnOffDevice", "SelectiveSuspend", "WakeOnMagicPacket", "WakeOnPattern" };

    private readonly ProcessRunner _runner;
    public WindowsNetAdapterBackend(ProcessRunner? runner = null) => _runner = runner ?? ProcessRunner.Instance;
    public ChangeType Type => ChangeType.NetAdapter;

    public StateValue Read(string target)
    {
        var (name, prop) = Split(target);
        var r = _runner.RunPowerShell($"(Get-NetAdapterPowerManagement -Name {ProcessRunner.PsQuote(name)}).{prop}");
        if (!r.Ok) throw new IOException(r.StdErr.Trim());
        var v = r.StdOut.Trim();
        return v.Length == 0 ? StateValue.Missing : StateValue.Str(v);
    }

    public void Write(string target, StateValue value)
    {
        var (name, prop) = Split(target);
        if (!value.Exists || value.Value is not ("Enabled" or "Disabled")) return;
        var r = _runner.RunPowerShell($"Set-NetAdapterPowerManagement -Name {ProcessRunner.PsQuote(name)} -{prop} {value.Value} -NoRestart");
        if (!r.Ok) throw new IOException(r.StdErr.Trim());
    }

    private static (string, string) Split(string target)
    {
        var (name, prop) = Targets.Split2(target);
        if (!Allowed.Contains(prop)) throw new NotSupportedException(Loc.T($"Свойство адаптера {prop} не поддерживается"));
        return (name, prop);
    }
}
