using System.Text.RegularExpressions;
using Opti.Core.Findings;
using Opti.Core.Profile;
using Opti.Core.Localization;

namespace Opti.Core.Drivers;

public enum DriverStatus { Ok, Old, Generic, Missing }

/// <summary>Куда идти за драйвером: официальный сайт производителя чипа или устройства.</summary>
public sealed record DriverLink(string Title, string Url);

/// <summary>
/// Оценка драйверов и ссылки на официальные страницы загрузки. Онлайн-проверки версий нет:
/// Opti не скачивает драйверы и не ходит в сеть без команды — только говорит, где взять свежий.
/// </summary>
public static partial class DriverAdvisor
{
    /// <summary>Возраст, после которого драйвер видеокарты стоит обновить (новые игры, исправления).</summary>
    public static readonly TimeSpan GpuMaxAge = TimeSpan.FromDays(548);
    /// <summary>Сеть, звук, Bluetooth, чипсет обновляются реже — тревожимся после трёх лет.</summary>
    public static readonly TimeSpan OtherMaxAge = TimeSpan.FromDays(3 * 365);

    public static readonly DriverCategory[] KeyCategories =
        { DriverCategory.Display, DriverCategory.Network, DriverCategory.Audio, DriverCategory.Chipset, DriverCategory.Bluetooth, DriverCategory.Storage };

    public static DriverStatus Evaluate(DriverInfo d, DateTimeOffset now)
    {
        if (d.Category == DriverCategory.Display && (d.DeviceName.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
            || d.DeviceName.Contains(Loc.T("Базовый видеоадаптер"), StringComparison.OrdinalIgnoreCase))) return DriverStatus.Missing;
        // Стандартный драйвер Microsoft для сети/звука/Bluetooth работает, но без функций и исправлений производителя
        if (d.IsGenericMicrosoft && d.Category is DriverCategory.Network or DriverCategory.Audio or DriverCategory.Bluetooth && VendorOf(d) != ChipVendor.Unknown)
            return DriverStatus.Generic;
        var maxAge = d.Category == DriverCategory.Display ? GpuMaxAge : OtherMaxAge;
        // Даты 2006/1968 у встроенных драйверов Windows условные — по ним возраст не оценить
        if (d.Date is { } date && date.Year > 2008 && now - date > maxAge && !d.IsGenericMicrosoft) return DriverStatus.Old;
        return DriverStatus.Ok;
    }

    public enum ChipVendor { Unknown, Nvidia, Amd, Intel, Realtek, Qualcomm, MediaTek, Broadcom, Samsung, Asmedia, Marvell }

    [GeneratedRegex(@"(?:VEN|VID)_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VendorIdRx();

    public static ChipVendor VendorOf(DriverInfo d)
    {
        var m = VendorIdRx().Match(d.HardwareId ?? "");
        if (m.Success)
        {
            switch (m.Groups[1].Value.ToUpperInvariant())
            {
                case "10DE": return ChipVendor.Nvidia;
                case "1002": case "1022": return ChipVendor.Amd;
                case "8086": case "8087": return ChipVendor.Intel;
                case "10EC": case "0BDA": return ChipVendor.Realtek;
                case "168C": case "17CB": case "1969": case "0CF3": return ChipVendor.Qualcomm;
                case "14C3": case "0E8D": return ChipVendor.MediaTek;
                case "14E4": case "0A5C": return ChipVendor.Broadcom;
                case "144D": return ChipVendor.Samsung;
                case "1B21": return ChipVendor.Asmedia;
                case "1B4B": case "11AB": return ChipVendor.Marvell;
            }
        }
        var text = (d.Manufacturer + " " + d.DeviceName).ToLowerInvariant();
        if (text.Contains("nvidia")) return ChipVendor.Nvidia;
        if (text.Contains("amd") || text.Contains("radeon") || text.Contains("advanced micro")) return ChipVendor.Amd;
        if (text.Contains("intel")) return ChipVendor.Intel;
        if (text.Contains("realtek")) return ChipVendor.Realtek;
        if (text.Contains("qualcomm") || text.Contains("killer") || text.Contains("atheros")) return ChipVendor.Qualcomm;
        if (text.Contains("mediatek")) return ChipVendor.MediaTek;
        if (text.Contains("broadcom")) return ChipVendor.Broadcom;
        if (text.Contains("samsung")) return ChipVendor.Samsung;
        return ChipVendor.Unknown;
    }

    /// <summary>Официальная страница поддержки производителя ПК / ноутбука / материнской платы.</summary>
    public static DriverLink? OemSupport(DeviceInfo dev)
    {
        // Для ноутбука важен производитель ноутбука, для настольного — материнской платы
        var name = ((dev.IsLaptop ? dev.Manufacturer : dev.BoardManufacturer ?? dev.Manufacturer) ?? "").ToLowerInvariant();
        if (name.Contains("asus")) return new(Loc.T("Поддержка ASUS"), "https://www.asus.com/support/download-center/");
        if (name.Contains("micro-star") || name.Contains("msi")) return new(Loc.T("Поддержка MSI"), "https://www.msi.com/support/download");
        if (name.Contains("gigabyte") || name.Contains("aorus")) return new(Loc.T("Поддержка GIGABYTE"), "https://www.gigabyte.com/Support");
        if (name.Contains("asrock")) return new(Loc.T("Поддержка ASRock"), "https://www.asrock.com/support/index.asp");
        if (name.Contains("acer")) return new(Loc.T("Поддержка Acer"), "https://www.acer.com/us-en/support/drivers-and-manuals");
        if (name.Contains("lenovo")) return new(Loc.T("Поддержка Lenovo"), "https://pcsupport.lenovo.com/");
        if (name.Contains("hewlett") || name.StartsWith("hp", StringComparison.Ordinal)) return new(Loc.T("Поддержка HP"), "https://support.hp.com/drivers");
        if (name.Contains("dell") || name.Contains("alienware")) return new(Loc.T("Поддержка Dell"), "https://www.dell.com/support/home/");
        if (name.Contains("huawei")) return new(Loc.T("Поддержка Huawei"), "https://consumer.huawei.com/en/support/");
        if (name.Contains("samsung")) return new(Loc.T("Поддержка Samsung"), "https://www.samsung.com/us/support/");
        if (name.Contains("microsoft")) return new(Loc.T("Поддержка Surface"), "https://support.microsoft.com/surface");
        return null;
    }

    /// <summary>Лучшая ссылка для обновления драйвера устройства.</summary>
    public static DriverLink LinkFor(DriverInfo d, DeviceInfo dev)
    {
        var vendor = VendorOf(d);
        var oem = OemSupport(dev);
        // Видеокарты и Intel-устройства — с сайта производителя чипа: там свежее, чем у OEM.
        // Звук, LAN/Wi-Fi Realtek/MediaTek/Broadcom и чипсет ноутбуков — у производителя ПК: драйвер адаптирован под плату.
        return (d.Category, vendor) switch
        {
            (DriverCategory.Display, ChipVendor.Nvidia) => new(Loc.T("Драйверы NVIDIA"), Sources.NvidiaDrivers),
            (DriverCategory.Display, ChipVendor.Amd) => new(Loc.T("Драйверы AMD"), Sources.AmdDrivers),
            (DriverCategory.Display, ChipVendor.Intel) => new("Intel Driver & Support Assistant", Sources.IntelDsa),
            (DriverCategory.Chipset, ChipVendor.Amd) when !dev.IsLaptop => new(Loc.T("Драйвер чипсета AMD"), Sources.AmdDrivers),
            (DriverCategory.Chipset, ChipVendor.Intel) when !dev.IsLaptop => new("Intel Driver & Support Assistant", Sources.IntelDsa),
            (_, ChipVendor.Intel) => new("Intel Driver & Support Assistant", Sources.IntelDsa),
            (_, ChipVendor.Qualcomm) when oem is null => new("Intel Driver & Support Assistant (Killer)", Sources.IntelDsa),
            (DriverCategory.Storage, ChipVendor.Samsung) => new(Loc.T("Samsung Magician и NVMe-драйвер"), "https://semiconductor.samsung.com/consumer-storage/support/tools/"),
            _ when oem is not null => oem,
            (_, ChipVendor.Amd) => new(Loc.T("Драйверы AMD"), Sources.AmdDrivers),
            (_, ChipVendor.Nvidia) => new(Loc.T("Драйверы NVIDIA"), Sources.NvidiaDrivers),
            _ => new(Loc.T("Необязательные обновления Windows"), Sources.WindowsOptionalUpdates),
        };
    }

    /// <summary>
    /// Как поставить драйвер видеокарты начисто, когда он старый или стандартный. Только текст: Opti ничего не скачивает и не удаляет.
    /// null — подсказка не нужна (не видеокарта или драйвер в порядке).
    /// </summary>
    public static string? CleanInstallHint(DriverInfo d, DriverStatus status)
    {
        if (d.Category != DriverCategory.Display || status == DriverStatus.Ok) return null;
        var common = Loc.T(" Перед обновлением создайте точку восстановления. Если после обновления появились артефакты или вылеты — удалите драйвер программой Display Driver Uninstaller (сторонняя, запускается в безопасном режиме) и поставьте заново; обычно это не нужно.");
        return VendorOf(d) switch
        {
            ChipVendor.Nvidia => Loc.T("Как обновить начисто: в установщике NVIDIA выберите «Пользовательская установка» и отметьте «Выполнить чистую установку».") + common,
            ChipVendor.Amd => Loc.T("Как обновить начисто: в установщике AMD Software выберите «Сброс к заводским настройкам» (Factory Reset).") + common,
            ChipVendor.Intel => Loc.T("Как обновить: Intel Driver & Support Assistant сам подберёт драйвер для вашей графики.") + common,
            _ => null,
        };
    }

    public static string CategoryName(DriverCategory c) => c switch
    {
        DriverCategory.Display => Loc.T("Видео"),
        DriverCategory.Network => Loc.T("Сеть"),
        DriverCategory.Audio => Loc.T("Звук"),
        DriverCategory.Chipset => Loc.T("Чипсет"),
        DriverCategory.Storage => Loc.T("Накопители"),
        DriverCategory.Bluetooth => "Bluetooth",
        DriverCategory.Usb => "USB",
        DriverCategory.Input => Loc.T("Ввод"),
        _ => Loc.T("Прочее"),
    };

    /// <summary>Категория по классу устройства Windows (DeviceClass в Win32_PnPSignedDriver).</summary>
    public static DriverCategory CategoryFromClass(string? cls, string? name) => (cls ?? "").ToUpperInvariant() switch
    {
        "DISPLAY" => DriverCategory.Display,
        "NET" => DriverCategory.Network,
        "MEDIA" or "AUDIOENDPOINT" => DriverCategory.Audio,
        "BLUETOOTH" => DriverCategory.Bluetooth,
        "SCSIADAPTER" or "HDC" or "DISKDRIVE" => DriverCategory.Storage,
        "USB" => DriverCategory.Usb,
        "HIDCLASS" or "MOUSE" or "KEYBOARD" => DriverCategory.Input,
        "SYSTEM" when IsChipsetName(name) => DriverCategory.Chipset,
        _ => DriverCategory.Other,
    };

    private static bool IsChipsetName(string? n) => n is not null && (
        n.Contains("SMBus", StringComparison.OrdinalIgnoreCase) || n.Contains("Management Engine", StringComparison.OrdinalIgnoreCase)
        || n.Contains("PSP", StringComparison.Ordinal) || n.Contains("GPIO", StringComparison.OrdinalIgnoreCase)
        || n.Contains("Serial IO", StringComparison.OrdinalIgnoreCase) || n.Contains("PPM Provisioning", StringComparison.OrdinalIgnoreCase)
        || n.Contains("Chipset", StringComparison.OrdinalIgnoreCase) || n.Contains("Dynamic Tuning", StringComparison.OrdinalIgnoreCase));
}
