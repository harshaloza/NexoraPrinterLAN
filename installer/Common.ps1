# Shared helpers for the Nexora installers (dot-sourced). Windows PowerShell 5.1 compatible.

$NexoraRoot = Join-Path $env:ProgramFiles 'Nexora Printer LAN'
$UninstallKeyRoot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
$RunKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'

function Assert-Admin {
    $p = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host 'Run this installer as Administrator (use the .cmd launcher, which asks via UAC).' -ForegroundColor Red
        exit 10
    }
}

function Assert-Platform {
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'Nexora Printer LAN requires 64-bit Windows.' }
    $build = [Environment]::OSVersion.Version.Build
    if ($build -lt 19041) { throw "Windows 10 version 2004 (build 19041) or newer is required; this PC is build $build." }
}

function Stop-NexoraProcess([string]$name) {
    $procs = Get-Process $name -ErrorAction SilentlyContinue
    if (-not $procs) { return }
    Write-Host "Closing running $name..." -ForegroundColor Yellow
    $procs | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    Start-Sleep -Seconds 2
    Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force
}

# Copies a new build into place, keeping the previous one as <dir>.previous for rollback.
function Install-Files([string]$source, [string]$target) {
    $prev = "$target.previous"
    if (Test-Path $target) {
        Remove-Item $prev -Recurse -Force -ErrorAction SilentlyContinue
        Move-Item $target $prev
        Write-Host "[ok] Previous version kept at $prev (use -Rollback to restore)"
    }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item (Join-Path $source '*') $target -Recurse -Force
    # Files extracted from a downloaded zip carry Mark-of-the-Web; the installed copy is trusted by the admin who installed it.
    Get-ChildItem $target -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
    Write-Host "[ok] Installed files to $target"
}

function Restore-Previous([string]$target) {
    $prev = "$target.previous"
    if (-not (Test-Path $prev)) { throw "No previous version found at $prev" }
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    Move-Item $prev $target
    Write-Host "[ok] Rolled back to the previous version in $target"
}

function New-Shortcut([string]$lnk, [string]$exe, [string]$arguments = '') {
    $sh = New-Object -ComObject WScript.Shell
    $s = $sh.CreateShortcut($lnk)
    $s.TargetPath = $exe; $s.Arguments = $arguments; $s.WorkingDirectory = Split-Path $exe; $s.IconLocation = "$exe,0"
    $s.Save()
}

function Register-Uninstall([string]$id, [string]$name, [string]$version, [string]$dir, [string]$uninstallScript, [string]$icon) {
    $k = Join-Path $UninstallKeyRoot $id
    New-Item -Path $k -Force | Out-Null
    Set-ItemProperty $k DisplayName $name
    Set-ItemProperty $k DisplayVersion $version
    Set-ItemProperty $k Publisher 'Nexora'
    Set-ItemProperty $k InstallLocation $dir
    Set-ItemProperty $k DisplayIcon $icon
    Set-ItemProperty $k UninstallString "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""
    Set-ItemProperty $k NoModify 1 -Type DWord
    Set-ItemProperty $k NoRepair 1 -Type DWord
}

# Starts a program as the signed-in (non-elevated) user, so per-user data and DPAPI keys belong to that user.
function Start-AsUser([string]$exe) {
    Start-Process explorer.exe -ArgumentList "`"$exe`""
}
