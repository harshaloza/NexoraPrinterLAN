<#
.SYNOPSIS
    Builds and signs the Nexora Printer LAN v4 driver package into artifacts\driver\NexoraV4.
.DESCRIPTION
    1. Copies the INF, GPD, manifest and (empty) pipeline config.
    2. Creates the security catalog (.cat) with Inf2Cat from the Windows Driver Kit.
    3. Signs the catalog with a code-signing certificate:
         -CertThumbprint <thumbprint>  a certificate you bought (publicly trusted; installs on any PC), or
         default                       the local "Nexora Printer LAN Driver Signing" certificate in CurrentUser\My,
                                       created on first run (self-signed, non-exportable private key, 10 years).
       With the local certificate, a PC accepts the driver only after its public part
       (artifacts\driver\NexoraDriverSigning.cer) is trusted there; Setup does that for your own PCs.
    No test-signing mode and no driver-signature-enforcement change is involved.
#>
param([string]$CertThumbprint)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'artifacts\driver\NexoraV4'
$kit = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$inf2cat = Get-ChildItem $kit -Recurse -Filter Inf2Cat.exe | Sort-Object FullName -Descending | Select-Object -First 1
$signtool = Get-ChildItem $kit -Recurse -Filter signtool.exe | Where-Object FullName -match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $inf2cat -or -not $signtool) { throw 'Inf2Cat.exe / signtool.exe not found: install the Windows Driver Kit (WDK) and Windows SDK.' }

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'NexoraV4\*') $out

& $inf2cat.FullName /driver:$out /os:10_X64 /verbose | Select-String -Pattern 'error|warning|Signability test complete|Catalog generation complete' | ForEach-Object { $_.Line.Trim() }
if ($LASTEXITCODE -ne 0 -or -not (Test-Path "$out\nexoraprinterlan.cat")) { throw "Inf2Cat failed ($LASTEXITCODE)" }

if (-not $CertThumbprint) {
    $subject = 'CN=Nexora Printer LAN Driver Signing (local)'
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation Cert:\CurrentUser\My `
            -KeyExportPolicy NonExportable -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(10)
        Write-Host "Created signing certificate $($cert.Thumbprint) (private key stays in this user's store)"
    }
    $CertThumbprint = $cert.Thumbprint
    Export-Certificate -Cert $cert -FilePath (Join-Path (Split-Path $out) 'NexoraDriverSigning.cer') | Out-Null
}

& $signtool.FullName sign /sha1 $CertThumbprint /fd SHA256 "$out\nexoraprinterlan.cat"
if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
Write-Host "Signed driver package: $out" -ForegroundColor Green
Get-ChildItem $out | ForEach-Object { "  $($_.Name)" }
