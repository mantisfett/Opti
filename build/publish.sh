#!/usr/bin/env bash
# Портативная сборка для Windows x64 (работает в Linux и Windows/Git Bash):
#   build/publish.sh [версия]
# Результат в artifacts/: Opti.exe (интерфейс), opti-cli.exe (командная строка), SHA256SUMS.txt.
#
# Как устроено (2.0): приложение (Opti.App + Opti.Cli) публикуется обычной папкой, упаковывается в zip
# и встраивается в лаунчер Opti.Launcher — один управляемый файл без нативных библиотек. Лаунчер ничего
# не распаковывает во временную папку пользователя: он выкладывает приложение в защищённую
# C:\ProgramData\Opti\app\<версия>-<хеш> и запускает его оттуда.
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)}"
DOTNET="${DOTNET:-dotnet}"
COMMON=(-c Release -r win-x64 -p:Version="$VERSION" -p:DebugType=none -p:DebugSymbols=false --nologo -v quiet)
rm -rf artifacts
mkdir -p artifacts
PAYLOAD="$PWD/artifacts/payload"

"$DOTNET" publish src/Opti.App "${COMMON[@]}" -p:PublishSingleFile=false -o "$PAYLOAD"
"$DOTNET" publish src/Opti.Cli "${COMMON[@]}" -p:PublishSingleFile=false -o "$PAYLOAD"
"$DOTNET" publish src/Opti.Launcher "${COMMON[@]}" -p:OptiPayloadDir="$PAYLOAD" -o artifacts/gui
"$DOTNET" publish src/Opti.Launcher "${COMMON[@]}" -p:OptiPayloadDir="$PAYLOAD" -p:OptiCli=true -p:IntermediateOutputPath=obj/cli/ -o artifacts/cli
mv artifacts/gui/Opti.exe artifacts/Opti.exe
mv artifacts/cli/opti-cli.exe artifacts/opti-cli.exe
rm -rf artifacts/gui artifacts/cli "$PAYLOAD"

(cd artifacts && sha256sum Opti.exe opti-cli.exe > SHA256SUMS.txt)
ls -la artifacts
