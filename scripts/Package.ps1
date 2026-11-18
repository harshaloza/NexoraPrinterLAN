<#
.SYNOPSIS  Builds the Nexora Printer LAN Setup (MSI) into release\.
.DESCRIPTION
    1. Runs the tests (unless -SkipTests)
    2. Publishes NexoraPrinterLAN.exe and nexora.exe as self-contained single-file x64 programs (no .NET needed on target PCs)
    3. Builds release\NexoraPrinterLAN-<ver>-x64.msi with WiX 4 (UI, Util and Firewall extensions from installer\msi\.wix)
    One MSI serves both PCs: Setup asks whether this PC prints to another PC, receives and prints, or both.
    Binaries and the MSI are not code-signed unless -SignCertThumbprint is given (see docs\INSTALLATION.md, "Signing").
#>
param(
    [string]$Version = '1.2.0',
    [string]$SignCertThumbprint,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$release = Join-Path $root 'release'
$payload = Join-Path $root 'artifacts\payload'
$msiDir = Join-Path $root 'installer\msi'
Remove-Item $payload -Recurse -Force -ErrorAction SilentlyContinue

if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'Test.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not packaging.' }
}

function Publish($proj, $out) {
    & dotnet publish (Join-Path $root $proj) -c Release -r win-x64 --self-contained true -p:Platform=x64 `
        -p:Version=$Version -p:DebugType=none -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -o $out -nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $proj" }
}

function Sign([string[]]$files) {
    if (-not $SignCertThumbprint) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found (Windows SDK)' }
    foreach ($f in $files) {
        & $signtool.FullName sign /sha1 $SignCertThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $f
        if ($LASTEXITCODE -ne 0) { throw "Signing failed: $f" }
    }
}

Publish 'src\App\Nexora.App\Nexora.App.csproj' "$payload\app"
Publish 'src\Tools\Nexora.Cli\Nexora.Cli.csproj' "$payload\cli"
foreach ($exe in "$payload\app\NexoraPrinterLAN.exe", "$payload\cli\nexora.exe") {
    if (-not (Test-Path $exe)) { throw "Missing $exe after publish" }
}
$extra = Get-ChildItem "$payload\app" -File | Where-Object Name -notin 'NexoraPrinterLAN.exe', 'nexora.ico'
if ($extra) { throw "Single-file publish left extra files the MSI does not install: $($extra.Name -join ', ')" }
Sign @("$payload\app\NexoraPrinterLAN.exe", "$payload\cli\nexora.exe")

# Nexora v4 printer driver: built and its catalog signed (local Nexora certificate unless -SignCertThumbprint is given).
$driverArgs = @{}
if ($SignCertThumbprint) { $driverArgs.CertThumbprint = $SignCertThumbprint }
& (Join-Path $root 'driver\Build-DriverPackage.ps1') @driverArgs
$driverDir = Join-Path $root 'artifacts\driver'
if (-not (Test-Path "$driverDir\NexoraV4\nexoraprinterlan.cat")) { throw 'Driver package was not built' }
# With a purchased (publicly trusted) certificate there is no .cer to ship: Windows already trusts it.
$hasDriverCert = if (-not $SignCertThumbprint -and (Test-Path "$driverDir\NexoraDriverSigning.cer")) { 'yes' } else { 'no' }

New-Item -ItemType Directory -Force -Path $release | Out-Null
$msi = Join-Path $release "NexoraPrinterLAN-$Version-x64.msi"
Push-Location $msiDir
try {
    & wix build NexoraPrinterLAN.wxs -arch x64 `
        -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext -ext WixToolset.Firewall.wixext `
        -d Version=$Version -d PayloadDir=$payload -d AssetsDir="$root\assets" -d ScriptsDir="$root\scripts" `
        -d DocsDir="$root\docs" -d RootDir=$root -d DriverDir=$driverDir -d HasDriverCert=$hasDriverCert -o $msi
    if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }
}
finally { Pop-Location }
Remove-Item ([IO.Path]::ChangeExtension($msi, '.wixpdb')) -ErrorAction SilentlyContinue
Sign @($msi)

$hash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $msi -Leaf)" | Set-Content (Join-Path $release 'SHA256SUMS.txt')
Write-Host "Created $msi ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)" -ForegroundColor Green
Write-Host "SHA-256 $hash"
