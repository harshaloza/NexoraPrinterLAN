<#
.SYNOPSIS  Runs unit and integration tests and writes TRX results to TestResults\.
.DESCRIPTION
    Integration tests use loopback TCP/UDP on random ports (no firewall changes).
    Two tests use the real Windows print system and are skipped with a message when not applicable:
      * queue capture: needs the "Nexora Printer LAN" queue (scripts\Install-NexoraQueue.ps1) and Nexora Client NOT running
      * receiver print path: needs the inbox "Microsoft Print to PDF" queue
.EXAMPLE   .\scripts\Test.ps1
           .\scripts\Test.ps1 -UnitOnly
#>
param([switch]$UnitOnly, [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$results = Join-Path $root 'TestResults'
$projects = @('tests\Unit\Nexora.Tests.Unit\Nexora.Tests.Unit.csproj')
if (-not $UnitOnly) { $projects += 'tests\Integration\Nexora.Tests.Integration\Nexora.Tests.Integration.csproj' }
$failed = $false
foreach ($p in $projects) {
    Write-Host "== $p" -ForegroundColor Cyan
    & dotnet test (Join-Path $root $p) -c $Configuration -p:Platform=x64 -nologo `
        --logger "trx;LogFilePrefix=$([IO.Path]::GetFileNameWithoutExtension($p))" --logger "console;verbosity=normal" --results-directory $results
    if ($LASTEXITCODE -ne 0) { $failed = $true }
}
if ($failed) { Write-Host 'Some tests FAILED.' -ForegroundColor Red; exit 1 }
Write-Host "All tests passed. Results: $results" -ForegroundColor Green
