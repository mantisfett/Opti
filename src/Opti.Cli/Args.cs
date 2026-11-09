namespace Opti.Cli;

/// <summary>Минимальный разбор аргументов: «команда --ключ значение --флаг».</summary>
public sealed class Args
{
    private readonly Dictionary<string, List<string>> _opts = new(StringComparer.OrdinalIgnoreCase);
    public string Command { get; }
    public List<string> Positional { get; } = new();

    public Args(string[] argv)
    {
        bool hasCommand = argv.Length > 0 && !argv[0].StartsWith("--", StringComparison.Ordinal);
        Command = hasCommand ? argv[0].ToLowerInvariant() : "help";
        for (int i = hasCommand ? 1 : 0; i < argv.Length; i++)
        {
            var a = argv[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var key = a[2..];
                string val = i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal) ? argv[++i] : "true";
                if (!_opts.TryGetValue(key, out var list)) _opts[key] = list = new();
                list.Add(val);
            }
            else Positional.Add(a);
        }
    }

    public string? Get(string key) => _opts.TryGetValue(key, out var v) ? v[^1] : null;
    public IReadOnlyList<string> GetAll(string key) => _opts.TryGetValue(key, out var v) ? v : Array.Empty<string>();
    public bool Has(string key) => _opts.ContainsKey(key);
    public int GetInt(string key, int def) => int.TryParse(Get(key), out var v) ? v : def;
}
