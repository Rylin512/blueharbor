param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\src\BlueHarbor\Assets')
)

Add-Type -AssemblyName System.Drawing

$size = 256
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.Clear([System.Drawing.Color]::Transparent)

# Rounded square shell
$rect = New-Object System.Drawing.RectangleF(8, 8, 240, 240)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 54
$path.AddArc($rect.X, $rect.Y, $r, $r, 180, 90)
$path.AddArc($rect.Right - $r, $rect.Y, $r, $r, 270, 90)
$path.AddArc($rect.Right - $r, $rect.Bottom - $r, $r, $r, 0, 90)
$path.AddArc($rect.X, $rect.Bottom - $r, $r, $r, 90, 90)
$path.CloseFigure()

$bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 0)),
    (New-Object System.Drawing.Point(256, 256)),
    [System.Drawing.Color]::FromArgb(255, 12, 24, 38),
    [System.Drawing.Color]::FromArgb(255, 6, 12, 22))
$g.FillPath($bgBrush, $path)

$borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(90, 77, 227, 193), 3)
$g.DrawPath($borderPen, $path)

# Glow orb
$orb = New-Object System.Drawing.RectangleF(48, 48, 160, 160)
$orbPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$orbPath.AddEllipse($orb)
$orbBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush($orbPath)
$orbBrush.CenterColor = [System.Drawing.Color]::FromArgb(55, 77, 227, 193)
$orbBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 77, 227, 193))
$g.FillEllipse($orbBrush, $orb)

$ringPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(120, 77, 227, 193), 2)
$g.DrawEllipse($ringPen, 70, 70, 116, 116)

# Bluetooth rune
$runePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 236, 255, 250), 13)
$runePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$runePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$runePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

$points1 = @(
    (New-Object System.Drawing.PointF(128, 48)),
    (New-Object System.Drawing.PointF(176, 88)),
    (New-Object System.Drawing.PointF(80, 168)),
    (New-Object System.Drawing.PointF(128, 208)),
    (New-Object System.Drawing.PointF(128, 48))
)
$g.DrawLines($runePen, $points1)

$points2 = @(
    (New-Object System.Drawing.PointF(80, 88)),
    (New-Object System.Drawing.PointF(176, 168)),
    (New-Object System.Drawing.PointF(128, 208))
)
$runePen2 = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 77, 227, 193), 13)
$runePen2.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$runePen2.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$runePen2.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
$g.DrawLines($runePen2, $points2)

# Accent dot
$dotBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 196, 92))
$g.FillEllipse($dotBrush, 178, 178, 24, 24)

$g.Dispose()

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$pngPath = Join-Path $OutDir 'BlueHarbor.png'
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

# Single-image PNG-compressed ICO (256x256), supported by Windows Vista+.
$pngBytes = [System.IO.File]::ReadAllBytes($pngPath)
$icoPath = Join-Path $OutDir 'BlueHarbor.ico'
$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([UInt16]0)      # reserved
$writer.Write([UInt16]1)      # type: icon
$writer.Write([UInt16]1)      # image count
$writer.Write([Byte]0)        # width 256
$writer.Write([Byte]0)        # height 256
$writer.Write([Byte]0)        # palette
$writer.Write([Byte]0)        # reserved
$writer.Write([UInt16]1)      # color planes
$writer.Write([UInt16]32)     # bits per pixel
$writer.Write([UInt32]$pngBytes.Length)
$writer.Write([UInt32]22)     # offset
$writer.Write($pngBytes)
$writer.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

$bmp.Dispose()
Write-Host "Wrote $pngPath and $icoPath"
