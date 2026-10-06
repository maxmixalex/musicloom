param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

Add-Type -AssemblyName System.Drawing.Common

function New-RoundedRectPath {
    param(
        [single]$X,
        [single]$Y,
        [single]$Width,
        [single]$Height,
        [single]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2

    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPngBytes {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode =
        [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode =
        [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    try {
        $scale = [single]$Size / 256.0

        $backPath = New-RoundedRectPath (30 * $scale) (75 * $scale) (150 * $scale) (150 * $scale) (30 * $scale)
        $middlePath = New-RoundedRectPath (54 * $scale) (51 * $scale) (150 * $scale) (150 * $scale) (30 * $scale)
        $frontPath = New-RoundedRectPath (78 * $scale) (27 * $scale) (150 * $scale) (150 * $scale) (30 * $scale)

        $backBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.Color]::FromArgb(255, 91, 200, 245))
        $middleBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.Color]::FromArgb(255, 45, 155, 232))
        $frontBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.Color]::FromArgb(255, 31, 92, 190))

        $graphics.FillPath($backBrush, $backPath)
        $graphics.FillPath($middleBrush, $middlePath)
        $graphics.FillPath($frontBrush, $frontPath)

        $white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
        $pen = [System.Drawing.Pen]::new(
            $white,
            [single](13 * $scale))
        $pen.StartCap =
            [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap =
            [System.Drawing.Drawing2D.LineCap]::Round

        $graphics.DrawLine(
            $pen,
            [single](121 * $scale),
            [single](65 * $scale),
            [single](108 * $scale),
            [single](153 * $scale))
        $graphics.DrawLine(
            $pen,
            [single](166 * $scale),
            [single](59 * $scale),
            [single](153 * $scale),
            [single](147 * $scale))
        $graphics.DrawLine(
            $pen,
            [single](101 * $scale),
            [single](94 * $scale),
            [single](185 * $scale),
            [single](80 * $scale))
        $graphics.DrawLine(
            $pen,
            [single](96 * $scale),
            [single](128 * $scale),
            [single](180 * $scale),
            [single](114 * $scale))

        # Two small front-color bridges make the sharp look woven rather than
        # simply crossed.
        $bridgeBrush =
            [System.Drawing.SolidBrush]::new(
                [System.Drawing.Color]::FromArgb(
                    255, 31, 92, 190))
        $graphics.FillRectangle(
            $bridgeBrush,
            [single](113 * $scale),
            [single](91 * $scale),
            [single](19 * $scale),
            [single](14 * $scale))
        $graphics.FillRectangle(
            $bridgeBrush,
            [single](149 * $scale),
            [single](111 * $scale),
            [single](19 * $scale),
            [single](14 * $scale))

        $stream = [System.IO.MemoryStream]::new()
        $bitmap.Save(
            $stream,
            [System.Drawing.Imaging.ImageFormat]::Png)
        return $stream.ToArray()
    }
    finally {
        if ($pen) { $pen.Dispose() }
        if ($bridgeBrush) { $bridgeBrush.Dispose() }
        if ($backBrush) { $backBrush.Dispose() }
        if ($middleBrush) { $middleBrush.Dispose() }
        if ($frontBrush) { $frontBrush.Dispose() }
        if ($backPath) { $backPath.Dispose() }
        if ($middlePath) { $middlePath.Dispose() }
        if ($frontPath) { $frontPath.Dispose() }
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = @()

foreach ($size in $sizes) {
    $images += ,(New-IconPngBytes -Size $size)
}

$directory = [System.IO.Path]::GetDirectoryName($OutputPath)
if ($directory) {
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
}

$stream = [System.IO.File]::Open(
    $OutputPath,
    [System.IO.FileMode]::Create,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::None)

$writer = [System.IO.BinaryWriter]::new($stream)

try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)

    $offset = 6 + (16 * $sizes.Count)

    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $size = $sizes[$i]
        $bytes = $images[$i]

        $writer.Write(
            [byte]($(if ($size -eq 256) { 0 } else { $size })))
        $writer.Write(
            [byte]($(if ($size -eq 256) { 0 } else { $size })))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$bytes.Length)
        $writer.Write([uint32]$offset)

        $offset += $bytes.Length
    }

    foreach ($bytes in $images) {
        $writer.Write($bytes)
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}
