<#
.SYNOPSIS
    Installs Nexora Printer LAN Receiver on the PC with the physical printer (PC2). Requires Administrator.
.DESCRIPTION
    1. Copies the app to "C:\Program Files\Nexora Printer LAN\Receiver" (previous version kept for rollback)
    2. Adds two inbound Windows Firewall rules, limited to NexoraReceiver.exe, the Private profile and the local subnet:
         TCP 9102 (jobs) and UDP 9101 (discovery)
    3. Starts the receiver at sign-in, adds a Start menu shortcut and an Apps & features entry
    The network profile is never changed by this script. If the LAN is marked Public the script tells you how to change it.
.PARAMETER RemoteAddress  Firewall scope for clients. Default LocalSubnet. Example: 192.168.0.0/24 or 192.168.0.43
.PARAMETER Rollback       Restore the previously installed version.
#>
param(
    [string]$RemoteAddress = 'LocalSubnet',
    [int]$TcpPort = 9102,
    [int]$UdpPort = 9101,
    [switch]$Rollback,
    [switch]$NoAutoStart,
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$target = Join-Path $NexoraRoot 'Receiver'
$exe = Join-Path $target 'NexoraReceiver.exe'
function Finish([int]$code) { if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }; exit $code }

try {
    Assert-Admin
    Assert-Platform
    Write-Host 'Nexora Printer LAN - Receiver setup' -ForegroundColor Cyan
    Stop-NexoraProcess 'NexoraReceiver'

    # Older prototypes (receiver.py / test_receiver.py) hold the same ports.
    foreach ($ep in @(Get-NetUDPEndpoint -LocalPort $UdpPort -ErrorAction SilentlyContinue) + @(Get-NetTCPConnection -State Listen -LocalPort $TcpPort -ErrorAction SilentlyContinue)) {
        if ($ep) {
            $p = Get-Process -Id $ep.OwningProcess -ErrorAction SilentlyContinue
            Write-Host "WARNING: port $($ep.LocalPort) is in use by '$($p.ProcessName)' (PID $($ep.OwningProcess)). Stop the old Nexora test receiver (Ctrl+C in its window) before starting Nexora Receiver." -ForegroundColor Yellow
        }
    }

    if ($Rollback) { Restore-Previous $target }
    else {
        $src = Join-Path $PSScriptRoot 'app'
        if (-not (Test-Path (Join-Path $src 'NexoraReceiver.exe'))) { throw "Package is incomplete: $src\NexoraReceiver.exe not found" }
        Install-Files $src $target
        Copy-Item (Join-Path $PSScriptRoot 'Uninstall-NexoraReceiver.ps1'), (Join-Path $PSScriptRoot 'Common.ps1'), (Join-Path $PSScriptRoot 'Diagnostics.ps1') $target -Force
    }

    # Firewall: scoped to the program, Private profile and LAN addresses only. Never Public/Domain-wide, never Any address.
    Get-NetFirewallRule -Group 'Nexora Printer LAN' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName 'Nexora Receiver - print jobs (TCP-In)' -Group 'Nexora Printer LAN' -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort $TcpPort -Program $exe -Profile Private -RemoteAddress $RemoteAddress | Out-Null
    New-NetFirewallRule -DisplayName 'Nexora Receiver - discovery (UDP-In)' -Group 'Nexora Printer LAN' -Direction Inbound -Action Allow `
        -Protocol UDP -LocalPort $UdpPort -Program $exe -Profile Private -RemoteAddress $RemoteAddress | Out-Null
    Write-Host "[ok] Firewall: TCP $TcpPort + UDP $UdpPort inbound, NexoraReceiver.exe only, Private profile, remote=$RemoteAddress"

    $public = Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Public'
    foreach ($n in $public) {
        Write-Host "NOTE: network '$($n.Name)' on $($n.InterfaceAlias) is marked PUBLIC, so the rules above do not apply to it." -ForegroundColor Yellow
        Write-Host '      If this is your trusted home/office LAN, change it to Private in Settings > Network & internet > (adapter) > Network profile type,' -ForegroundColor Yellow
        Write-Host "      or as admin: Set-NetConnectionProfile -InterfaceAlias '$($n.InterfaceAlias)' -NetworkCategory Private" -ForegroundColor Yellow
    }

    if ($NoAutoStart) { Remove-ItemProperty $RunKey -Name 'NexoraReceiver' -ErrorAction SilentlyContinue }
    else {
        Set-ItemProperty $RunKey -Name 'NexoraReceiver' -Value "`"$exe`" --minimized"
        Write-Host '[ok] Nexora Receiver starts at sign-in'
    }
    $menu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Nexora Printer LAN'
    New-Item -ItemType Directory -Force -Path $menu | Out-Null
    New-Shortcut (Join-Path $menu 'Nexora Receiver.lnk') $exe
    Register-Uninstall 'NexoraPrinterLAN.Receiver' 'Nexora Printer LAN Receiver' (Get-Item $exe).VersionInfo.ProductVersion $target (Join-Path $target 'Uninstall-NexoraReceiver.ps1') $exe
    Write-Host '[ok] Start menu shortcut and Apps & features entry'

    Start-AsUser $exe
    Write-Host ''
    Write-Host 'Done. The receiver window shows the pairing code to enter on each client PC. It starts in TEST MODE (saves, does not print).' -ForegroundColor Green
    Finish 0
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Finish 1
}
