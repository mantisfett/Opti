using Opti.Core.Fixes;
using Opti.Core.Profile;
using Opti.Fixes.Backends;
using Opti.Core.Localization;

namespace Opti.Fixes.Catalog;

public sealed class MouseAccelOffFix : FixBase
{
    public const string Key = @"HKCU\Control Panel\Mouse";
    public override string Id => FixIds.MouseAccelOff;
    public override FixIntent Intent => FixIntent.Preference;
    public override string Title => Loc.T("Отключить ускорение мыши");
    public override string Explanation => Loc.T("Снимает «Повышенную точность установки указателя»: движение курсора становится строго пропорциональным движению руки. ") +
                                          Loc.T("Вступает в силу после выхода из системы и повторного входа.");
    public override FixCategory Category => FixCategory.GameSettings;
    public override bool IsAlreadyApplied(SystemProfile p) => p.Input.MouseAcceleration != true;

    protected override void Execute(ChangeContext ctx)
    {
        SetString(ctx, Key, "MouseSpeed", "0");
        SetString(ctx, Key, "MouseThreshold1", "0");
        SetString(ctx, Key, "MouseThreshold2", "0");
    }
}

public sealed class AccessibilityHotkeysOffFix : FixBase
{
    public const string StickyKey = @"HKCU\Control Panel\Accessibility\StickyKeys";
    public const string FilterKey = @"HKCU\Control Panel\Accessibility\Keyboard Response";
    public override string Id => FixIds.AccessibilityHotkeysOff;
    public override FixIntent Intent => FixIntent.Preference;
    public override string Title => Loc.T("Отключить сочетания залипания клавиш");
    public override string Explanation => Loc.T("Отключает только сочетания Shift×5 и удержание правого Shift, которые сворачивают игру. Сами специальные возможности остаются в параметрах Windows.");
    public override FixCategory Category => FixCategory.GameSettings;
    public override bool IsAlreadyApplied(SystemProfile p) =>
        !(p.Input.StickyKeysFlags is { } s && (s & 4) != 0 && (s & 1) == 0) && !(p.Input.FilterKeysFlags is { } f && (f & 4) != 0 && (f & 1) == 0);

    protected override void Execute(ChangeContext ctx)
    {
        foreach (var key in new[] { StickyKey, FilterKey })
        {
            var target = Targets.Reg(key, "Flags");
            var cur = ctx.Read(ChangeType.Registry, target);
            if (!cur.Exists || !int.TryParse(cur.Value, out var flags)) continue;
            if ((flags & 1) != 0 || (flags & 4) == 0) continue; // функция включена пользователем или сочетание уже выключено
            ctx.Set(ChangeType.Registry, target, cur with { Value = (flags & ~4).ToString() });
        }
    }
}

public sealed class PageFileAutoFix : FixBase
{
    public const string Key = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    public override string Id => FixIds.PageFileAuto;
    public override string Title => Loc.T("Вернуть файл подкачки, управляемый системой");
    public override string Explanation => Loc.T("Включает «Автоматически выбирать объём файла подкачки» — настройка Windows по умолчанию, которую рекомендует Microsoft.");
    public override bool RequiresReboot => true;
    public override FixCategory Category => FixCategory.Power;
    public override bool IsAlreadyApplied(SystemProfile p) => p.Memory.PageFileDisabled != true;
    protected override void Execute(ChangeContext ctx) =>
        ctx.Set(ChangeType.Registry, Targets.Reg(Key, "PagingFiles"), new StateValue(true, "MultiString", "?:\\pagefile.sys"));
}
