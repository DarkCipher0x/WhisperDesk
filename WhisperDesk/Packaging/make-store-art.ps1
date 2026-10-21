<#
    Draws the Microsoft Store listing images into Packaging\StoreListing:
      art\          optional Store logos and hero art from Assets\logo.png (named after the Partner Center box)
      screenshots\  1920x1080 screenshots: the user guide's synthetic screenshots (docs\img, all on the website's
                    approved-images list) placed at their own size, never enlarged, on the brand background with a caption.
    The Store needs desktop screenshots of at least 1366x768; the guide's are smaller, hence the canvas.
    Only the brand and the guide's fictional demo content are shown: never a real recording or name.

      powershell -ExecutionPolicy Bypass -File Packaging\make-store-art.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$out = Join-Path $PSScriptRoot 'StoreListing'
$artDir = Join-Path $out 'art'; $shotDir = Join-Path $out 'screenshots'
foreach ($d in $artDir, $shotDir) { if (Test-Path $d) { Remove-Item $d -Recurse -Force }; New-Item -ItemType Directory -Force $d | Out-Null }
$icon = [System.Drawing.Image]::FromFile((Join-Path $root 'Assets\logo.png'))

# Brand colours (the app's dark terminal theme)
$bgTop    = [System.Drawing.Color]::FromArgb(255, 16, 42, 46)
$bgBottom = [System.Drawing.Color]::FromArgb(255, 9, 20, 23)
$teal     = [System.Drawing.Color]::FromArgb(255, 61, 214, 200)
$text     = [System.Drawing.Color]::FromArgb(255, 236, 244, 244)
$dim      = [System.Drawing.Color]::FromArgb(255, 150, 178, 180)

function New-Canvas([int]$w, [int]$h, [bool]$background) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    if ($background) {
        $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $bgTop, $bgBottom, 90
        $g.FillRectangle($brush, $rect); $brush.Dispose()
        $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
        $gw = [int]($w * 0.9); $gh = [int]($h * 0.6)
        $glow.AddEllipse([int](($w - $gw) / 2), [int]($h * 0.05), $gw, $gh)
        $pb = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
        $pb.CenterColor = [System.Drawing.Color]::FromArgb(60, $teal)
        $pb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, $teal))
        $g.FillPath($pb, $glow); $pb.Dispose(); $glow.Dispose()
    } else { $g.Clear([System.Drawing.Color]::Transparent) }
    return @($bmp, $g)
}

function Draw-Centered($g, [string]$s, [string]$font, [float]$size, $color, [int]$w, [float]$y) {
    $f = New-Object System.Drawing.Font $font, $size, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $b = New-Object System.Drawing.SolidBrush $color
    $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString($s, $f, $b, (New-Object System.Drawing.RectangleF 0, $y, $w, ($size * 1.6)), $fmt)
    $f.Dispose(); $b.Dispose(); $fmt.Dispose()
}

function Save($bmp, $g, [string]$path) {
    $g.Dispose(); $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    "{0,-48} {1:N0} bytes" -f (Split-Path $path -Leaf), (Get-Item $path).Length
}

# ---- Store art ----
$tagline = 'Private, on-device transcription'
$c = New-Canvas 720 1080 $true; $g = $c[1]
$g.DrawImage($icon, 160, 190, 400, 400)
Draw-Centered $g 'WhisperDesk' 'Segoe UI Semibold' 84 $text 720 650
Draw-Centered $g 'Private, on-device' 'Segoe UI' 36 $dim 720 780
Draw-Centered $g 'transcription' 'Segoe UI' 36 $dim 720 828
Save $c[0] $g (Join-Path $artDir 'poster-art-9x16-720x1080.png')

$c = New-Canvas 1080 1080 $true; $g = $c[1]
$g.DrawImage($icon, 300, 170, 480, 480)
Draw-Centered $g 'WhisperDesk' 'Segoe UI Semibold' 104 $text 1080 700
Draw-Centered $g $tagline 'Segoe UI' 38 $dim 1080 860
Save $c[0] $g (Join-Path $artDir 'box-art-1x1-1080x1080.png')

# Super hero art must not contain the product title.
$c = New-Canvas 1920 1080 $true; $g = $c[1]
$g.DrawImage($icon, 704, 284, 512, 512)
Save $c[0] $g (Join-Path $artDir 'super-hero-art-16x9-1920x1080.png')

foreach ($s in 300, 150, 71) {
    $c = New-Canvas $s $s $false; $g = $c[1]
    $g.DrawImage($icon, 0, 0, $s, $s)
    Save $c[0] $g (Join-Path $artDir "app-tile-icon-${s}x${s}.png")
}

# ---- Screenshots ----
$shots = @(
    @('files.png',      'Transcribe audio and video files entirely on your PC'),
    @('live.png',       'Live transcription with speaker identification'),
    @('review.png',     'Review every line, name speakers, keep an audited edit log'),
    @('library.png',    'A searchable library of your recordings and transcripts')
)
$n = 0
foreach ($s in $shots) {
    $n++
    $img = [System.Drawing.Image]::FromFile((Join-Path $root "docs\img\$($s[0])"))
    $c = New-Canvas 1920 1080 $true; $g = $c[1]
    $top = 140; $avail = 1080 - $top - 40
    if ($img.Width -gt 1840 -or $img.Height -gt $avail) { throw "$($s[0]) is larger than the canvas" }
    $x = [int]((1920 - $img.Width) / 2); $y = $top + [int](($avail - $img.Height) / 2)
    $g.InterpolationMode = 'NearestNeighbor'   # drawn 1:1, no resampling
    $g.DrawImage($img, (New-Object System.Drawing.Rectangle $x, $y, $img.Width, $img.Height))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(110, $teal)), 2
    $g.DrawRectangle($pen, $x - 1, $y - 1, $img.Width + 1, $img.Height + 1); $pen.Dispose()
    Draw-Centered $g $s[1] 'Segoe UI Semibold' 48 $text 1920 42
    $img.Dispose()
    Save $c[0] $g (Join-Path $shotDir ("{0:D2}-{1}" -f $n, $s[0]))
}
$icon.Dispose()
