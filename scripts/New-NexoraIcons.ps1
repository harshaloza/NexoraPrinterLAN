<#
.SYNOPSIS
    Regenerates assets\nexora-client.ico and assets\nexora-receiver.ico (PNG-compressed multi-size icons).
    Run on any Windows PC; output is committed so normal builds do not need this script.
#>
param([string]$OutDir = (Join-Path $PSScriptRoot '..\assets'))
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function New-Png([int]$size, [string]$hex, [string]$glyph) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $r = [Math]::Max(2, [int]($size * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2; $w = $size - 1
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($hex))), $path)
    # Printer silhouette: paper, body, tray.
    $white = [System.Drawing.Brushes]::White
    $s = $size / 32.0
    $g.FillRectangle($white, [float](10*$s), [float](6*$s), [float](12*$s), [float](6*$s))
    $g.FillRectangle($white, [float](6*$s), [float](12*$s), [float](20*$s), [float](10*$s))
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($hex))), [float](9*$s), [float](17*$s), [float](14*$s), [float](2*$s))
    $g.FillRectangle($white, [float](10*$s), [float](20*$s), [float](12*$s), [float](6*$s))
    $font = New-Object System.Drawing.Font 'Segoe UI', ([float](7*$s)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $rect = New-Object System.Drawing.RectangleF ([float](10*$s)), ([float](20*$s)), ([float](12*$s)), ([float](6*$s))
    $glyphBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($hex))
    $g.DrawString($glyph, $font, $glyphBrush, $rect, $sf)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return ,$ms.ToArray()
}

function Write-Ico([string]$file, [string]$hex, [string]$glyph) {
    $sizes = 16, 24, 32, 48, 64, 256
    $pngs = $sizes | ForEach-Object { ,(New-Png $_ $hex $glyph) }
    $fs = [System.IO.File]::Create($file); $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $sz = $sizes[$i]; $len = $pngs[$i].Length
        $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
        $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$len); $bw.Write([uint32]$offset); $offset += $len
    }
    foreach ($p in $pngs) { $bw.Write([byte[]]$p) }
    $bw.Close()
    Write-Host "Wrote $file"
}

Write-Ico (Join-Path $OutDir 'nexora.ico') '#2F6FED' 'N'
