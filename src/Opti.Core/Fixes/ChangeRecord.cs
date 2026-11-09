namespace Opti.Core.Fixes;

public enum ChangeType { Registry, Bcd, Service, PowerSetting, NetAdapter, File, Display, Dns }

/// <summary>
/// Значение настройки. Exists=false — значения/ключа не было (откат = удаление).
/// Kind: для реестра — DWord/QWord/String/ExpandString/MultiString/Binary;
/// для файлов — BackupPath/Text; для остальных — String.
/// Value хранится строкой в инвариантном формате (Binary — hex, MultiString — через \0).
/// </summary>
public sealed record StateValue(bool Exists, string? Kind = null, string? Value = null)
{
    public static readonly StateValue Missing = new(false);
    public static StateValue DWord(uint v) => new(true, "DWord", v.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static StateValue Str(string v) => new(true, "String", v);

    public override string ToString() => Exists ? $"{Kind}:{Value}" : "<нет>";
}

public sealed class ChangeRecord
{
    public long Id { get; set; }
    public required string FixId { get; init; }
    public required ChangeType Type { get; init; }
    /// <summary>Адрес настройки. Формат зависит от Type (см. бэкенды).</summary>
    public required string Target { get; init; }
    public required StateValue Before { get; init; }
    public required StateValue After { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public required string SessionId { get; init; }
    public bool RolledBack { get; set; }
    /// <summary>
    /// false — запись сделана, но подтверждения записи настройки не было (программа завершилась между журналом и изменением).
    /// Исходное значение в ней верное, поэтому откат безопасен; в истории такая запись помечается.
    /// </summary>
    public bool Committed { get; set; } = true;
    public DateTimeOffset? RolledBackAt { get; set; }
}
