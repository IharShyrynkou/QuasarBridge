#Requires -Version 5.1
<#
.SYNOPSIS
  Корневой CA и leaf MITM (~100 лет) для QuasarBridge.
#>
[CmdletBinding()]
param(
    [int]$ValidDays = 36500,
    [string]$OutDir = "",
    [string]$OpenSsl = ""
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $RepoRoot 'certs'
}

function Find-OpenSsl {
    if ($OpenSsl -and (Test-Path $OpenSsl)) { return $OpenSsl }
    $candidates = @(
        'C:\Program Files\Git\usr\bin\openssl.exe',
        'C:\Program Files\OpenSSL-Win64\bin\openssl.exe',
        'C:\Program Files (x86)\OpenSSL-Win32\bin\openssl.exe'
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    $w = Get-Command openssl -ErrorAction SilentlyContinue
    if ($w) { return $w.Source }
    throw 'openssl не найден. Установите Git for Windows или OpenSSL Win64.'
}

$ssl = Find-OpenSsl
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$rootKey  = Join-Path $OutDir 'root-ca.key'
$rootCrt  = Join-Path $OutDir 'root-ca.pem'
$leafKey  = Join-Path $OutDir 'quasar-all.key'
$leafCsr  = Join-Path $OutDir 'quasar-mitm.csr'
$leafCrt  = Join-Path $OutDir 'quasar-mitm.pem'
$fullchain = Join-Path $OutDir 'fullchain.pem'
$pfx      = Join-Path $OutDir 'quasar-mitm.pfx'
$sanCnf   = Join-Path $RepoRoot 'certs\san-mitm.cnf'
$metaJson = Join-Path $OutDir 'mitm-cert-meta.json'

if (-not (Test-Path $sanCnf)) { throw "Нет $sanCnf" }

Write-Host "OpenSSL: $ssl"
Write-Host "Каталог: $OutDir"
Write-Host "Срок (дней): $ValidDays (~$([math]::Round($ValidDays / 365.25, 1)) лет)"

# Backup старых файлов
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
foreach ($f in @($rootKey, $rootCrt, $leafKey, $leafCrt, $fullchain, $pfx)) {
    if (Test-Path $f) {
        Copy-Item $f "$f.bak.$stamp" -Force
    }
}

# Root CA
$rootSubj = '/CN=QuasarBridge Homelab Root CA/O=QuasarBridge Homelab/C=RU'
& $ssl genrsa -out $rootKey 4096
& $ssl req -x509 -new -nodes -key $rootKey -sha256 -days $ValidDays -out $rootCrt -subj $rootSubj

# Leaf
& $ssl genrsa -out $leafKey 2048
& $ssl req -new -key $leafKey -out $leafCsr -config $sanCnf
& $ssl x509 -req -in $leafCsr -CA $rootCrt -CAkey $rootKey -CAcreateserial `
    -out $leafCrt -days $ValidDays -sha256 -extfile $sanCnf -extensions req_ext

Get-Content $leafCrt, $rootCrt | Set-Content -Encoding ascii $fullchain

# Android hash для cacerts
$hashLine = & $ssl x509 -inform PEM -subject_hash_old -in $rootCrt 2>$null | Select-Object -First 1
$hashLine = $hashLine.Trim()
$androidCa = Join-Path $OutDir "$hashLine.0"
Copy-Item $rootCrt $androidCa -Force

# PFX (пустой пароль)
if (Test-Path $pfx) { Remove-Item $pfx -Force }
& $ssl pkcs12 -export -out $pfx -inkey $leafKey -in $fullchain -passout pass:

$leafObj = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($leafCrt)
$rootObj = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($rootCrt)

$meta = [ordered]@{
    GeneratedAt  = (Get-Date).ToString('o')
    ValidDays    = $ValidDays
    LeafNotAfter = $leafObj.NotAfter.ToUniversalTime().ToString('o')
    RootNotAfter = $rootObj.NotAfter.ToUniversalTime().ToString('o')
    LeafSubject  = $leafObj.Subject
    RootSubject  = $rootObj.Subject
    Thumbprint   = $leafObj.Thumbprint
    AndroidCaFile = Split-Path $androidCa -Leaf
    SanConfig    = 'certs/san-mitm.cnf'
    Fullchain    = $fullchain
    Key          = $leafKey
}
$meta | ConvertTo-Json | Set-Content $metaJson -Encoding UTF8

Remove-Item $leafCsr -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Готово:" -ForegroundColor Green
Write-Host "  root:       $rootCrt"
Write-Host "  leaf:       $leafCrt"
Write-Host "  fullchain:  $fullchain"
Write-Host "  key:        $leafKey"
Write-Host "  pfx:        $pfx"
Write-Host "  station:    $androidCa"
Write-Host "  Thumbprint: $($leafObj.Thumbprint)"
Write-Host "  NotAfter:   $($leafObj.NotAfter.ToUniversalTime().ToString('yyyy-MM-dd')) (UTC)"
Write-Host "  meta:       $metaJson"