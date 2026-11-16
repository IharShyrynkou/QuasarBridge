#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$thumb = '1B607464F66256BD73B6A9BB6B7FFCD33A345C68'
$storeCert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $thumb }
if (-not $storeCert) {
    $pfx = Join-Path $root 'certs\quasar-mitm.pfx'
    if (-not (Test-Path $pfx)) {
        throw "Missing $pfx and store thumbprint $thumb. Import Pictures\certs first."
    }
    Write-Host "Importing PFX into CurrentUser\My..."
    certutil -f -p quasar2024 -user -importpfx $pfx NoExport | Out-Host
}

dotnet build -c Release --nologo -v q
Write-Host "Starting QuasarBridge on https://192.168.0.176:443 ..."
dotnet run -c Release --no-build --no-launch-profile --project (Join-Path $root 'QuasarBridge.csproj')
