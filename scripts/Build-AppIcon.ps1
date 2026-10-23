param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskAssets = Join-Path $taskRoot 'src/BuilderCrap/Builder.Desktop/Assets'
[xml]$taskSvg = Get-Content -LiteralPath (Join-Path $taskAssets 'Icons/app-mark.svg') -Raw
$taskFrames = @()
foreach ($taskSize in @(16,20,24,32,40,48,64,128,256)) {
    $taskVisual = [System.Windows.Media.DrawingVisual]::new()
    $taskContext = $taskVisual.RenderOpen()
    $taskContext.PushTransform([System.Windows.Media.ScaleTransform]::new($taskSize / 24.0, $taskSize / 24.0))
    foreach ($taskPath in $taskSvg.svg.path) {
        $taskFill = if ($taskPath.HasAttribute('fill')) { $taskPath.GetAttribute('fill') } else { $taskSvg.svg.GetAttribute('fill') }
        $taskStroke = if ($taskPath.HasAttribute('stroke')) { $taskPath.GetAttribute('stroke') } else { $taskSvg.svg.GetAttribute('stroke') }
        $taskWidth = if ($taskPath.HasAttribute('stroke-width')) { $taskPath.GetAttribute('stroke-width') } else { $taskSvg.svg.GetAttribute('stroke-width') }
        $taskBrush = $null
        if ($taskFill -ne 'none') { $taskBrush = [System.Windows.Media.BrushConverter]::new().ConvertFromInvariantString($taskFill) }
        $taskPen = $null
        if ($taskStroke -ne 'none') {
            $taskPen = [System.Windows.Media.Pen]::new([System.Windows.Media.BrushConverter]::new().ConvertFromInvariantString($taskStroke), [double]::Parse($taskWidth, [Globalization.CultureInfo]::InvariantCulture))
            $taskPen.StartLineCap = $taskPen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
            $taskPen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
        }
        $taskContext.DrawGeometry($taskBrush, $taskPen, [System.Windows.Media.Geometry]::Parse($taskPath.GetAttribute('d')))
    }
    $taskContext.Pop()
    $taskContext.Close()
    $taskBitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($taskSize, $taskSize, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $taskBitmap.Render($taskVisual)
    $taskEncoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $taskEncoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($taskBitmap))
    $taskBuffer = [IO.MemoryStream]::new()
    $taskEncoder.Save($taskBuffer)
    $taskFrames += @{ Size = $taskSize; Bytes = $taskBuffer.ToArray() }
    $taskBuffer.Dispose()
}
$taskOutput = Join-Path $taskAssets 'BuilderCrap.ico'
$taskWriter = [IO.BinaryWriter]::new([IO.File]::Create($taskOutput))
try {
    $taskWriter.Write([uint16]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]$taskFrames.Count)
    $taskOffset = 6 + 16 * $taskFrames.Count
    foreach ($taskFrame in $taskFrames) {
        $taskDimension = if ($taskFrame.Size -eq 256) { 0 } else { $taskFrame.Size }
        $taskWriter.Write([byte]$taskDimension); $taskWriter.Write([byte]$taskDimension)
        $taskWriter.Write([byte]0); $taskWriter.Write([byte]0)
        $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]32)
        $taskWriter.Write([uint32]$taskFrame.Bytes.Length); $taskWriter.Write([uint32]$taskOffset)
        $taskOffset += $taskFrame.Bytes.Length
    }
    foreach ($taskFrame in $taskFrames) { $taskWriter.Write([byte[]]$taskFrame.Bytes) }
} finally { $taskWriter.Dispose() }
Write-Output "Generated $taskOutput from app-mark.svg (9 sizes, 16–256 px)."
