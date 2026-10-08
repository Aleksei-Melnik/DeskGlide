$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# SC monogram built from vector paths, consistent at every tray and Explorer size.
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
    $tile = New-RoundedPath 8 8 240 240 52
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.Point]::new(24,12),[System.Drawing.Point]::new(224,248),[System.Drawing.ColorTranslator]::FromHtml('#41203f'),[System.Drawing.ColorTranslator]::FromHtml('#120e1d'))
    $g.FillPath($gradient,$tile)
    $border = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#8b456e'),2)
    $g.DrawPath($border,$tile)
    $white = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#f4f8ff'),22)
    $mint = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#ff538e'),22)
    foreach ($pen in @($white,$mint)) {
        $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    }
    $s = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $s.AddLine(108,80,78,80)
    $s.AddBezier(78,80,62,80,56,87,56,102)
    $s.AddBezier(56,102,56,117,64,124,80,124)
    $s.AddLine(80,124,88,124)
    $s.AddBezier(88,124,104,124,112,133,112,149)
    $s.AddBezier(112,149,112,167,102,176,86,176)
    $s.AddLine(86,176,54,176)
    $c = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $c.AddLine(202,80,178,80)
    $c.AddBezier(178,80,155,80,146,99,146,128)
    $c.AddBezier(146,128,146,157,155,176,178,176)
    $c.AddLine(178,176,202,176)
    $g.DrawPath($white,$s); $g.DrawPath($mint,$c)
    $s.Dispose(); $c.Dispose(); $tile.Dispose(); $gradient.Dispose(); $border.Dispose(); $mint.Dispose(); $white.Dispose(); $g.Dispose()
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
