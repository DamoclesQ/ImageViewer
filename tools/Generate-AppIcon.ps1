<#
.SYNOPSIS
使用 WPF 将图标矢量源绘制为包含多种分辨率的 ICO，无需额外图像库。
.PARAMETER ProjectDirectory
仓库根目录，默认使用本脚本所在目录的上级目录。
.PARAMETER PreviewFile
可选的 256 像素透明 PNG 预览保存路径。
#>
param(
    [string]$ProjectDirectory = (Split-Path -Parent $PSScriptRoot),
    [string]$PreviewFile
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$sourcePath = Join-Path $ProjectDirectory 'ImageViewer/Resources/ImageViewerIcon.xaml'
$iconPath = Join-Path $ProjectDirectory 'ImageViewer/Resources/ImageViewerIcon.ico'
$reader = [System.Xml.XmlReader]::Create($sourcePath)
try {
    $drawing = [System.Windows.Markup.XamlReader]::Load($reader)
}
finally {
    $reader.Dispose()
}
$drawing.Freeze()

$frames = @()
foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    try {
        $bounds = New-Object System.Windows.Rect(0, 0, $size, $size)
        $context.DrawImage($drawing, $bounds)
    }
    finally {
        $context.Close()
    }

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
        $size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $buffer = New-Object System.IO.MemoryStream
    try {
        $encoder.Save($buffer)
        $bytes = $buffer.ToArray()
    }
    finally {
        $buffer.Dispose()
    }

    $frames += @{ Size = $size; Bytes = $bytes }
    if ($size -eq 256 -and $PreviewFile) {
        [System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($PreviewFile), $bytes)
    }
}

# ICO 目录后依次写入各尺寸的 PNG；256 像素在目录中使用零表示。
$stream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $entrySize = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$entrySize)
        $writer.Write([byte]$entrySize)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) {
        $writer.Write([byte[]]$frame.Bytes)
    }
}
finally {
    $writer.Dispose()
}

Write-Output "Generated $iconPath (16, 24, 32, 48, 64, 128, 256 px)"
