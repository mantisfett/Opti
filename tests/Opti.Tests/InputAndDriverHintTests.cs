using Opti.Analysis;
using Opti.Analysis.Detectors;
using Opti.Core.Drivers;
using Opti.Core.Profile;
using Xunit;

namespace Opti.Tests;

public class InputAndDriverHintTests
{
    private static IReadOnlyList<string> InputFindings(Action<SystemProfile> setup)
    {
        var p = new SystemProfile();
        setup(p);
        return DetectorCatalog.RunAll(p, new[] { new InputDetector() }).Select(f => f.Id).ToList();
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(6, true)]
    [InlineData(14, true)]
    public void Pointer_speed_finding_only_when_not_one_to_one(int speed, bool expected) =>
        Assert.Equal(expected, InputFindings(p => { p.Input.MouseAcceleration = false; p.Input.MousePointerSpeed = speed; }).Contains("MOUSE_POINTER_SPEED"));

    [Fact]
    public void Pointer_speed_is_not_reported_when_acceleration_is_on_or_unknown()
    {
        Assert.DoesNotContain("MOUSE_POINTER_SPEED", InputFindings(p => { p.Input.MouseAcceleration = true; p.Input.MousePointerSpeed = 4; }));
        Assert.DoesNotContain("MOUSE_POINTER_SPEED", InputFindings(p => { p.Input.MousePointerSpeed = null; }));
    }

    private static DriverInfo Gpu(string vendorId, string name) =>
        new() { DeviceName = name, Category = DriverCategory.Display, HardwareId = $@"PCI\VEN_{vendorId}&DEV_0001", Provider = "x", Date = new DateTime(2020, 1, 1) };

    [Theory]
    [InlineData("10DE", "NVIDIA", "NVIDIA")]
    [InlineData("1002", "AMD", "Factory Reset")]
    [InlineData("8086", "Intel", "Intel Driver")]
    public void Clean_install_hint_is_vendor_specific(string vendor, string name, string fragment)
    {
        var d = Gpu(vendor, name);
        var hint = DriverAdvisor.CleanInstallHint(d, DriverStatus.Old);
        Assert.NotNull(hint);
        Assert.Contains(fragment, hint!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Display Driver Uninstaller", hint);
    }

    [Fact]
    public void No_hint_for_healthy_driver_or_other_categories()
    {
        Assert.Null(DriverAdvisor.CleanInstallHint(Gpu("10DE", "NVIDIA"), DriverStatus.Ok));
        var net = new DriverInfo { DeviceName = "Realtek", Category = DriverCategory.Network, HardwareId = @"PCI\VEN_10EC&DEV_8125" };
        Assert.Null(DriverAdvisor.CleanInstallHint(net, DriverStatus.Old));
        Assert.Null(DriverAdvisor.CleanInstallHint(Gpu("1234", "Unknown"), DriverStatus.Old));
    }
}
