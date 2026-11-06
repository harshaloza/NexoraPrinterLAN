<#
.SYNOPSIS
    Removes the "Nexora Printer LAN" queue and its named-pipe Local Port (CLIENT PC). Requires Administrator.
    The inbox "Microsoft Print To PDF" driver is left untouched (it belongs to Windows).
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
    Done 0
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Done 1
}
