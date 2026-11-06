<#
.SYNOPSIS
    Creates the "Nexora Printer LAN" Windows print queue on the CLIENT PC (PC1).

.DESCRIPTION
    Uses only components that ship with Windows:
      * Driver:  "Microsoft Print To PDF" (inbox, Microsoft-signed v4 driver; renders application output to PDF)
      * Port:    a Local Port named \\.\pipe\NexoraPrinterLAN (inbox Local Monitor)
    No driver package, DLL or port monitor is installed. The Nexora Client app owns the named pipe and
    forwards each job to the receiver. Requires Administrator (Windows will show a UAC prompt).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Install-NexoraQueue.ps1
    powershell -ExecutionPolicy Bypass -File .\Install-NexoraQueue.ps1 -SetDefault
#>
[CmdletBinding()]
param(
    [string]$QueueName = 'Nexora Printer LAN',
    [string]$PipeName = 'NexoraPrinterLAN',
    [string]$PaperSize = 'A4',
    [switch]$SetDefault,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$driver = 'Microsoft Print To PDF'
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
    Write-Host "Nexora Printer LAN - queue setup" -ForegroundColor Cyan

    # 1. Inbox driver. It is part of the "Microsoft Print to PDF" Windows feature; we never enable features silently.
    if (-not (Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue)) {
        Write-Host "Driver '$driver' is not installed." -ForegroundColor Yellow
        Write-Host "Enable it in: Settings > System > Optional features > More Windows features > 'Microsoft Print to PDF', then rerun."
        Done 11
    }
    Write-Host "[ok] Driver '$driver' present"

    # 2. Local port pointing at the client's named pipe.
    if (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue) {
        Write-Host "[ok] Port '$port' already exists"
    } else {
        Add-PrinterPort -Name $port
        Write-Host "[ok] Created Local Port '$port'"
    }

    # 3. Queue.
    $q = Get-Printer -Name $QueueName -ErrorAction SilentlyContinue
    if ($q) {
        if ($q.DriverName -ne $driver -or $q.PortName -ne $port) {
            Set-Printer -Name $QueueName -DriverName $driver -PortName $port
            Write-Host "[ok] Corrected existing queue '$QueueName' (driver/port)"
        } else {
            Write-Host "[ok] Queue '$QueueName' already installed"
        }
    } else {
        Add-Printer -Name $QueueName -DriverName $driver -PortName $port `
            -Comment 'Sends print jobs over the local network to a Nexora Receiver PC' -Location 'Nexora Printer LAN'
        Write-Host "[ok] Created queue '$QueueName'"
    }

    # Default paper for new jobs (applications can still choose any size the driver offers: A4, A3, Letter, Legal, ...).
    if ($PaperSize) {
        Set-PrintConfiguration -PrinterName $QueueName -PaperSize $PaperSize
        Write-Host "[ok] Default paper size: $PaperSize"
    }

    if ($SetDefault) {
        # Default printer is per-user; set it for the user who launched the installer via the app instead when possible.
        (New-Object -ComObject WScript.Network).SetDefaultPrinter($QueueName)
        Write-Host "[ok] Set '$QueueName' as default printer for this account"
    }

    $q = Get-Printer -Name $QueueName
    Write-Host ""
    Write-Host "Queue:  $($q.Name)"
    Write-Host "Driver: $($q.DriverName)"
    Write-Host "Port:   $($q.PortName)"
    Write-Host "Keep Nexora Client running (it starts with Windows) so print jobs can be forwarded." -ForegroundColor Green
    Done 0
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Done 1
}
