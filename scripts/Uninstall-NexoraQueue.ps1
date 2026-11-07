<#
.SYNOPSIS
    Removes the "Nexora Printer LAN" queue and its named-pipe Local Port (CLIENT PC). Requires Administrator.
    Also removes Nexora's own printer driver, its driver-store package and the trusted Nexora driver-signing
    certificate - unless another printer still uses the driver. The inbox "Microsoft Print To PDF" driver belongs to
    Windows and is left untouched.
#>
[CmdletBinding()]
param(
    [string]$QueueName = 'Nexora Printer LAN',
    [string]$PipeName = 'NexoraPrinterLAN',
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$port = "\\.\pipe\$PipeName"

function Done([int]$code) {
    if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') { Read-Host 'Press Enter to close' | Out-Null }
    exit $code
}

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'This script must run as Administrator.' -ForegroundColor Red
    Done 10
}

try {
    if (Get-Printer -Name $QueueName -ErrorAction SilentlyContinue) {
        Get-PrintJob -PrinterName $QueueName -ErrorAction SilentlyContinue | Remove-PrintJob -ErrorAction SilentlyContinue
        Remove-Printer -Name $QueueName
        Write-Host "[ok] Removed queue '$QueueName'"
    } else {
        Write-Host "[ok] Queue '$QueueName' not present"
    }
    $users = Get-Printer | Where-Object PortName -eq $port
    if ($users) {
        Write-Host "Port '$port' is still used by: $($users.Name -join ', '); leaving it." -ForegroundColor Yellow
    } elseif (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue) {
        Remove-PrinterPort -Name $port
        Write-Host "[ok] Removed port '$port'"
    }

    # Nexora's own driver, package and certificate.
    $driverUsers = Get-Printer -ErrorAction SilentlyContinue | Where-Object DriverName -eq 'Nexora Printer LAN'
    if ($driverUsers) {
        Write-Host "Driver 'Nexora Printer LAN' is still used by: $($driverUsers.Name -join ', '); leaving it." -ForegroundColor Yellow
    } else {
        if (Get-PrinterDriver -Name 'Nexora Printer LAN' -ErrorAction SilentlyContinue) {
            Remove-PrinterDriver -Name 'Nexora Printer LAN'
            Write-Host "[ok] Removed printer driver 'Nexora Printer LAN'"
        }
        foreach ($block in ((pnputil.exe /enum-drivers) -join "`n") -split "`n\s*`n") {
            if ($block -match 'Original Name:\s+nexoraprinterlan\.inf' -and $block -match 'Published Name:\s+(oem\d+\.inf)') {
                pnputil.exe /delete-driver $Matches[1] | Out-Null
                Write-Host "[ok] Removed driver package $($Matches[1])"
            }
        }
        foreach ($store in 'Root', 'TrustedPublisher') {
            Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object Subject -eq 'CN=Nexora Printer LAN Driver Signing (local)' | Remove-Item
        }
        Write-Host "[ok] Removed the Nexora driver-signing certificate from this PC's trusted certificates"
    }
    Done 0
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Done 1
}
