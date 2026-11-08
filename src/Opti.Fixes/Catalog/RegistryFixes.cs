using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Core.Registry;
using Opti.Fixes.Backends;
using Opti.Core.Localization;

namespace Opti.Fixes.Catalog;

/// <summary>
/// Возвращает стандартные значения Windows для нарушенных правил аудита реестра.
/// Можно ограничить набор правил опцией «rules» (Id через запятую). Каждое значение журналируется и откатывается.
/// </summary>
public abstract class RegistryDefaultsFixBase : FixBase
{
    public const string RulesOption = "rules";
    protected abstract RegistryRuleGroup Group { get; }
    public override FixCategory Category => FixCategory.Registry;
    public override RiskLevel Risk => RiskLevel.Moderate;
    public override bool RequiresReboot => true;

    public override bool IsApplicable(SystemProfile p) => RegistryAuditCatalog.Violations(p.RegistryAudit, Group).Any();
    public override bool IsAlreadyApplied(SystemProfile p) => !IsApplicable(p);

    protected override void Execute(ChangeContext ctx)
    {
        var rules = RegistryAuditCatalog.Violations(ctx.Profile.RegistryAudit, Group);
        if (ctx.Options.TryGetValue(RulesOption, out var only) && !string.IsNullOrWhiteSpace(only))
        {
            var set = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            rules = rules.Where(r => set.Contains(r.Id));
        }
        foreach (var r in rules)
        {
            var target = Targets.Reg(r.Key, r.ValueName);
            if (r.DefaultDword is { } d) ctx.Set(ChangeType.Registry, target, StateValue.DWord(d));
            else ctx.Delete(ChangeType.Registry, target);
        }
    }
}

public sealed class RegistryPerfDefaultsFix : RegistryDefaultsFixBase
{
    protected override RegistryRuleGroup Group => RegistryRuleGroup.Performance;
    public override string Id => FixIds.RegistryPerfDefaults;
    public override string Title => Loc.T("Вернуть стандартные значения реестра (вредные «твики»)");
    public override string Explanation => Loc.T("Убирает значения, оставленные «оптимизаторами»: большой системный кеш, отключённое восстановление видеодрайвера, ") +
                                          Loc.T("отключённые SysMain и Prefetch и т. п. Ставит значения, как в чистой Windows. Вступает в силу после перезагрузки.");
}

public sealed class RegistrySecurityDefaultsFix : RegistryDefaultsFixBase
{
    protected override RegistryRuleGroup Group => RegistryRuleGroup.UpdatesAndSecurity;
    public override string Id => FixIds.RegistrySecurityDefaults;
    public override string Title => Loc.T("Включить обратно обновления и защиту Windows");
    public override string Explanation => Loc.T("Снимает запреты на Центр обновления, драйверы из него, службы BITS и звука, Microsoft Defender и защиту процессора — ") +
                                          Loc.T("если их отключили через реестр. Вступает в силу после перезагрузки.");
}
