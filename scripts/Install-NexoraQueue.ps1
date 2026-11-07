<#
.SYNOPSIS
    Creates (or repairs) the "Nexora Printer LAN" Windows print queue on the CLIENT PC. Run as Administrator.

.DESCRIPTION
    Driver, in order of preference:
      1. "Nexora Printer LAN" - Nexora's own v4 driver from -DriverDir (signed driver package + NexoraDriverSigning.cer).
         Jobs leave the PC as XPS (vector, no PDF). The signing certificate is added to this PC's trusted
         certificates (Root + Trusted Publishers) only when the package is signed with Nexora's own certificate.
      2. "Microsoft Print To PDF" - Windows' inbox driver, used when no driver package is given or it cannot be
         installed. Jobs leave the PC as PDF.
    Port: a Local Port named \\.\pipe\NexoraPrinterLAN, owned by the running Nexora Printer LAN app.
    No port monitor, DLL or service is installed; test-signing and signature enforcement are never touched.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Install-NexoraQueue.ps1 -DriverDir "C:\Program Files\Nexora Printer LAN\driver"
    powershell -ExecutionPolicy Bypass -File .\Install-NexoraQueue.ps1            # inbox PDF driver only
#>
[CmdletBinding()]
param(
    [string]$QueueName = 'Nexora Printer LAN',
    [string]$PipeName = 'NexoraPrinterLAN',
    [string]$PaperSize = 'A4',
    [string]$DriverDir,
    [switch]$SetDefault,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$nexoraDriver = 'Nexora Printer LAN'
$inboxDriver = 'Microsoft Print To PDF'
$port = "\\.\pipe\$PipeName"

function Done([int]$code) {
    if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') { Read-Host 'Press Enter to close' | Out-Null }
    exit $code
}

# Installs the Nexora driver package; returns $true when the driver is ready to use.
function Install-NexoraDriver([string]$dir) {
    $inf = Join-Path $dir 'nexoraprinterlan.inf'
    if (-not (Test-Path $inf)) { Write-Host "[--] No Nexora driver package in '$dir'"; return $false }
    try {
        $cer = Join-Path $dir 'NexoraDriverSigning.cer'
        if (Test-Path $cer) {
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer
            foreach ($store in 'Root', 'TrustedPublisher') {
                if (-not (Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object Thumbprint -eq $cert.Thumbprint)) {
                    Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
                }
            }
            Write-Host "[ok] Nexora driver-signing certificate trusted ($($cert.Thumbprint.Substring(0, 8))...)"
        }
        $out = & pnputil.exe /add-driver $inf 2>&1
        if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 259) {   # 259 = already up to date
            Write-Host "[!!] pnputil could not add the driver package (exit $LASTEXITCODE): $($out -join ' ')" -ForegroundColor Yellow
            return $false
        }
        Add-PrinterDriver -Name $nexoraDriver
        Write-Host "[ok] Driver '$nexoraDriver' installed"
        return $true
    }
    catch {
        Write-Host "[!!] Nexora driver not installed: $($_.Exception.Message)" -ForegroundColor Yellow
        return $false
    }
}

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'This script must run as Administrator.' -ForegroundColor Red
    Done 10
}

try {
    Write-Host "Nexora Printer LAN - queue setup" -ForegroundColor Cyan

    # 1. Driver: Nexora's own if possible, otherwise the inbox PDF driver.
    $driver = $null
    if ($DriverDir -and (Install-NexoraDriver $DriverDir)) { $driver = $nexoraDriver }
    if (-not $driver) {
        if (-not (Get-PrinterDriver -Name $inboxDriver -ErrorAction SilentlyContinue)) {
            Write-Host "Neither the Nexora driver nor '$inboxDriver' is available." -ForegroundColor Red
            Write-Host "Enable 'Microsoft Print to PDF' in Settings > System > Optional features > More Windows features, then rerun."
            Done 11
        }
        $driver = $inboxDriver
        Write-Host "[ok] Using Windows' '$inboxDriver' driver (jobs are sent as PDF)"
    }

    # 2. Local port pointing at the app's named pipe.
    if (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue) {
        Write-Host "[ok] Port '$port' already exists"
    } else {
        Add-PrinterPort -Name $port
        Write-Host "[ok] Created Local Port '$port'"
    }

    # 3. Queue (an existing queue is switched to the chosen driver).
    $q = Get-Printer -Name $QueueName -ErrorAction SilentlyContinue
    if ($q) {
        if ($q.DriverName -ne $driver -or $q.PortName -ne $port) {
            Get-PrintJob -PrinterName $QueueName -ErrorAction SilentlyContinue | Where-Object { $_.JobStatus -notmatch 'Printing' } | Out-Null
            Set-Printer -Name $QueueName -DriverName $driver -PortName $port
            Write-Host "[ok] Queue '$QueueName' now uses driver '$driver'"
        } else {
            Write-Host "[ok] Queue '$QueueName' already installed"
        }
    } else {
        Add-Printer -Name $QueueName -DriverName $driver -PortName $port `
            -Comment 'Sends print jobs over the local network to a Nexora Receiver PC' -Location 'Nexora Printer LAN'
        Write-Host "[ok] Created queue '$QueueName'"
    }

    # Default paper for new jobs (applications can still choose any size the driver offers).
    if ($PaperSize) {
        try {
            Set-PrintConfiguration -PrinterName $QueueName -PaperSize $PaperSize
            Write-Host "[ok] Default paper size: $PaperSize"
        } catch {
            Write-Host "[!!] Could not set default paper ${PaperSize}: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    if ($SetDefault) {
        (New-Object -ComObject WScript.Network).SetDefaultPrinter($QueueName)
        Write-Host "[ok] Set '$QueueName' as default printer for this account"
    }

    $q = Get-Printer -Name $QueueName
    Write-Host ""
    Write-Host "Queue:  $($q.Name)"
    Write-Host "Driver: $($q.DriverName)"
    Write-Host "Port:   $($q.PortName)"
    Done 0
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Done 1
}
