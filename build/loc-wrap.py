#!/usr/bin/env python3
# Одноразовый помощник: оборачивает русские строковые литералы C# в Loc.T(...) в указанных файлах.
# Пропускает константы, case-метки, атрибуты, журнал (OptiLog) и уже обёрнутые строки.
import re, sys
CYR = re.compile('[А-Яа-яЁё]')

def literals(text):
    n = len(text); i = 0; out = []
    while i < n:
        c = text[i]
        if text.startswith('//', i):
            j = text.find('\n', i); i = n if j < 0 else j; continue
        if text.startswith('/*', i):
            j = text.find('*/', i); i = n if j < 0 else j + 2; continue
        if c == "'":
            j = i + 1
            while j < n and text[j] != "'": j += 2 if text[j] == '\\' else 1
            i = j + 1; continue
        m = re.match(r'(\$+)?(@)?"', text[i:i+4])
        if m and c in '$@"':
            dollars = len(m.group(1) or ''); verb = bool(m.group(2)); start = i
            if text.startswith('"""', i + len(m.group(0)) - 1):
                end = text.find('"""', start + 3 + dollars); end = end + 3; out.append((start, end)); i = end; continue
            j = i + len(m.group(0))
            while j < n:
                ch = text[j]
                if verb and text.startswith('""', j): j += 2; continue
                if ch == '"': break
                if not verb and ch == '\\': j += 2; continue
                if dollars and ch == '{':
                    if text[j+1] == '{': j += 2; continue
                    depth = 1; k = j + 1
                    while k < n and depth:
                        if text[k] == '{': depth += 1
                        elif text[k] == '}': depth -= 1
                        elif text[k] == '"':
                            k += 1
                            while k < n and text[k] != '"': k += 2 if text[k] == '\\' else 1
                        k += 1
                    j = k; continue
                j += 1
            out.append((start, j + 1)); i = j + 1; continue
        i += 1
    return out

def process(path):
    text = open(path, encoding='utf-8').read()
    raw = literals(text); res = []; last = 0; count = 0
    # Литералы, соединённые только «+», — один фрагмент: Loc.T("a" + "b") (так перевод один на весь текст)
    lits = []
    for s, e in raw:
        if lits and re.fullmatch(r'\s*\+\s*', text[lits[-1][1]:s]) and (CYR.search(text[lits[-1][0]:lits[-1][1]]) or CYR.search(text[s:e])):
            lits[-1] = (lits[-1][0], e)
        else:
            lits.append((s, e))
    for s, e in lits:
        lit = text[s:e]
        if not CYR.search(lit): continue
        ls = text.rfind('\n', 0, s) + 1; le = text.find('\n', e); le = len(text) if le < 0 else le
        line = text[ls:le]
        before = text[max(0, s - 8):s]
        if before.endswith('Loc.T(') or before.endswith('Loc.F(') or 'OptiLog.' in line: continue
        if re.search(r'\bconst\b', line) or re.match(r'\s*case\s', line) or re.match(r'\s*\[', line): continue
        if re.search(r'\bis\s+"', text[max(0, s - 6):s]) or re.search(r'\bor\s*$', text[max(0, s - 4):s]): continue
        res.append(text[last:s]); res.append('Loc.T(' + lit + ')'); last = e; count += 1
    res.append(text[last:])
    new = ''.join(res)
    if count:
        if 'using Opti.Core.Localization;' not in new:
            m = re.search(r'^(using [^\n]+;\n)+', new, re.M)
            new = ('using Opti.Core.Localization;\n' + new) if not m else new[:m.end()] + 'using Opti.Core.Localization;\n' + new[m.end():]
        open(path, 'w', encoding='utf-8').write(new)
    return count

if __name__ == '__main__':
    tot = 0
    for p in sys.argv[1:]:
        c = process(p); tot += c
        if c: print(f'{c:4} {p}')
    print('wrapped', tot)
