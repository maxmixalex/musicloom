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
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $backgroundPath = $null
    $backgroundBrush = $null
    $threadPen = $null
    $whitePen = $null
    $cyanPen = $null
    $bridgeBrush = $null
    $noteBrush = $null
    $upperWave = $null
    $lowerWave = $null

    try {
        $scale = [single]$Size / 256.0
        $backgroundPath = New-RoundedRectPath -X (24 * $scale) -Y (24 * $scale) -Width (208 * $scale) -Height (208 * $scale) -Radius (55 * $scale)

        $backgroundBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.Color]::FromArgb(255, 11, 92, 173))
        $graphics.FillPath($backgroundBrush, $backgroundPath)

        $threadPen = [System.Drawing.Pen]::new(
            [System.Drawing.Color]::FromArgb(184, 158, 220, 255),
            [single](7 * $scale))
        $threadPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $threadPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

        foreach ($x in @(80, 112, 144, 176)) {
            $graphics.DrawLine(
                $threadPen,
                [single]($x * $scale),
                [single](63 * $scale),
                [single]($x * $scale),
                [single](193 * $scale))
        }

        $upperWave = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $upperWave.StartFigure()
        $upperWave.AddBezier(
            [single](55 * $scale), [single](116 * $scale),
            [single](75 * $scale), [single](96 * $scale),
            [single](91 * $scale), [single](96 * $scale),
            [single](107 * $scale), [single](116 * $scale))
        $upperWave.AddBezier(
            [single](107 * $scale), [single](116 * $scale),
            [single](123 * $scale), [single](136 * $scale),
            [single](139 * $scale), [single](136 * $scale),
            [single](155 * $scale), [single](116 * $scale))
        $upperWave.AddBezier(
            [single](155 * $scale), [single](116 * $scale),
            [single](171 * $scale), [single](96 * $scale),
            [single](187 * $scale), [single](96 * $scale),
            [single](204 * $scale), [single](116 * $scale))

        $whitePen = [System.Drawing.Pen]::new(
            [System.Drawing.Color]::White,
            [single](16 * $scale))
        $whitePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $whitePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $whitePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $graphics.DrawPath($whitePen, $upperWave)

        $lowerWave = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $lowerWave.StartFigure()
        $lowerWave.AddBezier(
            [single](55 * $scale), [single](144 * $scale),
            [single](75 * $scale), [single](164 * $scale),
            [single](91 * $scale), [single](164 * $scale),
            [single](107 * $scale), [single](144 * $scale))
        $lowerWave.AddBezier(
            [single](107 * $scale), [single](144 * $scale),
            [single](123 * $scale), [single](124 * $scale),
            [single](139 * $scale), [single](124 * $scale),
            [single](155 * $scale), [single](144 * $scale))
        $lowerWave.AddBezier(
            [single](155 * $scale), [single](144 * $scale),
            [single](171 * $scale), [single](164 * $scale),
            [single](187 * $scale), [single](164 * $scale),
            [single](204 * $scale), [single](144 * $scale))

        $cyanPen = [System.Drawing.Pen]::new(
            [System.Drawing.Color]::FromArgb(255, 72, 199, 232),
            [single](12 * $scale))
        $cyanPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $cyanPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $cyanPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $graphics.DrawPath($cyanPen, $lowerWave)

        $bridgeBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.Color]::FromArgb(255, 11, 92, 173))
        $graphics.FillRectangle(
            $bridgeBrush,
            [single](105 * $scale),
            [single](103 * $scale),
            [single](14 * $scale),
            [single](24 * $scale))
        $graphics.FillRectangle(
            $bridgeBrush,
            [single](137 * $scale),
            [single](133 * $scale),
            [single](14 * $scale),
            [single](24 * $scale))

        $graphics.DrawLine(
            $whitePen,
            [single](194 * $scale),
            [single](112 * $scale),
            [single](194 * $scale),
            [single](78 * $scale))

        $noteBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.Color]::White)
        $graphics.FillEllipse(
            $noteBrush,
            [single](177 * $scale),
            [single](144 * $scale),
            [single](35 * $scale),
            [single](27 * $scale))

        $memory = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Output -NoEnumerate ([byte[]]$memory.ToArray())
        }
        finally {
            $memory.Dispose()
        }
    }
    finally {
        if ($backgroundPath) { $backgroundPath.Dispose() }
        if ($backgroundBrush) { $backgroundBrush.Dispose() }
        if ($threadPen) { $threadPen.Dispose() }
        if ($whitePen) { $whitePen.Dispose() }
        if ($cyanPen) { $cyanPen.Dispose() }
        if ($bridgeBrush) { $bridgeBrush.Dispose() }
        if ($noteBrush) { $noteBrush.Dispose() }
        if ($upperWave) { $upperWave.Dispose() }
        if ($lowerWave) { $lowerWave.Dispose() }
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

[byte[]]$png = New-IconPngBytes -Size 256

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
    $writer.Write([uint16]1)

    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$png.Length)
    $writer.Write([uint32]22)

    $writer.Write([byte[]]$png)
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}
