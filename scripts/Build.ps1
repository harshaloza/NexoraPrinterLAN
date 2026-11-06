<#
.SYNOPSIS  Builds the whole Nexora Printer LAN solution (x64).
.EXAMPLE   .\scripts\Build.ps1                 # Debug
           .\scripts\Build.ps1 -Configuration Release
#>
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $sdk = (& dotnet --version) 2>$null
    if (-not $sdk) { throw '.NET SDK not found. Install the .NET 8 (or newer) SDK: https://dotnet.microsoft.com/download' }
    Write-Host "Using .NET SDK $sdk" -ForegroundColor Cyan
    & dotnet build NexoraPrinterLAN.sln -c $Configuration -p:Platform=x64 -nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
    Write-Host "Build succeeded ($Configuration)." -ForegroundColor Green
}
finally { Pop-Location }
