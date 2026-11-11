param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\src\BlueHarbor\Assets')
)

Add-Type -AssemblyName System.Drawing

# Small sizes are written as classic 32-bit BMP/DIB entries. Some Windows
# shell versions do not render PNG-compressed small ICO entries correctly in
# the notification area, which can make the tray icon appear blank.
$sizes = @(16, 20, 24, 32, 48, 64)
$images = @()

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $circleBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 15, 174, 150))
    $g.FillEllipse($circleBrush, 0.5, 0.5, $size - 1, $size - 1)

    $outline = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(210, 255, 255, 255), [Math]::Max(0.8, $size * 0.025))
    $g.DrawEllipse($outline, 1, 1, $size - 2, $size - 2)

    $scale = ($size - 2) / 256.0
    $off = 1.0
    function P([double]$x, [double]$y) {
        return New-Object System.Drawing.PointF(($off + $x * $scale), ($off + $y * $scale))
    }

    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [Math]::Max(1.25, $size * 0.09))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $a = @((P 128 48), (P 176 88), (P 80 168), (P 128 208), (P 128 48))
    $g.DrawLines($pen, $a)

    $accent = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 210, 255, 248), [Math]::Max(1.25, $size * 0.09))
    $accent.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $accent.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $accent.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $b = @((P 80 88), (P 176 168), (P 128 208))
    $g.DrawLines($accent, $b)

    $g.Dispose()

    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $maskSize = $maskStride * $size
    $xorSize = $size * $size * 4
    $imageSize = 40 + $xorSize + $maskSize

    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    $w.Write([UInt32]40)
    $w.Write([Int32]$size)
    $w.Write([Int32]($size * 2))
    $w.Write([UInt16]1)
    $w.Write([UInt16]32)
    $w.Write([UInt32]0)
    $w.Write([UInt32]($xorSize + $maskSize))
    $w.Write([Int32]0)
    $w.Write([Int32]0)
    $w.Write([UInt32]0)
    $w.Write([UInt32]0)

    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([Byte]$c.B)
            $w.Write([Byte]$c.G)
            $w.Write([Byte]$c.R)
            $w.Write([Byte]$c.A)
        }
    }

    for ($y = 0; $y -lt $size; $y++) {
        $w.Write((New-Object byte[] $maskStride))
    }

    $w.Flush()
    $images += [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
    $w.Dispose()
    $ms.Dispose()
    $bmp.Dispose()
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$icoPath = Join-Path $OutDir 'TrayIcon.ico'
$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $writer.Write([Byte]$image.Size)
    $writer.Write([Byte]$image.Size)
    $writer.Write([Byte]0)
    $writer.Write([Byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$image.Bytes.Length)
    $writer.Write([UInt32]$offset)
    $offset += $image.Bytes.Length
}
foreach ($image in $images) {
    $writer.Write($image.Bytes)
}
$writer.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

Write-Host "Wrote $icoPath with $($images.Count) BMP sizes"
