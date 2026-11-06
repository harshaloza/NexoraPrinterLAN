<#
.SYNOPSIS
    Installs Nexora Printer LAN Client on the PC that prints (PC1). Requires Administrator.
.DESCRIPTION
    1. Copies the app to "C:\Program Files\Nexora Printer LAN\Client" (previous version kept for rollback)
    2. Creates the "Nexora Printer LAN" queue (inbox Microsoft Print To PDF driver + \\.\pipe\NexoraPrinterLAN port)
    3. Starts the client at sign-in for all users, adds a Start menu shortcut and an Apps & features entry
    No driver, DLL, service or port monitor is installed. No inbound firewall rule is needed on the client.
.PARAMETER Rollback   Restore the previously installed version instead of installing.
.PARAMETER NoAutoStart  Do not start the client automatically at sign-in.
.PARAMETER PaperSize  Default paper for the queue (A4, Letter, A3, ...). Applications can still choose any size.
#>
param([switch]$Rollback, [switch]$NoAutoStart, [string]$PaperSize = 'A4', [switch]$NoPause)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$target = Join-Path $NexoraRoot 'Client'
$exe = Join-Path $target 'NexoraClient.exe'

function Finish([int]$code) { if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }; exit $code }

try {
    Assert-Admin
    Assert-Platform
    Write-Host 'Nexora Printer LAN - Client setup' -ForegroundColor Cyan
    Stop-NexoraProcess 'NexoraClient'

    if ($Rollback) { Restore-Previous $target }
    else {
        $src = Join-Path $PSScriptRoot 'app'
        if (-not (Test-Path (Join-Path $src 'NexoraClient.exe'))) { throw "Package is incomplete: $src\NexoraClient.exe not found" }
        Install-Files $src $target
        New-Item -ItemType Directory -Force -Path "$target\scripts" | Out-Null
        Copy-Item (Join-Path $PSScriptRoot 'Install-NexoraQueue.ps1'), (Join-Path $PSScriptRoot 'Uninstall-NexoraQueue.ps1') "$target\scripts" -Force
        Copy-Item (Join-Path $PSScriptRoot 'Uninstall-NexoraClient.ps1'), (Join-Path $PSScriptRoot 'Common.ps1'), (Join-Path $PSScriptRoot 'Diagnostics.ps1') $target -Force
    }

    & (Join-Path $target 'scripts\Install-NexoraQueue.ps1') -PaperSize $PaperSize -NoPause
    if ($LASTEXITCODE -ne 0) { throw "Queue setup failed (exit $LASTEXITCODE)" }

    if ($NoAutoStart) { Remove-ItemProperty $RunKey -Name 'NexoraClient' -ErrorAction SilentlyContinue }
    else {
        Set-ItemProperty $RunKey -Name 'NexoraClient' -Value "`"$exe`" --minimized"
        Write-Host '[ok] Nexora Client starts at sign-in (all users)'
    }
    $menu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Nexora Printer LAN'
    New-Item -ItemType Directory -Force -Path $menu | Out-Null
    New-Shortcut (Join-Path $menu 'Nexora Client.lnk') $exe
    Register-Uninstall 'NexoraPrinterLAN.Client' 'Nexora Printer LAN Client' (Get-Item $exe).VersionInfo.ProductVersion $target (Join-Path $target 'Uninstall-NexoraClient.ps1') $exe
    Write-Host '[ok] Start menu shortcut and Apps & features entry'

    Start-AsUser $exe
    Write-Host ''
    Write-Host 'Done. Next: open Nexora Client > Settings and enter the pairing code shown on the receiver PC.' -ForegroundColor Green
    Finish 0
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Finish 1
}
