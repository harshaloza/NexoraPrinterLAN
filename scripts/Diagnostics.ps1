<#
.SYNOPSIS
    Read-only health check for Nexora Printer LAN. Run on either PC; changes nothing.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Diagnostics.ps1
    powershell -ExecutionPolicy Bypass -File .\Diagnostics.ps1 -Receiver 192.168.0.31
#>
param(
    [string]$Receiver,
    [int]$TcpPort = 9102,
    [int]$UdpPort = 9101
)

function Section($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Ok($t) { Write-Host "  [ok]   $t" -ForegroundColor Green }
function Warn($t) { Write-Host "  [warn] $t" -ForegroundColor Yellow }
function Bad($t) { Write-Host "  [fail] $t" -ForegroundColor Red }

Section 'System'
$os = Get-CimInstance Win32_OperatingSystem
Write-Host "  $($os.Caption) $($os.Version) $($env:PROCESSOR_ARCHITECTURE) on $env:COMPUTERNAME"
$spool = Get-Service Spooler
if ($spool.Status -eq 'Running') { Ok 'Print Spooler running' } else { Bad "Print Spooler is $($spool.Status)" }

Section 'Network'
Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
    ForEach-Object { Write-Host "  $($_.InterfaceAlias): $($_.IPAddress)/$($_.PrefixLength)" }
foreach ($p in Get-NetConnectionProfile) {
    if ($p.NetworkCategory -eq 'Public') {
        Warn "Network '$($p.Name)' ($($p.InterfaceAlias)) is PUBLIC. Receiver firewall rules apply to Private networks only. On a trusted home/office LAN set it to Private: Settings > Network & internet > Wi-Fi/Ethernet > (network) > Network profile type > Private."
    } else { Ok "Network '$($p.Name)' is $($p.NetworkCategory)" }
}

Section 'Nexora Client (this PC prints)'
$q = Get-Printer -Name 'Nexora Printer LAN' -ErrorAction SilentlyContinue
if ($q) {
    if ($q.DriverName -eq 'Microsoft Print To PDF' -and $q.PortName -eq '\\.\pipe\NexoraPrinterLAN') { Ok "Queue 'Nexora Printer LAN' installed (driver $($q.DriverName), port $($q.PortName))" }
    else { Warn "Queue exists but driver='$($q.DriverName)' port='$($q.PortName)' - rerun Install-NexoraQueue.ps1" }
    $jobs = Get-PrintJob -PrinterName 'Nexora Printer LAN' -ErrorAction SilentlyContinue
    if ($jobs) { Warn "$($jobs.Count) job(s) waiting in the Windows queue: $(($jobs | ForEach-Object { "$($_.DocumentName) [$($_.JobStatus)]" }) -join '; ')" }
} else { Write-Host '  Queue not installed (normal on the receiver PC)' }
if (Get-Process NexoraClient -ErrorAction SilentlyContinue) { Ok 'NexoraClient.exe running' } elseif ($q) { Warn 'NexoraClient.exe is not running - printed jobs will wait in the Windows queue' }
if (Test-Path '\\.\pipe\NexoraPrinterLAN') { Ok 'Capture pipe \\.\pipe\NexoraPrinterLAN is open' } elseif ($q) { Warn 'Capture pipe is not open' }

Section 'Nexora Receiver (this PC has the printer)'
$rx = Get-Process NexoraReceiver -ErrorAction SilentlyContinue
if ($rx) { Ok 'NexoraReceiver.exe running' } else { Write-Host '  NexoraReceiver.exe not running (normal on the client PC)' }
foreach ($ep in Get-NetTCPConnection -State Listen -LocalPort $TcpPort -ErrorAction SilentlyContinue) {
    $proc = (Get-Process -Id $ep.OwningProcess -ErrorAction SilentlyContinue).ProcessName
    if ($proc -eq 'NexoraReceiver' -or $proc -eq 'nexora') { Ok "TCP $TcpPort listening ($proc)" } else { Warn "TCP $TcpPort is held by '$proc' (PID $($ep.OwningProcess)) - an old test receiver?" }
}
foreach ($ep in Get-NetUDPEndpoint -LocalPort $UdpPort -ErrorAction SilentlyContinue) {
    $proc = (Get-Process -Id $ep.OwningProcess -ErrorAction SilentlyContinue).ProcessName
    if ($proc -eq 'NexoraReceiver' -or $proc -eq 'nexora') { Ok "UDP $UdpPort bound ($proc)" } else { Warn "UDP $UdpPort is held by '$proc' (PID $($ep.OwningProcess)) - stop the Phase 1 receiver.py" }
}
$rules = Get-NetFirewallRule -Group 'Nexora Printer LAN' -ErrorAction SilentlyContinue
if ($rules) {
    foreach ($r in $rules) {
        $addr = ($r | Get-NetFirewallAddressFilter).RemoteAddress -join ','
        Write-Host "  firewall: $($r.DisplayName) enabled=$($r.Enabled) profile=$($r.Profile) remote=$addr"
    }
} elseif ($rx) { Warn 'No Nexora firewall rules found; other PCs cannot reach this receiver' }
Get-Printer -ErrorAction SilentlyContinue | Where-Object Name -ne 'Nexora Printer LAN' | ForEach-Object {
    Write-Host ("  printer: {0,-34} status={1,-12} driver={2}" -f $_.Name, $_.PrinterStatus, $_.DriverName)
}

if ($Receiver) {
    Section "Connectivity to $Receiver"
    if (Test-Connection $Receiver -Count 2 -Quiet) { Ok 'Ping OK' } else { Warn 'No ping reply (ICMP may be blocked; continuing)' }
    $t = Test-NetConnection $Receiver -Port $TcpPort -WarningAction SilentlyContinue
    if ($t.TcpTestSucceeded) { Ok "TCP $TcpPort reachable" } else { Bad "TCP $TcpPort NOT reachable: receiver not running, firewall, or Public network profile on the receiver" }
}

$cli = @((Join-Path $PSScriptRoot 'app\nexora.exe'), (Join-Path $PSScriptRoot 'nexora.exe')) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($cli) {
    Section 'Discovery (UDP broadcast)'
    & $cli discover --seconds 3
}
Write-Host ''
