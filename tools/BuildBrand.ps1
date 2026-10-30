$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$brand = Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets\Brand'
$reader = [System.Xml.XmlReader]::Create((Join-Path $brand 'Mark.xaml'))
try { $drawing = [System.Windows.Markup.XamlReader]::Load($reader) } finally { $reader.Dispose() }
$sizes = @(16,24,32,48,64,128,256)
$frames = @()
foreach ($size in $sizes + @(512)) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.DrawImage($drawing, [System.Windows.Rect]::new(0,0,$size,$size))
    $context.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    if ($size -eq 512) { [System.IO.File]::WriteAllBytes((Join-Path $brand 'logo.png'),$stream.ToArray()) }
    else { $frames += ,@($size,$stream.ToArray()) }
    $stream.Dispose()
}
$output = [System.IO.File]::Create((Join-Path $brand 'BrokenPhone.ico'))
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16*$frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame[0] -eq 256) { 0 } else { $frame[0] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frame[1].Length); $writer.Write([uint32]$offset)
        $offset += $frame[1].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame[1]) }
} finally { $writer.Dispose() }
