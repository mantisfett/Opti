# Подпись кода .exe сертификатом из переменных окружения (используется build/publish.ps1 и CI).
#   OPTI_SIGN_CERT_BASE64   — файл сертификата .pfx в base64
#   OPTI_SIGN_CERT_PASSWORD — пароль сертификата
#   OPTI_SIGN_TIMESTAMP     — сервер меток времени (по умолчанию http://timestamp.digicert.com)
# Метка времени нужна, чтобы подпись оставалась действительной после окончания срока сертификата.
param([Parameter(Mandatory)][string[]]$Files)
$ErrorActionPreference = 'Stop'
if (-not $env:OPTI_SIGN_CERT_BASE64) { throw 'OPTI_SIGN_CERT_BASE64 не задан' }
$timestamp = if ($env:OPTI_SIGN_TIMESTAMP) { $env:OPTI_SIGN_TIMESTAMP } else { 'http://timestamp.digicert.com' }

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe не найден (нужен Windows SDK)' }

$pfx = Join-Path ([IO.Path]::GetTempPath()) ("opti-sign-" + [Guid]::NewGuid().ToString('N') + '.pfx')
try {
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:OPTI_SIGN_CERT_BASE64))
    foreach ($f in $Files) {
        & $signtool.FullName sign /fd SHA256 /td SHA256 /tr $timestamp /f $pfx /p $env:OPTI_SIGN_CERT_PASSWORD $f
        if ($LASTEXITCODE) { throw "Не удалось подписать $f" }
        & $signtool.FullName verify /pa $f
        if ($LASTEXITCODE) { throw "Подпись $f не прошла проверку" }
        Write-Host "Подписан: $f"
    }
}
finally {
    Remove-Item $pfx -Force -ErrorAction SilentlyContinue
}
