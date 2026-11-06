<#
.SYNOPSIS  Publishes self-contained x64 builds and creates the Client and Receiver release packages in release\.
.DESCRIPTION
    Output:
      release\NexoraPrinterLAN-Client-<ver>-win-x64\   (+ .zip)   install on the PC that prints (PC1)
      release\NexoraPrinterLAN-Receiver-<ver>-win-x64\ (+ .zip)   install on the PC with the printer (PC2)
    Packages are self-contained: the target PC does not need .NET installed.
    Binaries are not code-signed unless -SignCertThumbprint is given (see docs\INSTALLATION.md, "Signing").
#>
param(
    [string]$Version = '1.0.0',
    [string]$SignCertThumbprint,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$release = Join-Path $root 'release'
$stage = Join-Path $root 'artifacts\publish'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'Test.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not packaging.' }
}

function Publish($proj, $out) {
    & dotnet publish (Join-Path $root $proj) -c Release -r win-x64 --self-contained true -p:Platform=x64 `
        -p:Version=$Version -p:DebugType=none -o $out -nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $proj" }
}

function Sign($dir) {
    if (-not $SignCertThumbprint) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found (Windows SDK)' }
    Get-ChildItem $dir -Include 'Nexora*.exe', 'Nexora*.dll', 'nexora.exe' -Recurse | ForEach-Object {
        & $signtool.FullName sign /sha1 $SignCertThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $_.FullName
        if ($LASTEXITCODE -ne 0) { throw "Signing failed: $($_.Name)" }
    }
}

function Make-Package($name, $appProj, $files) {
    $dir = Join-Path $release "NexoraPrinterLAN-$name-$Version-win-x64"
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path "$dir\app" | Out-Null
    Publish $appProj "$dir\app"
    Publish 'src\Tools\Nexora.Cli\Nexora.Cli.csproj' "$stage\cli-$name"
    Copy-Item "$stage\cli-$name\nexora.exe", "$stage\cli-$name\nexora.dll", "$stage\cli-$name\nexora.runtimeconfig.json", "$stage\cli-$name\nexora.deps.json" "$dir\app" -Force
    foreach ($f in $files) { Copy-Item (Join-Path $root $f) $dir -Force }
    Copy-Item (Join-Path $root 'docs\INSTALLATION.md'), (Join-Path $root 'docs\TROUBLESHOOTING.md'), (Join-Path $root 'LICENSE') $dir -Force
    Sign "$dir\app"
    $zip = "$dir.zip"
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path "$dir\*" -DestinationPath $zip
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $zip -Leaf)" | Add-Content (Join-Path $release 'SHA256SUMS.txt')
    Write-Host "Created $zip" -ForegroundColor Green
}

New-Item -ItemType Directory -Force -Path $release | Out-Null
Remove-Item (Join-Path $release 'SHA256SUMS.txt') -ErrorAction SilentlyContinue
Make-Package 'Client' 'src\Client\Nexora.Client.App\Nexora.Client.App.csproj' @(
    'installer\Install-NexoraClient.ps1', 'installer\Uninstall-NexoraClient.ps1', 'installer\Install-Client.cmd', 'installer\Common.ps1',
    'scripts\Install-NexoraQueue.ps1', 'scripts\Uninstall-NexoraQueue.ps1', 'scripts\Diagnostics.ps1')
Make-Package 'Receiver' 'src\Receiver\Nexora.Receiver.App\Nexora.Receiver.App.csproj' @(
    'installer\Install-NexoraReceiver.ps1', 'installer\Uninstall-NexoraReceiver.ps1', 'installer\Install-Receiver.cmd', 'installer\Common.ps1',
    'scripts\Diagnostics.ps1')
Get-Content (Join-Path $release 'SHA256SUMS.txt')
