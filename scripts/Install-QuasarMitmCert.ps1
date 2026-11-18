#Requires -Version 5.1
<#
.SYNOPSIS
  Устанавливает MITM-сертификат: Windows (Schannel) + QuasarBridge appsettings + корень на Станцию Макс.
#>
[CmdletBinding()]
param(
    [switch]$SkipStation,
    [switch]$Regenerate,
    [string]$CertsDir = "",
    [string]$StationIp = "192.168.0.195",
    [string]$SshKey = "$env:USERPROFILE\Documents\ssh\id_rsa",
    [string]$AdbExe = "C:\platform-tools\adb.exe"
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($CertsDir)) { $CertsDir = Join-Path $RepoRoot 'certs' }

if ($Regenerate) {
    & (Join-Path $PSScriptRoot 'Generate-QuasarMitmCert.ps1') -OutDir $CertsDir
}

$fullchain = Join-Path $CertsDir 'fullchain.pem'
$key       = Join-Path $CertsDir 'quasar-all.key'
$pfx       = Join-Path $CertsDir 'quasar-mitm.pfx'
$metaPath  = Join-Path $CertsDir 'mitm-cert-meta.json'
$androidGlob = Join-Path $CertsDir '*.0'

if (-not (Test-Path $fullchain)) {
    & (Join-Path $PSScriptRoot 'Generate-QuasarMitmCert.ps1') -OutDir $CertsDir
}
if (-not (Test-Path $metaPath)) { throw "Нет $metaPath — сначала Generate-QuasarMitmCert.ps1" }

$meta = Get-Content $metaPath -Raw | ConvertFrom-Json
$thumb = $meta.Thumbprint -replace ' ', ''

function Prompt-Default([string]$Label, [string]$Default) {
    $raw = Read-Host "$Label [$Default]"
    if ([string]::IsNullOrWhiteSpace($raw)) { return $Default }
    return $raw.Trim()
}

if (-not $PSBoundParameters.ContainsKey('StationIp')) {
    $StationIp = Prompt-Default 'IP станции (SSH root)' $StationIp
}
if (-not $PSBoundParameters.ContainsKey('SshKey')) {
    $SshKey = Prompt-Default 'SSH private key' $SshKey
}

Write-Host "`n==> Windows: import PFX в CurrentUser\My" -ForegroundColor Cyan
if (-not (Test-Path $pfx)) { throw "Нет $pfx" }
$imported = Import-PfxCertificate -FilePath $pfx -CertStoreLocation 'Cert:\CurrentUser\My' -Exportable
Write-Host "OK Subject=$($imported.Subject) Thumbprint=$($imported.Thumbprint)"

Write-Host "`n==> appsettings.json: пути и thumbprint" -ForegroundColor Cyan
$appSettings = Join-Path $RepoRoot 'appsettings.json'
$json = Get-Content $appSettings -Raw | ConvertFrom-Json
$json.QuasarBridge.TlsCertPath = $fullchain
$json.QuasarBridge.TlsKeyPath = $key
$json.QuasarBridge.TlsStoreThumbprint = $imported.Thumbprint
$json | ConvertTo-Json -Depth 10 | Set-Content $appSettings -Encoding UTF8
Write-Host "OK TlsCertPath=$fullchain"

if ($SkipStation) {
    Write-Host "SkipStation — корень на станцию не ставим." -ForegroundColor Yellow
} else {
    $caFile = Get-ChildItem $androidGlob | Select-Object -First 1
    if (-not $caFile) { throw "Нет *.0 в $CertsDir" }
    $remoteData = '/data/quasar-mitm/cacerts'
    Write-Host "`n==> Станция: root CA в $remoteData + /system/etc/security/cacerts" -ForegroundColor Cyan

    $stageSh = @"
#!/system/bin/sh
set -e
DST=$remoteData
SYS=/system/etc/security/cacerts
mkdir -p "`$DST"
cp /data/local/tmp/quasar-root-ca.0 "`$DST/$( $caFile.Name )"
chmod 644 "`$DST/$( $caFile.Name )"
mount -o remount,rw / 2>/dev/null
cp "`$DST/$( $caFile.Name )" "`$SYS/$( $caFile.Name )"
chmod 644 "`$SYS/$( $caFile.Name )"
chcon u:object_r:system_file:s0 "`$SYS/$( $caFile.Name )" 2>/dev/null || true
mount -o remount,ro / 2>/dev/null
ls -l "`$SYS/$( $caFile.Name )"
"@
    $localStage = Join-Path $env:TEMP 'install-quasar-ca.sh'
    $unix = ($stageSh -replace "`r`n", "`n") -replace "`r", "`n"
    [System.IO.File]::WriteAllText($localStage, $unix, (New-Object System.Text.UTF8Encoding $false))

    if (-not (Test-Path $SshKey)) { throw "SSH key not found: $SshKey" }
    $dev = "${StationIp}:5555"
    $pushed = $false
    if (Test-Path $AdbExe) {
        & $AdbExe connect $dev 2>&1 | Out-Null
        & $AdbExe -s $dev push $caFile.FullName /data/local/tmp/quasar-root-ca.0
        & $AdbExe -s $dev push $localStage /data/local/tmp/install-quasar-ca.sh
        if ($LASTEXITCODE -eq 0) { $pushed = $true }
    }
    if (-not $pushed) {
        & scp -i $SshKey -o StrictHostKeyChecking=accept-new $caFile.FullName "root@${StationIp}:/data/local/tmp/quasar-root-ca.0"
        & scp -i $SshKey -o StrictHostKeyChecking=accept-new $localStage "root@${StationIp}:/data/local/tmp/install-quasar-ca.sh"
    }
    $out = & ssh -i $SshKey -o StrictHostKeyChecking=accept-new "root@$StationIp" 'chmod 755 /data/local/tmp/install-quasar-ca.sh; sh /data/local/tmp/install-quasar-ca.sh' 2>&1
    Write-Host $out

    # Хук автозапуска (идемпотентный)
    $hookMarker = 'quasar-mitm-cacert'
    $caName = $caFile.Name
    $hookBlock = @"

# --- quasar-mitm-cacert: свой CA на каждый бут (verity сбрасывает /system) ---
if [ -f $remoteData/$caName ]; then
  mount -o remount,rw / 2>/dev/null
  cp $remoteData/$caName /system/etc/security/cacerts/$caName 2>/dev/null
  chmod 644 /system/etc/security/cacerts/$caName 2>/dev/null
  chcon u:object_r:system_file:s0 /system/etc/security/cacerts/$caName 2>/dev/null || true
  mount -o remount,ro / 2>/dev/null
fi
"@
    $hookPath = Join-Path $env:TEMP 'quasar-ca-hook.sh'
    $hookOnly = ($hookBlock -replace "`r`n", "`n") -replace "`r", "`n"
    [System.IO.File]::WriteAllText($hookPath, $hookOnly, (New-Object System.Text.UTF8Encoding $false))
    if ($pushed) {
        & $AdbExe -s $dev push $hookPath /data/local/tmp/quasar-ca-hook.snippet
    } else {
        & scp -i $SshKey -o StrictHostKeyChecking=accept-new $hookPath "root@${StationIp}:/data/local/tmp/quasar-ca-hook.snippet"
    }
    & ssh -i $SshKey -o StrictHostKeyChecking=accept-new "root@$StationIp" "grep -q $hookMarker /data/local/tmp/myscript-max.sh 2>/dev/null || cat /data/local/tmp/quasar-ca-hook.snippet >> /data/local/tmp/myscript-max.sh"
    Write-Host "OK автозапуск: блок $hookMarker в myscript-max.sh"
}

Write-Host @"

Готово. Перезапустите QuasarBridge.
Проверка с ПК:
  curl.exe -sk --resolve uniproxy.alice.yandex.net:443:$((Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -match '^192\.168\.' } | Select-Object -First 1).IPAddress) https://uniproxy.alice.yandex.net/healthz
На станции — перезагрузка или повторный install-ca, затем снова подключение к uniproxy.

Документация: docs\mitm-certificate-100y.md

"@ -ForegroundColor Green