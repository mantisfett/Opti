using Opti.Core.Fixes;
using Opti.Core.Localization;

namespace Opti.Fixes.Backends;

/// <summary>Бэкенд в памяти — для тестов и для эмуляции на сохранённых профилях.</summary>
public sealed class InMemoryBackend : IStateBackend
{
    private readonly Dictionary<string, StateValue> _values = new(StringComparer.OrdinalIgnoreCase);
    public InMemoryBackend(ChangeType type) => Type = type;

    public ChangeType Type { get; }
    public Func<string, bool>? FailWriteWhen { get; set; }
    public List<(string Target, StateValue Value)> Writes { get; } = new();

    public StateValue Read(string target) => _values.TryGetValue(target, out var v) ? v : StateValue.Missing;

    public void Write(string target, StateValue value)
    {
        if (FailWriteWhen?.Invoke(target) == true) throw new IOException(Loc.T($"Отказ записи (тест): {target}"));
        Writes.Add((target, value));
        if (value.Exists) _values[target] = value; else _values.Remove(target);
    }

    public void Seed(string target, StateValue value) => _values[target] = value;
    public IReadOnlyDictionary<string, StateValue> Snapshot() => new Dictionary<string, StateValue>(_values, StringComparer.OrdinalIgnoreCase);

    public static BackendRegistry CreateRegistry(out Dictionary<ChangeType, InMemoryBackend> map)
    {
        map = Enum.GetValues<ChangeType>().ToDictionary(t => t, t => new InMemoryBackend(t));
        return new BackendRegistry(map.Values);
    }
}

/// <summary>
/// «Сухой прогон»: читает реальную систему, а записи складывает в память.
/// Позволяет показать точный список изменений, ничего не меняя.
/// </summary>
public sealed class DryRunBackendRegistry : IBackendRegistry
{
    private readonly IBackendRegistry _inner;
    private readonly Dictionary<ChangeType, OverlayBackend> _overlays = new();
    public DryRunBackendRegistry(IBackendRegistry inner) => _inner = inner;

    public IStateBackend Get(ChangeType type)
    {
        if (!_overlays.TryGetValue(type, out var o)) _overlays[type] = o = new OverlayBackend(_inner.Get(type));
        return o;
    }

    private sealed class OverlayBackend : IStateBackend
    {
        private readonly IStateBackend _inner;
        private readonly Dictionary<string, StateValue> _overlay = new(StringComparer.OrdinalIgnoreCase);
        public OverlayBackend(IStateBackend inner) => _inner = inner;
        public ChangeType Type => _inner.Type;
        public StateValue Read(string target) => _overlay.TryGetValue(target, out var v) ? v : _inner.Read(target);
        public void Write(string target, StateValue value) => _overlay[target] = value;
    }
}
