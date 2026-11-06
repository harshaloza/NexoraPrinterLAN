<#
.SYNOPSIS  Removes Nexora Printer LAN Client (app, queue, port, autostart, shortcut). Requires Administrator.
.PARAMETER RemoveData  Also delete per-user data (%LOCALAPPDATA%\Nexora\Client: config, outbox, logs) of the current user.
#>
param([switch]$RemoveData, [switch]$NoPause)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$target = Join-Path $NexoraRoot 'Client'
function Finish([int]$code) { if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }; exit $code }

try {
    Assert-Admin
    Stop-NexoraProcess 'NexoraClient'
    $queueScript = Join-Path $target 'scripts\Uninstall-NexoraQueue.ps1'
    if (-not (Test-Path $queueScript)) { $queueScript = Join-Path $PSScriptRoot 'Uninstall-NexoraQueue.ps1' }
    if (Test-Path $queueScript) { & $queueScript -NoPause }
    Remove-ItemProperty $RunKey -Name 'NexoraClient' -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Nexora Printer LAN\Nexora Client.lnk') -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $UninstallKeyRoot 'NexoraPrinterLAN.Client') -Recurse -ErrorAction SilentlyContinue
    Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'NexoraClient' -ErrorAction SilentlyContinue
    # The running script may live inside $target; remove it last and tolerate the locked file.
    Remove-Item "$target.previous" -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem $target -Exclude 'Uninstall-NexoraClient.ps1', 'Common.ps1' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    if ($RemoveData) {
        Remove-Item (Join-Path $env:LOCALAPPDATA 'Nexora\Client') -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host '[ok] Removed client data for this user'
    }
    Start-Process cmd.exe -ArgumentList "/c timeout /t 2 >nul & rmdir /s /q `"$target`"" -WindowStyle Hidden
    Write-Host 'Nexora Printer LAN Client removed.' -ForegroundColor Green
    Finish 0
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Finish 1
}
