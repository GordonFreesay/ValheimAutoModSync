param(
    [Parameter(Mandatory=$true)]
    [string]$SourcePng,

    [Parameter(Mandatory=$true)]
    [string]$OutputIco
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Generates the native helper icon from the same canonical AMS PNG used for package/in-game branding.
# The ICO contains several PNG-backed sizes so Windows can select a crisp shell/taskbar representation.

if (-not (Test-Path -LiteralPath $SourcePng -PathType Leaf)) {
    throw "Branding PNG not found: $SourcePng"
}

Add-Type -AssemblyName System.Drawing

$source = $null
$frames = @()
try {
    $source = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $SourcePng).Path)
    $sizes = @(16,24,32,48,64,128,256)

    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.DrawImage($source, 0, 0, $size, $size)
            }
            finally {
                $graphics.Dispose()
            }

            $memory = New-Object System.IO.MemoryStream
            try {
                $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
                $frames += ,$memory.ToArray()
            }
            finally {
                $memory.Dispose()
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }
}
finally {
    if ($source -ne $null) { $source.Dispose() }
}

$parent = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputIco))
if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}

$stream = [System.IO.File]::Open($OutputIco, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
$writer = New-Object System.IO.BinaryWriter $stream
try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$frames.Count)

    [UInt32]$offset = [UInt32](6 + (16 * $frames.Count))
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $size = $sizes[$i]
        [Byte]$dimension = if ($size -eq 256) { 0 } else { $size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([Byte]0)
        $writer.Write([Byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$frames[$i].Length)
        $writer.Write($offset)
        $offset = [UInt32]($offset + $frames[$i].Length)
    }

    for ($i = 0; $i -lt $frames.Count; $i++) {
        $writer.Write($frames[$i])
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

if (-not (Test-Path -LiteralPath $OutputIco -PathType Leaf) -or (Get-Item -LiteralPath $OutputIco).Length -lt 1024) {
    throw "Generated ICO failed validation: $OutputIco"
}

Write-Host ("Generated AutoModSync icon: {0}" -f $OutputIco)
