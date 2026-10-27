Add-Type -AssemblyName System.Drawing

function Create-Option4-Bitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $scale = [float]$size / 128.0

    # 1. Minimalist Monitor Glyph
    $penWidth = [Math]::Max(2.0, 7.0 * $scale)
    $monitorPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 241, 245, 249), [float]$penWidth)
    $monitorPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $monitorPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $screenBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(235, 24, 26, 32))

    # Screen dimensions
    $sx = [float](16.0 * $scale)
    $sy = [float](20.0 * $scale)
    $sw = [float](96.0 * $scale)
    $sh = [float](64.0 * $scale)
    $radius = [float][Math]::Max(1.0, 6.0 * $scale)

    # Rounded rectangle path for screen
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($sx, $sy, $radius * 2, $radius * 2, 180, 90)
    $path.AddArc($sx + $sw - $radius * 2, $sy, $radius * 2, $radius * 2, 270, 90)
    $path.AddArc($sx + $sw - $radius * 2, $sy + $sh - $radius * 2, $radius * 2, $radius * 2, 0, 90)
    $path.AddArc($sx, $sy + $sh - $radius * 2, $radius * 2, $radius * 2, 90, 90)
    $path.CloseFigure()

    $g.FillPath($screenBrush, $path)
    $g.DrawPath($monitorPen, $path)

    # Stand Neck & Base
    $neckTop = [float]($sy + $sh)
    $neckBottom = [float](104.0 * $scale)
    $midX = [float]($size / 2.0)
    $g.DrawLine($monitorPen, $midX, $neckTop, $midX, $neckBottom)

    $baseLeft = [float](38.0 * $scale)
    $baseRight = [float](90.0 * $scale)
    $g.DrawLine($monitorPen, $baseLeft, $neckBottom, $baseRight, $neckBottom)

    # 2. Option 4: Bold Diagonal Ban Stripe across monitor (bottom-left to top-right)
    $x1 = [float](12.0 * $scale)
    $y1 = [float](98.0 * $scale)
    $x2 = [float](116.0 * $scale)
    $y2 = [float](16.0 * $scale)

    # Dark contrast halo so the ban pops over white frame and dark/light backgrounds
    $haloWidth = [float][Math]::Max(4.0, 22.0 * $scale)
    $haloPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(230, 15, 17, 23), [float]$haloWidth)
    $haloPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $haloPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($haloPen, $x1, $y1, $x2, $y2)

    # Vivid Crimson Red Stripe
    $redWidth = [float][Math]::Max(2.5, 14.0 * $scale)
    $redPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 239, 68, 68), [float]$redWidth)
    $redPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $redPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($redPen, $x1, $y1, $x2, $y2)

    $monitorPen.Dispose()
    $screenBrush.Dispose()
    $haloPen.Dispose()
    $redPen.Dispose()
    $path.Dispose()
    $g.Dispose()

    return $bmp
}

# Generate 128x128 preview PNG
$previewBmp = Create-Option4-Bitmap 128
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
    $bmp = Create-Option4-Bitmap $sz
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

Write-Output "Successfully generated Option 4 Assets\app.ico and Assets\icon_preview_opt4.png"
