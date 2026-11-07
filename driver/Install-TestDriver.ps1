<#
.SYNOPSIS
    PROTOTYPE: installs the signed Nexora v4 driver and a separate test printer on this PC. Run as Administrator.
.DESCRIPTION
    1. Trusts the local Nexora driver-signing certificate (LocalMachine Root + TrustedPublisher) - only needed for the
       self-signed certificate; a purchased certificate is already trusted.
    2. Adds the driver package to the Windows driver store (pnputil) and installs the printer driver "Nexora Printer LAN".
    3. Adds port \\.\pipe\NexoraPrinterLAN-XPS and the printer "Nexora Printer LAN (XPS test)".
    The existing "Nexora Printer LAN" printer and app are not changed. Undo with Remove-TestDriver.ps1.
#>
param([switch]$NoPause)
$ErrorActionPreference = 'Stop'
$pkg = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\driver'
$queue = 'Nexora Printer LAN (XPS test)'
$port = '\\.\pipe\NexoraPrinterLAN-XPS'
function Done([int]$c) { if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }; exit $c }

try {
    $cer = Join-Path $pkg 'NexoraDriverSigning.cer'
    if (Test-Path $cer) {
        Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
        Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
        Write-Host '[ok] Nexora driver-signing certificate trusted on this PC'
    }
    $inf = Join-Path $pkg 'NexoraV4\nexoraprinterlan.inf'
    $out = & pnputil.exe /add-driver $inf 2>&1
    $out | ForEach-Object { "  $_" }
    if ($LASTEXITCODE -ne 0) { throw "pnputil failed ($LASTEXITCODE)" }
    Add-PrinterDriver -Name 'Nexora Printer LAN'
    Write-Host '[ok] Driver "Nexora Printer LAN" installed'
    if (-not (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue)) { Add-PrinterPort -Name $port }
    if (-not (Get-Printer -Name $queue -ErrorAction SilentlyContinue)) {
        Add-Printer -Name $queue -DriverName 'Nexora Printer LAN' -PortName $port -Comment 'Nexora v4 driver prototype (XPS)'
    }
    Get-Printer -Name $queue | Format-List Name, DriverName, PortName, PrinterStatus
    Done 0
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Done 1
}
