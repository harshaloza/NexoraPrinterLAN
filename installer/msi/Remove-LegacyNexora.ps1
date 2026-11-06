<#
.SYNOPSIS
    Run by Setup (as SYSTEM) to remove the separate Nexora Client / Receiver apps of version 1.0.x.
.DESCRIPTION
    1.0.x was installed by PowerShell scripts into "Program Files\Nexora Printer LAN\Client|Receiver".
    This removes those program folders, autostart entries, Start menu shortcuts, Apps & features entries and the
    old firewall rules. It keeps all user data (%LOCALAPPDATA%\Nexora: settings, pairing codes, job history) and
    the "Nexora Printer LAN" print queue, so the new app continues where the old ones left off.
    Safe to run when nothing old is installed.
#>
$ErrorActionPreference = 'Continue'
$root = Join-Path $env:ProgramFiles 'Nexora Printer LAN'
$run = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run'

foreach ($name in 'NexoraClient', 'NexoraReceiver') {
    Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 500

foreach ($name in 'NexoraClient', 'NexoraReceiver') {
    Remove-ItemProperty "HKLM:\$run" -Name $name -ErrorAction SilentlyContinue
    # Per-user autostart entries of every loaded user profile.
    foreach ($hive in Get-ChildItem Registry::HKEY_USERS -ErrorAction SilentlyContinue) {
        Remove-ItemProperty "Registry::$($hive.Name)\$run" -Name $name -ErrorAction SilentlyContinue
    }
}

foreach ($id in 'NexoraPrinterLAN.Client', 'NexoraPrinterLAN.Receiver') {
    Remove-Item "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$id" -Recurse -ErrorAction SilentlyContinue
}

# 1.0.x firewall rules were grouped "Nexora Printer LAN"; Setup's own rules are not grouped.
Get-NetFirewallRule -Group 'Nexora Printer LAN' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue

$menu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Nexora Printer LAN'
foreach ($lnk in 'Nexora Client.lnk', 'Nexora Receiver.lnk') { Remove-Item (Join-Path $menu $lnk) -ErrorAction SilentlyContinue }

foreach ($dir in 'Client', 'Client.previous', 'Receiver', 'Receiver.previous') {
    Remove-Item (Join-Path $root $dir) -Recurse -Force -ErrorAction SilentlyContinue
}
exit 0
