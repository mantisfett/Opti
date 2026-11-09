using Opti.Core.Localization;
namespace Opti.Fixes.Backends;

/// <summary>Форматы адресов настроек (поле ChangeRecord.Target).</summary>
public static class Targets
{
    public const char Sep = '|';

    /// <summary>Реестр: «HKLM\path\key|ValueName».</summary>
    public static string Reg(string keyPath, string valueName) => keyPath + Sep + valueName;
    /// <summary>BCD: «{current}|element».</summary>
    public static string Bcd(string element, string entry = "{current}") => entry + Sep + element;
    /// <summary>Активная схема питания.</summary>
    public const string ActiveScheme = "activescheme";
    /// <summary>Режим питания Windows (ползунок) для текущего источника питания; значение — GUID режима.</summary>
    public const string PowerOverlay = "overlay";
    /// <summary>Параметр схемы питания: «scheme|subgroup|setting|AC».</summary>
    public static string PowerAc(string scheme, string subgroup, string setting) => string.Join(Sep, scheme, subgroup, setting, "AC");
    /// <summary>Параметр сетевого адаптера: «adapterName|AllowComputerToTurnOffDevice».</summary>
    public static string NetAdapter(string adapter, string property) => adapter + Sep + property;
    /// <summary>DNS-серверы адаптера (IPv4): «Имя адаптера|dns». Значение — адреса через запятую; нет значения — автоматически (DHCP).</summary>
    public static string Dns(string adapter) => adapter + Sep + "dns";
    /// <summary>Частота экрана: «\\.\DISPLAY1|refresh».</summary>
    public static string DisplayRefresh(string device) => device + Sep + "refresh";

    public static (string Left, string Right) Split2(string target)
    {
        int i = target.LastIndexOf(Sep);
        if (i < 0) throw new FormatException(Loc.T($"Неверный адрес настройки: {target}"));
        return (target[..i], target[(i + 1)..]);
    }
}
