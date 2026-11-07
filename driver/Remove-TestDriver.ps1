<#
.SYNOPSIS  PROTOTYPE cleanup: removes the XPS test printer, its port, the Nexora v4 driver and the trusted test certificate. Run as Administrator.
#>
param([switch]$NoPause, [switch]$KeepCertificate)
$ErrorActionPreference = 'Continue'
$queue = 'Nexora Printer LAN (XPS test)'
$port = '\\.\pipe\NexoraPrinterLAN-XPS'
if (Get-Printer -Name $queue -ErrorAction SilentlyContinue) {
    Get-PrintJob -PrinterName $queue -ErrorAction SilentlyContinue | Remove-PrintJob
    Remove-Printer -Name $queue; Write-Host "[ok] Removed printer '$queue'"
}
if (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue) { Remove-PrinterPort -Name $port; Write-Host "[ok] Removed port $port" }
if (Get-PrinterDriver -Name 'Nexora Printer LAN' -ErrorAction SilentlyContinue) {
    Remove-PrinterDriver -Name 'Nexora Printer LAN'; Write-Host '[ok] Removed printer driver'
}
foreach ($line in (pnputil.exe /enum-drivers) -join "`n" -split "`n`n") {
    if ($line -match 'Original Name:\s+nexoraprinterlan\.inf' -and $line -match 'Published Name:\s+(oem\d+\.inf)') {
        pnputil.exe /delete-driver $Matches[1] /uninstall | Out-Null; Write-Host "[ok] Removed $($Matches[1]) from the driver store"
    }
}
if (-not $KeepCertificate) {
    foreach ($store in 'Root', 'TrustedPublisher') {
        Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object Subject -eq 'CN=Nexora Printer LAN Driver Signing (local)' | Remove-Item
    }
    Write-Host '[ok] Removed the trusted Nexora test certificate'
}
if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }
