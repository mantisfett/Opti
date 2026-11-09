using System.Text.Json.Serialization;
using Opti.Core.Localization;

namespace Opti.Core.Profile;

/// <summary>
/// Снимок состояния ПК. Всё, что не удалось собрать, остаётся null —
/// детекторы обязаны трактовать null как «неизвестно», а не как «нет проблемы/есть проблема».
/// </summary>
public sealed class SystemProfile
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.Now;
    public string? Label { get; set; }

    public DeviceInfo Device { get; set; } = new();
    public OsInfo Os { get; set; } = new();
    public CpuInfo Cpu { get; set; } = new();
    public List<GpuInfo> Gpus { get; set; } = new();
    public HybridGraphicsInfo Hybrid { get; set; } = new();
    public List<DisplayInfo> Displays { get; set; } = new();
    public MemoryInfo Memory { get; set; } = new();
    public List<DiskInfo> Disks { get; set; } = new();
    public ThermalInfo Thermals { get; set; } = new();
    public PowerInfo Power { get; set; } = new();
    public TimerInfo Timers { get; set; } = new();
    public SecurityInfo Security { get; set; } = new();
    public GraphicsSettingsInfo Graphics { get; set; } = new();
    public BackgroundInfo Background { get; set; } = new();
    public ObsInfo? Obs { get; set; }
    public List<NetAdapterInfo> Network { get; set; } = new();
    public NetworkQuality? NetQuality { get; set; }
    /// <summary>
    /// Режим «Оптимизации доставки» Windows (DODownloadMode): 0 — выкл., 1 — только локальная сеть, 2 — группа,
    /// 3 — локальная сеть и интернет (раздаёт обновления другим ПК и занимает исходящий канал). null — не задан.
    /// </summary>
    public int? DeliveryOptimizationMode { get; set; }
    public DpcLatencyInfo? Dpc { get; set; }
    public List<GameInfo> Games { get; set; } = new();
    /// <summary>Устройства с ошибками (нет драйвера, отключены, конфликт).</summary>
    public List<ProblemDevice> ProblemDevices { get; set; } = new();
    public ChipsetDriversInfo Chipset { get; set; } = new();
    public InputInfo Input { get; set; } = new();
    /// <summary>Драйверы ключевых устройств (видео, сеть, звук, чипсет, Bluetooth…).</summary>
    public List<DriverInfo> Drivers { get; set; } = new();
    /// <summary>Значения реестра из аудита (см. RegistryAuditCatalog): Id правила → значение (null — значения нет).</summary>
    public Dictionary<string, string?> RegistryAudit { get; set; } = new();

    /// <summary>Ошибки коллекторов: что не удалось собрать и почему.</summary>
    public List<string> CollectorErrors { get; set; } = new();

    [JsonIgnore] public DiskInfo? SystemDisk => Disks.FirstOrDefault(d => d.IsSystem);
    [JsonIgnore] public DisplayInfo? PrimaryDisplay => Displays.FirstOrDefault(d => d.IsPrimary) ?? Displays.FirstOrDefault();
    [JsonIgnore] public GpuInfo? DiscreteGpu => Gpus.FirstOrDefault(g => !g.IsIntegrated && !g.IsSoftware);
    [JsonIgnore] public GpuInfo? IntegratedGpu => Gpus.FirstOrDefault(g => g.IsIntegrated);
}

public sealed class DeviceInfo
{
    public bool IsLaptop { get; set; }
    public bool HasBattery { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? BiosVersion { get; set; }
    public DateTime? BiosDate { get; set; }
    public bool IsVirtualMachine { get; set; }
    /// <summary>Производитель и модель материнской платы (Win32_BaseBoard) — для ссылок на драйверы.</summary>
    public string? BoardManufacturer { get; set; }
    public string? BoardProduct { get; set; }
}

public sealed class OsInfo
{
    public string? ProductName { get; set; }
    public string? Edition { get; set; }
    public string? DisplayVersion { get; set; }   // 23H2, 24H2…
    public int Build { get; set; }
    public int Ubr { get; set; }
    /// <summary>Название модифицированной сборки (ReviOS, AtlasOS…), если обнаружена.</summary>
    public string? ModifiedBuild { get; set; }
    /// <summary>Дата установки Windows (InstallDate в реестре).</summary>
    public DateTime? InstallDate { get; set; }
    /// <summary>Windows ждёт перезагрузку для завершения обновлений.</summary>
    public bool? PendingReboot { get; set; }

    [JsonIgnore] public bool IsWindows11 => Build >= 22000;
    /// <summary>Windows 11 24H2 и новее.</summary>
    [JsonIgnore] public bool IsWin11_24H2OrLater => Build >= 26100;
}

public enum CpuVendor { Unknown, Intel, Amd, Other }

public sealed class CpuInfo
{
    public string? Name { get; set; }
    public CpuVendor Vendor { get; set; }
    public int Cores { get; set; }
    public int Threads { get; set; }
    public int MaxClockMhz { get; set; }
    public int? CurrentClockMhz { get; set; }
    /// <summary>% от номинальной частоты (счётчик Processor Information/% Processor Performance).</summary>
    public double? PerformancePercent { get; set; }
}

public enum GpuVendor { Unknown, Nvidia, Amd, Intel, Microsoft, Other }

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public GpuVendor Vendor { get; set; }
    public string? DriverVersion { get; set; }
    public DateTime? DriverDate { get; set; }
    public long DedicatedVramMb { get; set; }
    public bool IsIntegrated { get; set; }
    public bool IsSoftware { get; set; }
    /// <summary>У адаптера есть подключённые выходы (DXGI EnumOutputs) — он выводит изображение.</summary>
    public bool DrivesDisplay { get; set; }
    public string? Luid { get; set; }
    public string? PnpDeviceId { get; set; }
    /// <summary>Видеокарта работает на «Базовом видеоадаптере Майкрософт» — драйвер производителя не установлен.</summary>
    public bool UsingBasicDriver { get; set; }
}

public sealed class HybridGraphicsInfo
{
    /// <summary>Есть и iGPU, и dGPU.</summary>
    public bool IsHybrid { get; set; }
    /// <summary>Встроенный экран подключён к iGPU (Optimus/AMD Hybrid без MUX или MUX в гибридном режиме).</summary>
    public bool? InternalDisplayOnIntegrated { get; set; }
    /// <summary>Наличие MUX-переключателя. null — неизвестно (Windows это не сообщает напрямую).</summary>
    public bool? HasMux { get; set; }
    /// <summary>Значение «Hybrid Graphics GPU» из dxdiag, если удалось получить.</summary>
    public string? DxDiagHybridInfo { get; set; }
}

public sealed class DisplayInfo
{
    public string DeviceName { get; set; } = "";   // \\.\DISPLAY1
    public string? FriendlyName { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int CurrentHz { get; set; }
    /// <summary>Максимальная частота, доступная при текущем разрешении.</summary>
    public int MaxHzAtCurrentResolution { get; set; }
    public bool IsPrimary { get; set; }
    public bool? IsInternal { get; set; }
    public bool? VrrSupported { get; set; }
    public bool? HdrEnabled { get; set; }
    public string? AdapterName { get; set; }
}

public sealed class MemoryModule
{
    public long CapacityMb { get; set; }
    /// <summary>Номинал модуля (Win32_PhysicalMemory.Speed), МТ/с.</summary>
    public int RatedSpeed { get; set; }
    /// <summary>Текущая (Win32_PhysicalMemory.ConfiguredClockSpeed), МТ/с.</summary>
    public int ConfiguredSpeed { get; set; }
    public string? Slot { get; set; }
    public string? PartNumber { get; set; }
    public string? Manufacturer { get; set; }
}

public sealed class MemoryInfo
{
    public long TotalMb { get; set; }
    public long AvailableMb { get; set; }
    public List<MemoryModule> Modules { get; set; } = new();
    public int? TotalSlots { get; set; }
    /// <summary>Предполагаемое число активных каналов, если удалось определить.</summary>
    public int? ChannelsPopulated { get; set; }
    public List<ProcessUsage> TopConsumers { get; set; } = new();
    /// <summary>Файл подкачки отключён полностью.</summary>
    public bool? PageFileDisabled { get; set; }

    [JsonIgnore] public double AvailablePercent => TotalMb > 0 ? 100.0 * AvailableMb / TotalMb : 0;
}

public enum DiskMediaType { Unknown, Hdd, Ssd, Nvme, Usb }

public sealed class DiskInfo
{
    public string? Model { get; set; }
    public DiskMediaType MediaType { get; set; }
    public bool IsSystem { get; set; }
    public string? DriveLetter { get; set; }
    public double SizeGb { get; set; }
    public double FreeGb { get; set; }
    public string? HealthStatus { get; set; }   // Healthy / Warning / Unhealthy
    public int? WearPercent { get; set; }
    public bool? TrimEnabled { get; set; }

    [JsonIgnore] public double UsedPercent => SizeGb > 0 ? 100.0 * (SizeGb - FreeGb) / SizeGb : 0;
}

public sealed class ThermalInfo
{
    public double? CpuTempC { get; set; }
    public double? GpuTempC { get; set; }
    /// <summary>Наблюдалось снижение частоты под нагрузкой при высокой температуре.</summary>
    public bool? ThrottleObserved { get; set; }
    public string? Source { get; set; }
}

public sealed class PowerInfo
{
    public bool? OnAcPower { get; set; }
    public int? BatteryPercent { get; set; }
    public string? ActiveSchemeGuid { get; set; }
    public string? ActiveSchemeName { get; set; }
    public List<string> AvailableSchemeGuids { get; set; } = new();
    /// <summary>Режим питания Windows 10/11 (overlay): BetterBattery / Balanced / BestPerformance.</summary>
    public string? PowerModeOverlay { get; set; }
    public bool? ModernStandby { get; set; }
    /// <summary>Максимальное состояние процессора от сети, % (PROCTHROTTLEMAX AC).</summary>
    public int? AcMaxProcessorState { get; set; }
    /// <summary>Минимум незапаркованных ядер от сети, % (CPMINCORES AC).</summary>
    public int? AcCoreParkingMinCores { get; set; }
    public bool? PowerThrottlingDisabledGlobally { get; set; }
}

public sealed class TimerInfo
{
    public bool? UsePlatformClock { get; set; }
    public bool? UsePlatformTick { get; set; }
    public bool? DisableDynamicTick { get; set; }
    public string? TscSyncPolicy { get; set; }
    public bool? GlobalTimerResolutionRequests { get; set; }
}

public sealed class SecurityInfo
{
    public bool? VbsRunning { get; set; }
    public bool? HvciRunning { get; set; }
    public bool? HypervisorPresent { get; set; }
    public string? HypervisorLaunchType { get; set; }
    public bool? BitLockerOnSystemDrive { get; set; }
    public bool? SystemRestoreEnabled { get; set; }
}

public sealed class GraphicsSettingsInfo
{
    public bool? HagsSupported { get; set; }
    public bool? HagsEnabled { get; set; }
    /// <summary>HKLM\SOFTWARE\Microsoft\Windows\Dwm\OverlayTestMode, если задан.</summary>
    public int? OverlayTestMode { get; set; }
    public bool? WindowedOptimizationsEnabled { get; set; }
    public bool? VrrOptimizationsEnabled { get; set; }
    public bool? GameModeEnabled { get; set; }
    public bool? GameDvrEnabled { get; set; }
}

public enum StartupImpact { Unknown, None, Low, Medium, High }

public sealed class StartupItem
{
    public string Name { get; set; } = "";
    public string? Command { get; set; }
    /// <summary>Run-ключ реестра или папка автозагрузки.</summary>
    public string? Location { get; set; }
    public bool Enabled { get; set; } = true;
    public StartupImpact Impact { get; set; }
    /// <summary>Значение StartupApproved, через которое пункт включается/отключается (как в Диспетчере задач):
    /// «HKCU\...\StartupApproved\Run|Имя».</summary>
    public string? ApprovedKey { get; set; }
}

public sealed class ProcessUsage
{
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    public long WorkingSetMb { get; set; }
    public double? CpuPercent { get; set; }
}

public sealed class BackgroundInfo
{
    public List<StartupItem> StartupItems { get; set; } = new();
    /// <summary>Запущенные оверлеи (Discord, Steam, RTSS, Overwolf, NVIDIA App…).</summary>
    public List<string> RunningOverlays { get; set; } = new();
    public List<ProcessUsage> HeavyProcesses { get; set; } = new();
}

public sealed class ObsInfo
{
    public bool Installed { get; set; }
    public string? ExePath { get; set; }
    public string? ConfigDir { get; set; }
    public bool Running { get; set; }
    public bool RunsAsAdmin { get; set; }
    /// <summary>В сценах есть источник «Захват экрана» (monitor_capture).</summary>
    public bool UsesDisplayCapture { get; set; }
    public List<string> SceneFilesWithDisplayCapture { get; set; } = new();
    public bool? ReplayBufferEnabled { get; set; }
    public int? ReplayBufferSeconds { get; set; }
    public int? ReplayBufferMaxMb { get; set; }
    /// <summary>Предпочтение GPU для obs64.exe в UserGpuPreferences (0 — авто, 1 — экономия, 2 — производительность).</summary>
    public int? GpuPreference { get; set; }
}

public enum NetAdapterKind { Unknown, Ethernet, WiFi, Other }

public sealed class NetAdapterInfo
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public NetAdapterKind Kind { get; set; }
    public bool IsUp { get; set; }
    public long? LinkSpeedMbps { get; set; }
    public string? DriverVersion { get; set; }
    public DateTime? DriverDate { get; set; }
    /// <summary>«Разрешить отключение этого устройства для экономии энергии».</summary>
    public bool? AllowComputerToTurnOff { get; set; }
    /// <summary>Для Wi-Fi: диапазон, ГГц (2.4/5/6).</summary>
    public double? WifiBandGhz { get; set; }
    /// <summary>Для Wi-Fi: уровень сигнала, % (netsh wlan show interfaces).</summary>
    public int? WifiSignalPercent { get; set; }
}

public sealed class NetworkQuality
{
    public string? Target { get; set; }
    public double IdlePingMs { get; set; }
    public double IdleJitterMs { get; set; }
    public double? LoadedPingMs { get; set; }
    public double PacketLossPercent { get; set; }
}

public sealed class DriverLatency
{
    public string Driver { get; set; } = "";
    public double MaxDpcUs { get; set; }
    public double MaxIsrUs { get; set; }
    public long DpcCount { get; set; }
}

public sealed class DpcLatencyInfo
{
    public double DurationSec { get; set; }
    public List<DriverLatency> Drivers { get; set; } = new();
}

public enum GameLauncher { Unknown, Steam, Epic, Gog, Xbox, Other }

public sealed class GameInfo
{
    public string Name { get; set; } = "";
    public GameLauncher Launcher { get; set; }
    public string? InstallDir { get; set; }
    /// <summary>Основные исполняемые файлы игры.</summary>
    public List<string> Executables { get; set; } = new();
    /// <summary>
    /// GpuPreference из UserGpuPreferences по каждому exe: 0 — авто, 1 — энергосбережение, 2 — высокая производительность.
    /// Отсутствие ключа — пользователь ничего не выбирал.
    /// </summary>
    public Dictionary<string, int> ExeGpuPreferences { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsOnline { get; set; }
}

public sealed class ProblemDevice
{
    public string Name { get; set; } = "";
    public string? DeviceClass { get; set; }
    /// <summary>ConfigManagerErrorCode: 28 — драйвер не установлен, 22 — отключено, 10 — не запускается…</summary>
    public int ErrorCode { get; set; }
    public string? PnpDeviceId { get; set; }

    public string ErrorText => ErrorCode switch
    {
        1 => Loc.T("устройство настроено неправильно"),
        10 => Loc.T("устройство не запускается"),
        12 => Loc.T("не хватает ресурсов"),
        18 => Loc.T("нужно переустановить драйвер"),
        22 => Loc.T("устройство отключено"),
        24 => Loc.T("устройство отсутствует или не работает"),
        28 => Loc.T("драйвер не установлен"),
        31 => Loc.T("устройство работает неправильно"),
        43 => Loc.T("устройство сообщило о сбое"),
        _ => Loc.T($"код ошибки {ErrorCode}"),
    };
}

public sealed class ChipsetDriversInfo
{
    /// <summary>AMD PPM Provisioning File Driver (ставится с драйвером чипсета AMD).</summary>
    public bool? AmdPpmDriver { get; set; }
    /// <summary>AMD 3D V-Cache Performance Optimizer (нужен двухкристальным X3D).</summary>
    public bool? AmdVCacheOptimizer { get; set; }
}

public sealed class InputInfo
{
    /// <summary>«Повышенная точность установки указателя» (ускорение мыши).</summary>
    public bool? MouseAcceleration { get; set; }
    /// <summary>HKCU\Control Panel\Mouse\MouseSensitivity: скорость указателя 1–20; 10 («6 из 11») — движение 1:1.</summary>
    public int? MousePointerSpeed { get; set; }
    /// <summary>HKCU\Control Panel\Accessibility\StickyKeys\Flags.</summary>
    public int? StickyKeysFlags { get; set; }
    /// <summary>HKCU\Control Panel\Accessibility\Keyboard Response\Flags (залипание фильтрации ввода).</summary>
    public int? FilterKeysFlags { get; set; }
}

public enum DriverCategory { Display, Network, Audio, Chipset, Storage, Bluetooth, Usb, Input, Other }

/// <summary>Установленный драйвер устройства (Win32_PnPSignedDriver).</summary>
public sealed class DriverInfo
{
    public string DeviceName { get; set; } = "";
    public DriverCategory Category { get; set; }
    /// <summary>Производитель устройства (по PnP ID или полю Manufacturer).</summary>
    public string? Manufacturer { get; set; }
    /// <summary>Кто выпустил драйвер (DriverProviderName): «Microsoft» у стандартных драйверов Windows.</summary>
    public string? Provider { get; set; }
    public string? Version { get; set; }
    public DateTime? Date { get; set; }
    public string? HardwareId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsGenericMicrosoft => Provider is { } pr && pr.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                                      && !(Manufacturer ?? "").Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
}
