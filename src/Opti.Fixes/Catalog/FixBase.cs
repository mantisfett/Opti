using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Fixes.Backends;

namespace Opti.Fixes.Catalog;

public abstract class FixBase : IFix
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Explanation { get; }
    public virtual RiskLevel Risk => RiskLevel.Safe;
    public virtual bool RequiresReboot => false;
    public abstract FixCategory Category { get; }
    public virtual FixIntent Intent => FixIntent.Repair;
    public virtual bool RequiresAbTest => false;
    public virtual bool TouchesBcd => false;
    public virtual bool NeedsUserChoice => false;
    public virtual int Priority => 0;

    public virtual bool IsApplicable(SystemProfile p) => true;
    public abstract bool IsAlreadyApplied(SystemProfile p);

    public IReadOnlyList<ChangeRecord> Apply(ChangeContext ctx)
    {
        Execute(ctx);
        return ctx.Records.ToList();
    }

    protected abstract void Execute(ChangeContext ctx);

    protected static void SetDword(ChangeContext ctx, string key, string name, uint value) =>
        ctx.Set(ChangeType.Registry, Targets.Reg(key, name), StateValue.DWord(value));

    protected static void SetString(ChangeContext ctx, string key, string name, string value) =>
        ctx.Set(ChangeType.Registry, Targets.Reg(key, name), StateValue.Str(value));
}

public static class FixCatalog
{
    public static IReadOnlyList<IFix> All() => new IFix[]
    {
        new DisplayMaxHzFix(),
        new GpuPreferenceDgpuFix(),
        new WindowedOptimizationsFix(),
        new GameModeOnFix(),
        new GameDvrOffFix(),
        new MpoResetFix(),
        new HagsAbFix(),
        new PowerSchemePerfFix(),
        new PowerAcTuningFix(),
        new PowerThrottlingOffFix(),
        new BcdTimersResetFix(),
        new TimerResolutionGlobalFix(),
        new ObsGameCaptureFix(),
        new ObsNoAdminFix(),
        new StartupDisableFix(),
        new NetAdapterPowerOffFix(),
        new MouseAccelOffFix(),
        new AccessibilityHotkeysOffFix(),
        new PageFileAutoFix(),
        new DeliveryOptimizationLanFix(),
        new DnsSetFix(),
        new RegistryPerfDefaultsFix(),
        new RegistrySecurityDefaultsFix(),
    };

    public static IFix? Find(string id) => All().FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
}
