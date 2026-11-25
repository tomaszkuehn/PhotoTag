# Generuje Assets\PhotoTag.ico (16–256 px) i podgląd Assets\PhotoTag-256.png.
# Motyw: zdjęcie krajobrazu (niebo, słońce, góry) z czerwoną pinezką lokalizacji.
# Uruchom: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$outIco = Join-Path $root 'Assets\PhotoTag.ico'
$outPng = Join-Path $root 'Assets\PhotoTag-256.png'

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Color([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)
    $small = $size -le 24

    # karta zdjęcia: niebo
    $m = if ($small) { 4 } else { 10 }
    $frame = New-RoundRect $m ($m + 10) (256 - 2 * $m - 34) (256 - 2 * $m - 40) 30
    $sky = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, 220), (Color '#6EC1FF'), (Color '#1F6FC5')
    $g.FillPath($sky, $frame)

    $g.SetClip($frame)
    if (-not $small) {
        $g.FillEllipse((New-Object System.Drawing.SolidBrush (Color '#FFD54F')), 44, 46, 46, 46)
    }
    $back = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF 0, 230), (New-Object System.Drawing.PointF 0, 170),
        (New-Object System.Drawing.PointF 70, 105), (New-Object System.Drawing.PointF 130, 165),
        (New-Object System.Drawing.PointF 250, 230))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush (Color '#3FA36B')), $back)
    $front = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF 0, 240), (New-Object System.Drawing.PointF 0, 205),
        (New-Object System.Drawing.PointF 60, 175), (New-Object System.Drawing.PointF 150, 140),
        (New-Object System.Drawing.PointF 250, 200), (New-Object System.Drawing.PointF 250, 240))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush (Color '#257A4C')), $front)
    $g.ResetClip()
    $g.DrawPath((New-Object System.Drawing.Pen (Color '#FFFFFF'), ([single]($(if ($small) { 14 } else { 8 })))), $frame)

    # pinezka lokalizacji (prawy dolny róg, nachodzi na zdjęcie)
    $cx = 178; $cy = 128; $r = 56; $tip = 246
    $pin = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pin.AddArc(($cx - $r), ($cy - $r), (2 * $r), (2 * $r), 150, 240)
    $pin.AddLine([single]($cx + $r * [Math]::Cos([Math]::PI / 6)), [single]($cy + $r * 0.5), [single]$cx, [single]$tip)
    $pin.CloseFigure()
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 16), $pin)
    $red = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 70), (New-Object System.Drawing.PointF 0, 246), (Color '#FF5A4E'), (Color '#C62828')
    $g.FillPath($red, $pin)
    $hole = if ($small) { 26 } else { 22 }
    $g.FillEllipse([System.Drawing.Brushes]::White, ($cx - $hole), ($cy - $hole), (2 * $hole), (2 * $hole))

    $g.Dispose()
    return $bmp
}

# Wpis ICO w klasycznym formacie DIB (32 bpp + maska AND) — czytają go wszystkie komponenty, także GDI+.
function To-Dib([System.Drawing.Bitmap]$b) {
    $n = $b.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $maskRow = [int]([Math]::Ceiling($n / 32.0) * 4)
    $w.Write([UInt32]40); $w.Write([Int32]$n); $w.Write([Int32](2 * $n))
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $w.Write([UInt32]($n * $n * 4 + $maskRow * $n)); $w.Write([Int32]0); $w.Write([Int32]0)
    $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $n - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $n; $x++) {
            $c = $b.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $w.Write((New-Object byte[] ($maskRow * $n)))
    $w.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $b = Render $sz
    if ($sz -eq 256) {
        $b.Save($outPng, [System.Drawing.Imaging.ImageFormat]::Png)
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $data = $ms.ToArray()
    } else {
        $data = To-Dib $b
    }
    $b.Dispose()
    , $data
}

# plik ICO: małe rozmiary jako DIB, 256 px jako PNG
$fs = [System.IO.File]::Create($outIco)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"Zapisano $outIco"
