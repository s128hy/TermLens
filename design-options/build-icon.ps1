$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$root = Split-Path -Parent $PSScriptRoot
$resources = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $root 'Branding.xaml')))
$source = $resources['BrandMark']
$frames = [Collections.Generic.List[object]]::new()
foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
 $visual = [Windows.Media.DrawingVisual]::new()
 $drawing = $visual.RenderOpen()
 $drawing.DrawImage($source, [Windows.Rect]::new(0,0,$size,$size))
 $drawing.Close()
 $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
 $bitmap.Render($visual)
 $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
 $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
 $stream = [IO.MemoryStream]::new()
 $encoder.Save($stream)
 $frames.Add(@{size=$size;bytes=$stream.ToArray()})
 $stream.Dispose()
}
$file = [IO.File]::Create((Join-Path $root 'app.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
 $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
 $offset = 6 + 16 * $frames.Count
 foreach($frame in $frames) {
  $dimension = if ($frame.size -eq 256) {0} else {$frame.size}
  $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
  $writer.Write([byte]0); $writer.Write([byte]0)
  $writer.Write([uint16]1); $writer.Write([uint16]32)
  $writer.Write([uint32]$frame.bytes.Length); $writer.Write([uint32]$offset)
  $offset += $frame.bytes.Length
 }
 foreach($frame in $frames) { $writer.Write([byte[]]$frame.bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
[IO.File]::WriteAllBytes((Join-Path $root 'artifacts\icon-preview.png'), $frames[$frames.Count-1].bytes)
Write-Output 'Generated shared blue icon with transparent safe margins: 16–256 px'
