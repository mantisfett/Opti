using System.Globalization;
using System.Runtime.Versioning;
using Opti.Core.Fixes;
using Microsoft.Win32;
using Opti.Core.Localization;

namespace Opti.Fixes.Backends;

/// <summary>Реестр Windows (64-битное представление). Target: «HKLM\path|ValueName».</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryBackend : IStateBackend
{
    public ChangeType Type => ChangeType.Registry;

    public StateValue Read(string target)
    {
        var (keyPath, name) = Targets.Split2(target);
        using var key = OpenKey(keyPath, writable: false, create: false);
        if (key is null) return StateValue.Missing;
        var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null) return StateValue.Missing;
        return ToState(key.GetValueKind(name), raw);
    }

    public void Write(string target, StateValue value)
    {
        var (keyPath, name) = Targets.Split2(target);
        if (!value.Exists)
        {
            using (var k = OpenKey(keyPath, writable: true, create: false))
            {
                if (k is null) return;
                k.DeleteValue(name, throwOnMissingValue: false);
            }
            // Значение по умолчанию («») само по себе создаёт смысл ключа (например, переопределение COM-класса
            // для классического контекстного меню). При откате удаляем ключ, если он остался пустым, и его пустого родителя.
            if (name.Length == 0) PruneEmptyKey(keyPath, levels: 2);
            return;
        }
        using var key = OpenKey(keyPath, writable: true, create: true)
                        ?? throw new IOException(Loc.T($"Не удалось открыть ключ {keyPath}"));
        var (kind, data) = FromState(value);
        key.SetValue(name, data, kind);
    }

    public static StateValue ToState(RegistryValueKind kind, object raw) => kind switch
    {
        RegistryValueKind.DWord => new StateValue(true, "DWord", unchecked((uint)(int)raw).ToString(CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => new StateValue(true, "QWord", unchecked((ulong)(long)raw).ToString(CultureInfo.InvariantCulture)),
        RegistryValueKind.String => new StateValue(true, "String", (string)raw),
        RegistryValueKind.ExpandString => new StateValue(true, "ExpandString", (string)raw),
        RegistryValueKind.MultiString => new StateValue(true, "MultiString", string.Join('\0', (string[])raw)),
        RegistryValueKind.Binary => new StateValue(true, "Binary", Convert.ToHexString((byte[])raw)),
        _ => new StateValue(true, kind.ToString(), raw.ToString()),
    };

    public static (RegistryValueKind Kind, object Data) FromState(StateValue v) => v.Kind switch
    {
        "DWord" => (RegistryValueKind.DWord, unchecked((int)uint.Parse(v.Value!, CultureInfo.InvariantCulture))),
        "QWord" => (RegistryValueKind.QWord, unchecked((long)ulong.Parse(v.Value!, CultureInfo.InvariantCulture))),
        "String" => (RegistryValueKind.String, v.Value ?? ""),
        "ExpandString" => (RegistryValueKind.ExpandString, v.Value ?? ""),
        "MultiString" => (RegistryValueKind.MultiString, (v.Value ?? "").Split('\0')),
        "Binary" => (RegistryValueKind.Binary, Convert.FromHexString(v.Value ?? "")),
        _ => throw new NotSupportedException(Loc.T($"Тип значения реестра {v.Kind} не поддерживается")),
    };

    private static void PruneEmptyKey(string keyPath, int levels)
    {
        for (int i = 0; i < levels; i++)
        {
            int sep = keyPath.LastIndexOf('\\');
            if (sep < 0 || keyPath.Count(c => c == '\\') < 3) return; // не трогаем ключи верхних уровней
            using (var k = OpenKey(keyPath, writable: false, create: false))
            {
                if (k is null || k.ValueCount > 0 || k.SubKeyCount > 0) return;
            }
            using (var parent = OpenKey(keyPath[..sep], writable: true, create: false))
                parent?.DeleteSubKey(keyPath[(sep + 1)..], throwOnMissingSubKey: false);
            keyPath = keyPath[..sep];
        }
    }

    private static RegistryKey? OpenKey(string path, bool writable, bool create)
    {
        int i = path.IndexOf('\\');
        string hive = i < 0 ? path : path[..i];
        string sub = i < 0 ? "" : path[(i + 1)..];
        var root = hive.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
            "HKCU" or "HKEY_CURRENT_USER" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
            "HKU" or "HKEY_USERS" => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64),
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64),
            _ => throw new ArgumentException(Loc.T($"Неизвестный раздел реестра: {hive}")),
        };
        using (root)
            return create ? root.CreateSubKey(sub, writable) : root.OpenSubKey(sub, writable);
    }
}
