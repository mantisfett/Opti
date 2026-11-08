#!/usr/bin/env python3
"""
Достаёт русские строки из исходников (C# и XAML) и сравнивает с src/Opti.Core/Localization/en/*.json.
  python3 build/loc-extract.py            — сколько строк без перевода по проектам
  python3 build/loc-extract.py --list P   — вывести непереведённые строки проекта P (например Opti.Core)
Строки с подстановками ($"…{x}…") превращаются в шаблоны: каждое {…} заменяется на {} (так они и записаны в en.json,
а в английском значении подстановки — {0}, {1}… по порядку).
"""
import re, sys, json, glob, os
CYR = re.compile('[А-Яа-яЁё]')
root = os.path.join(os.path.dirname(__file__), '..')

def cs_strings(text):
    # Литералы C#: обычные, $-строки, @-строки и сырые (три кавычки). Возвращает (ключ, интерполяция).
    out = []; i = 0; n = len(text)
    while i < n:
        c = text[i]
        if c == '/' and text[i:i+2] == '//':
            j = text.find('\n', i); i = n if j < 0 else j; continue
        if c == '/' and text[i:i+2] == '/*':
            j = text.find('*/', i); i = n if j < 0 else j + 2; continue
        if c == "'":  # char literal
            j = i + 1
            while j < n and text[j] != "'":
                j += 2 if text[j] == '\\' else 1
            i = j + 1; continue
        m = re.match(r'(\$+)?(@)?(?:"""|")', text[i:i+8])
        if m and (c in '$@"'):
            i0 = i
            dollars = len(m.group(1) or ''); verb = bool(m.group(2))
            q = text[i + len(m.group(0)) - 3: i + len(m.group(0))] == '"""'
            start = i + len(m.group(0))
            if q:
                end = text.find('"""', start); body = text[start:end]; i = end + 3
                out.append((body, dollars > 0, i0, i)); continue
            j = start; buf = []
            while j < n:
                ch = text[j]
                if verb and ch == '"' and text[j:j+2] == '""': buf.append('"'); j += 2; continue
                if ch == '"' : break
                if not verb and ch == '\\':
                    esc = text[j+1]
                    buf.append({'n': '\n', 't': '\t', '"': '"', '\\': '\\', "'": "'", 'r': ''}.get(esc, esc)); j += 2; continue
                if dollars and ch == '{':
                    if text[j+1] == '{': buf.append('{'); j += 2; continue
                    depth = 1; k = j + 1
                    while k < n and depth:
                        if text[k] == '{': depth += 1
                        elif text[k] == '}': depth -= 1
                        elif text[k] == '"':  # вложенная строка в подстановке
                            k += 1
                            while k < n and text[k] != '"': k += 2 if text[k] == '\\' else 1
                        k += 1
                    buf.append('{}'); j = k; continue
                if dollars and ch == '}' and text[j+1] == '}': buf.append('}'); j += 2; continue
                buf.append(ch); j += 1
            out.append((''.join(buf), dollars > 0, i0, j + 1)); i = j + 1; continue
        i += 1
    # Соседние литералы, соединённые «+» (длинные тексты в несколько строк), — один ключ: во время работы это одна строка
    merged = []
    for body, interp, a, b in out:
        if merged and re.fullmatch(r'\s*\+\s*', text[merged[-1][3]:a]):
            pb, pi, pa, _ = merged[-1]
            merged[-1] = (pb + body, pi or interp, pa, b)
        else:
            merged.append((body, interp, a, b))
    return [(b, i) for b, i, _, _ in merged]

def xaml_strings(text):
    out = []
    for m in re.finditer(r'\b(?:Text|Content|Header|ToolTip|AutomationProperties\.Name)="([^"{][^"]*)"', text):
        v = m.group(1).replace('&#xA;', '\n').replace('&quot;', '"').replace('&amp;', '&').replace('&lt;', '<').replace('&gt;', '>')
        out.append((v, False))
    return out

def collect():
    res = {}
    for p in glob.glob(os.path.join(root, 'src/**/*'), recursive=True):
        if '/obj/' in p or '/bin/' in p: continue
        if p.endswith('.cs'): items = cs_strings(open(p, encoding='utf-8').read())
        elif p.endswith('.xaml'): items = xaml_strings(open(p, encoding='utf-8').read())
        else: continue
        proj = p.split('/src/')[1].split('/')[0]
        for s, _ in items:
            if CYR.search(s) and s.strip():
                res.setdefault(proj, {}).setdefault(s, os.path.basename(p))
    return res

if __name__ == '__main__':
    en = {}
    for f in glob.glob(os.path.join(root, 'src/Opti.Core/Localization/en/*.json')):
        en.update(json.load(open(f, encoding='utf-8')))
    res = collect()
    if '--list' in sys.argv:
        proj = sys.argv[sys.argv.index('--list') + 1]
        for s, f in res.get(proj, {}).items():
            if s not in en: print(json.dumps(s, ensure_ascii=False))
    else:
        tot = miss = 0
        for proj, d in sorted(res.items()):
            m = sum(1 for s in d if s not in en); tot += len(d); miss += m
            print(f'{proj:20} всего {len(d):4}  без перевода {m:4}')
        print(f'{"ИТОГО":20} всего {tot:4}  без перевода {miss:4}')
