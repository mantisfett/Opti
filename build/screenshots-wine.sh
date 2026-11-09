#!/usr/bin/env bash
# Снимки интерфейса без Windows: WPF-приложение под Wine с виртуальным экраном.
#   build/screenshots-wine.sh [папка-для-снимков]
# Нужны: wine64, xvfb, fonts-open-sans, fonts-dejavu-core, python3-fonttools (или pip install fonttools).
# Шрифты подменные (см. build/wine-fonts.py), поэтому это проверка вёрстки, а не пиксельная точность;
# точные снимки на Windows делает CI (джоб screenshots).
set -euo pipefail
cd "$(dirname "$0")/.."
OUT="$(realpath -m "${1:-screenshots}")"
WORK="${OPTI_WINE_WORK:-$PWD/obj/wine}"
export WINEPREFIX="$WORK/prefix" WINEDEBUG=-all
mkdir -p "$OUT" "$WORK"

dotnet publish src/Opti.App -c Release -r win-x64 -p:PublishSingleFile=false -p:DebugType=none -o "$WORK/app" --nologo -v quiet

if [ ! -f "$WINEPREFIX/.opti-fonts" ]; then
  xvfb-run -a wineboot -u >/dev/null 2>&1 || true
  FONTS="$WINEPREFIX/drive_c/windows/Fonts"
  mkdir -p "$FONTS"
  python3 build/wine-fonts.py "$FONTS"
  for f in "$FONTS"/*.ttf; do
    name="$(basename "$f" .ttf)"
    xvfb-run -a wine reg add 'HKLM\Software\Microsoft\Windows NT\CurrentVersion\Fonts' /v "$name (TrueType)" /d "$(basename "$f")" /f >/dev/null 2>&1
  done
  touch "$WINEPREFIX/.opti-fonts"
fi

xvfb-run -a -s "-screen 0 1600x1200x24" wine "$WORK/app/Opti.exe" --screenshots "Z:$OUT" ${OPTI_LANG:+--lang $OPTI_LANG}
ls "$OUT"
