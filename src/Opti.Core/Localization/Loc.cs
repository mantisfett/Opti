using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Opti.Core.Localization;

/// <summary>
/// Перевод интерфейса. Исходный язык — русский: в коде остаются русские строки, а английские значения лежат в
/// <c>Localization/en/*.json</c> (встроены в сборку). <see cref="T"/> возвращает перевод, если выбран английский, иначе строку как есть.
///
/// Ключи двух видов:
///  • точная строка — «Проанализировать»;
///  • шаблон для строк с подстановками — «Найдено {}: критичных {}»: каждая подстановка в русском ключе записана как {},
///    в английском значении — {0}, {1}… по порядку. Шаблон сопоставляется уже готовой строке, поэтому код, который
///    собирает текст через $"…", переводить не нужно.
/// Не найдено — вернётся исходный текст (интерфейс остаётся рабочим, просто часть строк по-русски).
/// </summary>
public static class Loc
{
    private static readonly object Gate = new();
    private static Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private static List<(Regex Re, string Template)> _patterns = new();
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly Regex Cyrillic = new("[А-Яа-яЁё]", RegexOptions.Compiled);

    public static bool IsEnglish { get; private set; }
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>"auto" — по языку Windows, "ru" или "en".</summary>
    public static void SetLanguage(string? language, CultureInfo? system = null)
    {
        var lang = (language ?? "auto").Trim().ToLowerInvariant();
        if (lang is not ("ru" or "en"))
        {
            var ui = (system ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName;
            // Русский — для русскоязычных; всем остальным английский понятнее
            lang = ui == "ru" ? "ru" : "en";
        }
        IsEnglish = lang == "en";
        Culture = CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "ru-RU");
        Cache.Clear();
        if (IsEnglish) EnsureLoaded();
    }

    private static bool _loaded;

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;
            var asm = typeof(Loc).Assembly;
            var all = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Opti.Loc.en.", StringComparison.Ordinal)))
            {
                using var s = asm.GetManifestResourceStream(name)!;
                foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new()) all[kv.Key] = kv.Value;
            }
            Load(all);
        }
    }

    /// <summary>Загрузить словарь (для тестов и расширений).</summary>
    public static void Load(IReadOnlyDictionary<string, string> en)
    {
        lock (Gate)
        {
            _exact = new Dictionary<string, string>(StringComparer.Ordinal);
            _patterns = new();
            foreach (var (key, value) in en)
            {
                if (!key.Contains("{}", StringComparison.Ordinal)) { _exact[key] = value; continue; }
                var parts = key.Split("{}");
                var rx = "^" + string.Join("(.+?)", parts.Select(Regex.Escape)) + "$";
                _patterns.Add((new Regex(rx, RegexOptions.Compiled | RegexOptions.Singleline), value));
            }
            // Длинные (специфичные) шаблоны раньше коротких
            _patterns = _patterns.OrderByDescending(p => p.Re.ToString().Length).ToList();
            _loaded = true;
            Cache.Clear();
        }
    }

    /// <summary>Все русские ключи (для проверок полноты).</summary>
    public static IReadOnlyCollection<string> Keys
    {
        get { EnsureLoaded(); lock (Gate) return _exact.Keys.ToList(); }
    }

    public static bool HasCyrillic(string? s) => !string.IsNullOrEmpty(s) && Cyrillic.IsMatch(s);

    public static string T(string? ru)
    {
        if (string.IsNullOrEmpty(ru) || !IsEnglish) return ru ?? "";
        if (Cache.TryGetValue(ru, out var hit)) return hit;
        var result = Translate(ru);
        Cache[ru] = result;
        return result;
    }

    private static string Translate(string ru)
    {
        if (!Cyrillic.IsMatch(ru)) return ru;
        if (_exact.TryGetValue(ru, out var exact)) return exact;
        foreach (var (re, template) in _patterns)
        {
            var m = re.Match(ru);
            if (!m.Success) continue;
            // Подстановки — это числа, имена и т. п.; русские куски внутри них тоже переводим (например, «да»/«нет»)
            var args = m.Groups.Cast<Group>().Skip(1).Select(g => (object)Translate(g.Value)).ToArray();
            try { return string.Format(CultureInfo.InvariantCulture, template, args); }
            catch (FormatException) { return ru; }
        }
        // Многострочный текст: переводим построчно
        if (ru.Contains('\n'))
            return string.Join("\n", ru.Split('\n').Select(Translate));
        return ru;
    }

    /// <summary>Строки с подстановками удобнее писать так: Loc.F("Найдено {0}", n) — без шаблонов в словаре.</summary>
    public static string F(string ruFormat, params object?[] args) =>
        string.Format(Culture, T(ruFormat), args);
}
