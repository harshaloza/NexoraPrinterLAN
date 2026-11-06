<#
.SYNOPSIS  Removes Nexora Printer LAN Receiver (app, firewall rules, autostart, shortcut). Requires Administrator.
.PARAMETER RemoveData  Also delete %LOCALAPPDATA%\Nexora\Receiver of the current user (config, pairing code, received documents, logs).
#>
param([switch]$RemoveData, [switch]$NoPause)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$target = Join-Path $NexoraRoot 'Receiver'
function Finish([int]$code) { if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }; exit $code }

try {
    Assert-Admin
    Stop-NexoraProcess 'NexoraReceiver'
    Get-NetFirewallRule -Group 'Nexora Printer LAN' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Write-Host '[ok] Firewall rules removed'
    Remove-ItemProperty $RunKey -Name 'NexoraReceiver' -ErrorAction SilentlyContinue
    Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'NexoraReceiver' -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Nexora Printer LAN\Nexora Receiver.lnk') -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $UninstallKeyRoot 'NexoraPrinterLAN.Receiver') -Recurse -ErrorAction SilentlyContinue
    Remove-Item "$target.previous" -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem $target -Exclude 'Uninstall-NexoraReceiver.ps1', 'Common.ps1' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    if ($RemoveData) {
        Remove-Item (Join-Path $env:LOCALAPPDATA 'Nexora\Receiver') -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host '[ok] Removed receiver data (including received documents) for this user'
    } else {
        Write-Host "Received documents and settings were kept in $(Join-Path $env:LOCALAPPDATA 'Nexora\Receiver')"
    }
    Start-Process cmd.exe -ArgumentList "/c timeout /t 2 >nul & rmdir /s /q `"$target`"" -WindowStyle Hidden
    Write-Host 'Nexora Printer LAN Receiver removed.' -ForegroundColor Green
    Finish 0
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Finish 1
}
