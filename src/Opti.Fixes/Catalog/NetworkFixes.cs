using Opti.Core.Fixes;
using Opti.Core.Localization;
using Opti.Core.Profile;
using Opti.Fixes.Backends;

namespace Opti.Fixes.Catalog;

/// <summary>
/// «Оптимизация доставки»: только локальная сеть (DODownloadMode = 1). Обмен обновлениями между вашими устройствами остаётся,
/// раздача в интернет прекращается. Политика пишется в HKLM и действует на Home и Pro.
/// </summary>
public sealed class DeliveryOptimizationLanFix : FixBase
{
    public const string Key = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";
    public override string Id => FixIds.DeliveryOptimizationLan;
    public override FixIntent Intent => FixIntent.Tuning;
    public override string Title => Loc.T("«Оптимизация доставки» только в локальной сети");
    public override string Explanation => Loc.T("Windows перестаёт раздавать обновления и приложения чужим компьютерам через интернет и не занимает исходящий канал — пинг в играх становится ровнее. " +
                                                "Обмен между вашими устройствами дома остаётся. Режим 1 («только локальная сеть»).");
    public override FixCategory Category => FixCategory.Network;
    public override bool IsApplicable(SystemProfile p) => p.DeliveryOptimizationMode == 3;
    public override bool IsAlreadyApplied(SystemProfile p) => p.DeliveryOptimizationMode != 3;
    protected override void Execute(ChangeContext ctx) => SetDword(ctx, Key, "DODownloadMode", 1);
}

/// <summary>
/// Смена DNS-серверов на активных адаптерах (параметр «servers»: адреса через запятую). В план не попадает — применяется со страницы «Сеть»
/// после замера скорости DNS; откат возвращает прежние серверы или автоматический режим.
/// </summary>
public sealed class DnsSetFix : FixBase
{
    public override string Id => FixIds.DnsSet;
    public override FixIntent Intent => FixIntent.Preference;
    public override string Title => Loc.T("Быстрый DNS-сервер");
    public override string Explanation => Loc.T("Заменяет DNS-серверы на активных подключениях. Быстрый DNS ускоряет вход в игру, поиск матча и загрузку лаунчеров. На пинг идущего матча он не влияет.");
    public override FixCategory Category => FixCategory.Network;
    public override bool IsApplicable(SystemProfile p) => false; // только по выбору пользователя на странице «Сеть»
    public override bool IsAlreadyApplied(SystemProfile p) => false;

    /// <summary>Активные проводные и Wi-Fi подключения.</summary>
    public static Func<IReadOnlyList<string>> ActiveAdapters { get; set; } = () =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                        && n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211
                        && n.GetIPProperties().GatewayAddresses.Count > 0)
            .Select(n => n.Name).ToList();

    protected override void Execute(ChangeContext ctx)
    {
        if (!ctx.Options.TryGetValue("servers", out var servers) || string.IsNullOrWhiteSpace(servers))
            throw new ChangeAbortedException(Loc.T("Не указаны DNS-серверы"));
        foreach (var name in ActiveAdapters())
            ctx.Set(ChangeType.Dns, Targets.Dns(name), StateValue.Str(servers));
    }
}
