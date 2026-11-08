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

/// <summary>
/// DNS-серверы адаптера. Читает именно заданные вручную серверы из реестра (NameServer): у адаптера с DHCP значения нет —
/// это «автоматически», и откат возвращает автоматический режим, а не адреса роутера, записанные как статические.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDnsBackend : IStateBackend
{
    private readonly ProcessRunner _runner;
    public WindowsDnsBackend(ProcessRunner? runner = null) => _runner = runner ?? ProcessRunner.Instance;
    public ChangeType Type => ChangeType.Dns;

    public StateValue Read(string target)
    {
        var (name, _) = Targets.Split2(target);
        var script = $"$g = (Get-NetAdapter -Name {ProcessRunner.PsQuote(name)}).InterfaceGuid;" +
                     "(Get-ItemProperty -Path \"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\$g\" -Name NameServer -ErrorAction SilentlyContinue).NameServer";
        var r = _runner.RunPowerShell(script);
        if (!r.Ok) throw new IOException(r.StdErr.Trim());
        var v = r.StdOut.Trim().Replace(' ', ',');
        return v.Length == 0 ? StateValue.Missing : StateValue.Str(v);
    }

    public void Write(string target, StateValue value)
    {
        var (name, _) = Targets.Split2(target);
        string cmd;
        if (!value.Exists || string.IsNullOrWhiteSpace(value.Value))
            cmd = $"Set-DnsClientServerAddress -InterfaceAlias {ProcessRunner.PsQuote(name)} -ResetServerAddresses";
        else
        {
            var addrs = value.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // Только IPv4-адреса: произвольный текст в команду не попадёт
            if (addrs.Length is 0 or > 4 || addrs.Any(a => !System.Net.IPAddress.TryParse(a, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
                throw new ArgumentException(Loc.T($"Недопустимый адрес DNS: {value.Value}"));
            cmd = $"Set-DnsClientServerAddress -InterfaceAlias {ProcessRunner.PsQuote(name)} -ServerAddresses ({string.Join(",", addrs.Select(ProcessRunner.PsQuote))})";
        }
        var r = _runner.RunPowerShell(cmd + "; Clear-DnsClientCache");
        if (!r.Ok) throw new IOException(r.StdErr.Trim());
    }
}
