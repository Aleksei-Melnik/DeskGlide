$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# A vector master for Explorer, plus a simplified S mark for tiny tray sizes.
# Supersampling preserves the rounded silhouette; no external graphics tools required.
function New-RoundedPath([single]$X, [single]$Y, [single]$Width, [single]$Height, [single]$Radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X+$Width-$diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X+$Width-$diameter, $Y+$Height-$diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y+$Height-$diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return ,$path
}
function New-IconBitmap([int]$Size) {
    $scale = 4
    $large = [System.Drawing.Bitmap]::new($Size*$scale, $Size*$scale)
    $g = [System.Drawing.Graphics]::FromImage($large)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.ScaleTransform($Size*$scale/256.0, $Size*$scale/256.0)
    $dark = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#102532'))
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#f5fcff'))
    $mint = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#45e5c0'),12)
    $mint.StartCap = $mint.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $tile = New-RoundedPath 8 8 240 240 48
    $g.FillPath($dark,$tile)
    if ($Size -le 32) {
        # Wider strokes and a geometric S stay readable at 16 pixels.
        $screen = New-RoundedPath 32 40 192 136 12
        $mint.Width = 16
        $g.DrawPath($mint,$screen)
        $g.DrawLine($mint,128,184,128,208)
        $g.DrawLine($mint,88,216,168,216)
        $mark = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $points = [System.Drawing.PointF[]]@(
            [System.Drawing.PointF]::new(166,72),[System.Drawing.PointF]::new(94,72),
            [System.Drawing.PointF]::new(94,118),[System.Drawing.PointF]::new(142,118),
            [System.Drawing.PointF]::new(142,134),[System.Drawing.PointF]::new(94,134),
            [System.Drawing.PointF]::new(94,154),[System.Drawing.PointF]::new(166,154),
            [System.Drawing.PointF]::new(166,100),[System.Drawing.PointF]::new(118,100),
            [System.Drawing.PointF]::new(118,92),[System.Drawing.PointF]::new(166,92))
        $mark.AddPolygon($points)
        $g.FillPath($white,$mark)
        $mark.Dispose()
    } else {
        $screen = New-RoundedPath 40 49 176 125 14
        $g.DrawPath($mint,$screen)
        $g.DrawLine($mint,128,180,128,206)
        $g.DrawLine($mint,94,212,162,212)
        $font = [System.Drawing.Font]::new('Segoe UI',51,[System.Drawing.FontStyle]::Bold,[System.Drawing.GraphicsUnit]::Pixel)
        $format = [System.Drawing.StringFormat]::new()
        $format.Alignment = $format.LineAlignment = [System.Drawing.StringAlignment]::Center
        $g.DrawString('SDR',$font,$white,[System.Drawing.RectangleF]::new(40,49,176,125),$format)
        $format.Dispose(); $font.Dispose()
    }
    $screen.Dispose(); $tile.Dispose(); $mint.Dispose(); $white.Dispose(); $dark.Dispose(); $g.Dispose()
    $result = [System.Drawing.Bitmap]::new($Size,$Size)
    $target = [System.Drawing.Graphics]::FromImage($result)
    $target.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $target.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $target.DrawImage($large,[System.Drawing.Rectangle]::new(0,0,$Size,$Size))
    $target.Dispose(); $large.Dispose()
    return ,$result
}

$sizes = @(16,20,24,32,40,48,64,128,256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = New-IconBitmap $size
    $memory = [System.IO.MemoryStream]::new()
    $bitmap.Save($memory,[System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,$memory.ToArray()
    if ($size -eq 256) { $bitmap.Save((Join-Path $PSScriptRoot 'SdrCapture.png'),[System.Drawing.Imaging.ImageFormat]::Png) }
    $memory.Dispose(); $bitmap.Dispose()
}
$file = [System.IO.File]::Create((Join-Path $PSScriptRoot 'SdrCapture.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16*$sizes.Count
    for ($i=0; $i -lt $sizes.Count; $i++) {
        $dimension = $sizes[$i] % 256
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $file.Dispose() }

# Actual-size previews against the two common taskbar backgrounds.
$preview = [System.Drawing.Bitmap]::new(620,230)
$canvas = [System.Drawing.Graphics]::FromImage($preview)
$canvas.Clear([System.Drawing.ColorTranslator]::FromHtml('#f0f1f3'))
$background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#202225'))
$canvas.FillRectangle($background,0,115,620,115)
$label = [System.Drawing.Font]::new('Segoe UI',10)
$x = 22
foreach ($size in @(16,20,24,32,40,48,64)) {
    $bitmap = New-IconBitmap $size
    $canvas.DrawImageUnscaled($bitmap,$x,16)
    $canvas.DrawImageUnscaled($bitmap,$x,131)
    $canvas.DrawString("${size}px",$label,[System.Drawing.Brushes]::DimGray,$x,85)
    $canvas.DrawString("${size}px",$label,[System.Drawing.Brushes]::LightGray,$x,200)
    $bitmap.Dispose(); $x += 82
}
$preview.Save((Join-Path $PSScriptRoot 'Icon-preview.png'),[System.Drawing.Imaging.ImageFormat]::Png)
$label.Dispose(); $background.Dispose(); $canvas.Dispose(); $preview.Dispose()
Write-Output ('Icon built: ' + ($sizes -join ', ') + ' px')
