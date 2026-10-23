$ErrorActionPreference = 'Stop'
# Reuse the Mac icon's PNG verbatim; do not redraw the brand artwork.
$repo = Split-Path $PSScriptRoot
$bytes = [IO.File]::ReadAllBytes((Join-Path $repo 'assets/AppIcon.icns'))
$offset = 8
$png = $null
while ($offset -lt $bytes.Length) {
    $type = [Text.Encoding]::ASCII.GetString($bytes, $offset, 4)
    $length = ([int]$bytes[$offset+4]*16777216)+([int]$bytes[$offset+5]*65536)+([int]$bytes[$offset+6]*256)+$bytes[$offset+7]
    if ($length -lt 8 -or $offset + $length -gt $bytes.Length) { throw 'Invalid ICNS entry' }
    if ($type -eq 'ic08') { $png = [byte[]]$bytes[($offset+8)..($offset+$length-1)]; break }
    $offset += $length
}
if (!$png -or [Convert]::ToHexString($png[0..7]) -ne '89504E470D0A1A0A') { throw '256px PNG is missing in Mac icon' }
$folder = Join-Path $repo 'windows/Assets'
New-Item -ItemType Directory -Force $folder | Out-Null
[IO.File]::WriteAllBytes((Join-Path $folder 'Morphy.png'), $png)
Add-Type -AssemblyName System.Drawing
$inputStream = [IO.MemoryStream]::new($png)
$source = [Drawing.Image]::FromStream($inputStream)
$frames = @()
try {
    foreach ($size in @(16,24,32,48,64,128,256)) {
        $bitmap = [Drawing.Bitmap]::new($size,$size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        try {
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($source,0,0,$size,$size)
            $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
            $frames += @{ Size=$size; Data=$stream.ToArray() }
        } finally { $graphics.Dispose(); $bitmap.Dispose(); $stream.Dispose() }
    }
} finally { $source.Dispose(); $inputStream.Dispose() }
$output = [IO.File]::Create((Join-Path $folder 'Morphy.ico'))
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $position = 6 + 16*$frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([uint16]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Data.Length); $writer.Write([uint32]$position)
        $position += $frame.Data.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Data) }
} finally { $writer.Dispose(); $output.Dispose() }
