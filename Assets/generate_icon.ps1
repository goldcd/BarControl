Add-Type -AssemblyName System.Drawing

function Create-Soundbar-Bitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $scale = [float]$size / 128.0

    # 1. Upward Acoustic Soundwave Arcs (centered above the soundbar)
    $arcCenterX = [float](64.0 * $scale)
    $arcCenterY = [float](58.0 * $scale)

    $wavePenWidth = [float][Math]::Max(1.8, 7.5 * $scale)
    $wavePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 52, 211, 153), $wavePenWidth)
    $wavePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $wavePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    # Inner wave arc
    $r1 = [float](22.0 * $scale)
    $g.DrawArc($wavePen, $arcCenterX - $r1, $arcCenterY - $r1, $r1 * 2, $r1 * 2, 215, 110)

    # Outer wave arc
    $r2 = [float](38.0 * $scale)
    $g.DrawArc($wavePen, $arcCenterX - $r2, $arcCenterY - $r2, $r2 * 2, $r2 * 2, 220, 100)

    # 2. Sleek Horizontal Soundbar Enclosure (Pill / Rounded Rect)
    $bx = [float](8.0 * $scale)
    $by = [float](60.0 * $scale)
    $bw = [float](112.0 * $scale)
    $bh = [float](44.0 * $scale)
    $radius = [float][Math]::Max(2.0, 14.0 * $scale)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($bx, $by, $radius * 2, $radius * 2, 180, 90)
    $path.AddArc($bx + $bw - $radius * 2, $by, $radius * 2, $radius * 2, 270, 90)
    $path.AddArc($bx + $bw - $radius * 2, $by + $bh - $radius * 2, $radius * 2, $radius * 2, 0, 90)
    $path.AddArc($bx, $by + $bh - $radius * 2, $radius * 2, $radius * 2, 90, 90)
    $path.CloseFigure()

    $bodyBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(245, 24, 26, 32))
    $borderWidth = [float][Math]::Max(1.8, 7.0 * $scale)
    $borderPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 241, 245, 249), $borderWidth)

    $g.FillPath($bodyBrush, $path)
    $g.DrawPath($borderPen, $path)

    # 3. Left & Right Speaker Drivers inside Soundbar
    $driverRadius = [float][Math]::Max(1.5, 8.5 * $scale)
    $driverY = [float]($by + ($bh / 2.0) - $driverRadius)
    $leftDriverX = [float](28.0 * $scale - $driverRadius)
    $rightDriverX = [float](100.0 * $scale - $driverRadius)

    $driverBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 241, 245, 249))
    $g.FillEllipse($driverBrush, $leftDriverX, $driverY, $driverRadius * 2, $driverRadius * 2)
    $g.FillEllipse($driverBrush, $rightDriverX, $driverY, $driverRadius * 2, $driverRadius * 2)

    # 4. Center Status Bar / Grille Accent (only drawn at >= 24px so 16px stays ultra-crisp)
    if ($size -ge 24) {
        $grillePenWidth = [float][Math]::Max(1.5, 5.5 * $scale)
        $grillePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 52, 211, 153), $grillePenWidth)
        $grillePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $grillePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $midY = [float]($by + ($bh / 2.0))
        $g.DrawLine($grillePen, [float](52.0 * $scale), $midY, [float](76.0 * $scale), $midY)
        $grillePen.Dispose()
    }

    $wavePen.Dispose()
    $bodyBrush.Dispose()
    $borderPen.Dispose()
    $driverBrush.Dispose()
    $path.Dispose()
    $g.Dispose()

    return $bmp
}

# Generate 128x128 preview PNG
$previewBmp = Create-Soundbar-Bitmap 128
$previewBmp.Save("Assets\icon_preview_opt4.png", [System.Drawing.Imaging.ImageFormat]::Png)
$previewBmp.Dispose()

# Generate .ico file using multi-resolution approach
$sizes = @(16, 24, 32, 48, 64, 128)
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms

# ICONDIR header: idReserved (0), idType (1 = icon), idCount (count of images)
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)

$pngStreams = @()
foreach ($sz in $sizes) {
    $bmp = Create-Soundbar-Bitmap $sz
    $pngStream = New-Object System.IO.MemoryStream
    $bmp.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngStreams += $pngStream
}

# Calculate offset: header (6 bytes) + entries (16 bytes each)
$offset = 6 + (16 * $sizes.Count)

for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $data = $pngStreams[$i].ToArray()

    # ICONDIRENTRY:
    $bw.Write([byte]$(if ($sz -ge 256) { 0 } else { $sz }))
    $bw.Write([byte]$(if ($sz -ge 256) { 0 } else { $sz }))
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$offset)

    $offset += $data.Length
}

for ($i = 0; $i -lt $sizes.Count; $i++) {
    $data = $pngStreams[$i].ToArray()
    $bw.Write($data, 0, $data.Length)
    $pngStreams[$i].Dispose()
}

$bw.Flush()
[System.IO.File]::WriteAllBytes("Assets\app.ico", $ms.ToArray())
$bw.Dispose()
$ms.Dispose()

Write-Output "Successfully generated Soundbar Assets\app.ico and Assets\icon_preview_opt4.png"
