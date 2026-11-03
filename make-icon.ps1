Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path $PSScriptRoot 'assets\SnapTransOCR.ico'
$frames = New-Object 'System.Collections.Generic.List[byte[]]'
$dimensions = @(16,20,24,32,40,48,64,128,256)
foreach ($dimension in $dimensions) {
    $bitmap = New-Object System.Drawing.Bitmap $dimension,$dimension
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform(($dimension/64.0),($dimension/64.0))
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc(2,2,24,24,180,90); $tile.AddArc(38,2,24,24,270,90)
    $tile.AddArc(38,38,24,24,0,90); $tile.AddArc(2,38,24,24,90,90); $tile.CloseFigure()
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(4,4)),([System.Drawing.Point]::new(60,62)),([System.Drawing.Color]::FromArgb(36,104,220)),([System.Drawing.Color]::FromArgb(17,34,80))
    $graphics.FillPath($fill,$tile)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White),3.5
    $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLines($pen,[System.Drawing.Point[]]@([System.Drawing.Point]::new(13,25),[System.Drawing.Point]::new(13,13),[System.Drawing.Point]::new(25,13)))
    $graphics.DrawLines($pen,[System.Drawing.Point[]]@([System.Drawing.Point]::new(39,13),[System.Drawing.Point]::new(51,13),[System.Drawing.Point]::new(51,25)))
    $graphics.DrawLines($pen,[System.Drawing.Point[]]@([System.Drawing.Point]::new(13,39),[System.Drawing.Point]::new(13,51),[System.Drawing.Point]::new(25,51)))
    $graphics.DrawLines($pen,[System.Drawing.Point[]]@([System.Drawing.Point]::new(39,51),[System.Drawing.Point]::new(51,51),[System.Drawing.Point]::new(51,39)))
    $letterPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White),3.8
    $letterPen.StartCap = $letterPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $letterPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawLines($letterPen,[System.Drawing.Point[]]@([System.Drawing.Point]::new(23,39),[System.Drawing.Point]::new(32,21),[System.Drawing.Point]::new(41,39)))
    $graphics.DrawLine($letterPen,27,32,37,32)
    $scanPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(83,231,216)),3
    $scanPen.StartCap = $scanPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($scanPen,24,45,40,45)
    $stream = New-Object System.IO.MemoryStream
    try {
        $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
        $frames.Add($stream.ToArray())
        if ($dimension -eq 256) { [IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'assets\SnapTransOCR.png'),$stream.ToArray()) }
    } finally { $stream.Dispose(); $scanPen.Dispose(); $letterPen.Dispose(); $pen.Dispose(); $fill.Dispose(); $tile.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
$iconStream = [IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter $iconStream
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16*$frames.Count
    for ($i=0; $i -lt $frames.Count; $i++) {
        $dimensionByte = if ($dimensions[$i] -eq 256) { 0 } else { $dimensions[$i] }
        $writer.Write([byte]$dimensionByte); $writer.Write([byte]$dimensionByte); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose(); $iconStream.Dispose() }
