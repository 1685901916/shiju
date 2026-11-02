# Renders the app icon (the character U+62FE) into a multi-size .ico. Run from Windows PowerShell 5.1.
param([string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/SelectionAssistant.Windows/Assets/app.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)
    $inset = [Math]::Max(0, [Math]::Round($size / 32))
    $box = $size - 2 * $inset
    $radius = [Math]::Max(3, $box * 0.24)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($inset, $inset, $d, $d, 180, 90)
    $path.AddArc($inset + $box - $d, $inset, $d, $d, 270, 90)
    $path.AddArc($inset + $box - $d, $inset + $box - $d, $d, $d, 0, 90)
    $path.AddArc($inset, $inset + $box - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x1E, 0x75, 0x56))), $path)
    $font = New-Object System.Drawing.Font 'Microsoft YaHei UI', ([single]($box * 0.62)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = 'Center'; $format.LineAlignment = 'Center'
    $rect = New-Object System.Drawing.RectangleF 0, ([single]($size * 0.03)), $size, $size
    $g.DrawString([string][char]0x62FE, $font, [System.Drawing.Brushes]::White, $rect, $format)
    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    if ($size -ge 256) { $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png) }
    else {
        # Classic 32-bit DIB frame: header, bottom-up BGRA pixels, empty AND mask. Every icon loader reads these.
        $writer = New-Object System.IO.BinaryWriter $stream
        $writer.Write([uint32]40); $writer.Write([int32]$size); $writer.Write([int32]($size * 2))
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]0)
        $writer.Write([uint32]($size * $size * 4)); $writer.Write([int32]0); $writer.Write([int32]0); $writer.Write([uint32]0); $writer.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bitmap.GetPixel($x, $y)
                $writer.Write([byte]$c.B); $writer.Write([byte]$c.G); $writer.Write([byte]$c.R); $writer.Write([byte]$c.A)
            }
        }
        $writer.Write((New-Object byte[] ([Math]::Ceiling($size / 32) * 4 * $size)))
        $writer.Flush()
    }
    $bitmap.Dispose()
    , $stream.ToArray()
}
New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
$file = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($Out))
$file.Write([uint16]0); $file.Write([uint16]1); $file.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $edge = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $file.Write([byte]$edge); $file.Write([byte]$edge); $file.Write([byte]0); $file.Write([byte]0)
    $file.Write([uint16]1); $file.Write([uint16]32)
    $file.Write([uint32]$frames[$i].Length); $file.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $file.Write($frame) }
$file.Close()
Write-Host "Icon written: $Out"
