# Портативная сборка в Windows: powershell -ExecutionPolicy Bypass -File build\publish.ps1 [-Version 2.0.0]
# Устройство сборки описано в build/publish.sh: приложение встраивается в лаунчер, который выкладывает его
# в защищённую C:\ProgramData\Opti\app и ничего не распаковывает во временную папку пользователя.
param([string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
if (-not $Version) { $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version }
Remove-Item artifacts -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory artifacts | Out-Null
$payload = Join-Path (Get-Location) 'artifacts\payload'
$common = @('-c', 'Release', '-r', 'win-x64', "-p:Version=$Version", '-p:DebugType=none', '-p:DebugSymbols=false', '--nologo', '-v', 'quiet')

function Publish([string]$project, [string[]]$extra) {
    dotnet publish $project @common @extra
    if ($LASTEXITCODE) { throw "publish $project failed" }
}
Publish 'src\Opti.App' @('-p:PublishSingleFile=false', '-o', $payload)
Publish 'src\Opti.Cli' @('-p:PublishSingleFile=false', '-o', $payload)
Publish 'src\Opti.Launcher' @("-p:OptiPayloadDir=$payload", '-o', 'artifacts\gui')
Publish 'src\Opti.Launcher' @("-p:OptiPayloadDir=$payload", '-p:OptiCli=true', '-p:IntermediateOutputPath=obj\cli\', '-o', 'artifacts\cli')
Move-Item artifacts\gui\Opti.exe artifacts\Opti.exe
Move-Item artifacts\cli\opti-cli.exe artifacts\opti-cli.exe
Remove-Item artifacts\gui, artifacts\cli, $payload -Recurse -Force
Get-ChildItem artifacts\*.exe | Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLower())  $(Split-Path $_.Path -Leaf)" } | Set-Content artifacts\SHA256SUMS.txt
Get-ChildItem artifacts
