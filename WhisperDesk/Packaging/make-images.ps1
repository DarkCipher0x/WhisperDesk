# Generates the MSIX tile and icon images in Packaging\Images from Assets\logo.png (transparent).
# Run once after the logo changes; the images are kept with the source so builds don't depend on this script.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$logo = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot ..\Assets\logo.png | Resolve-Path))
$out = Join-Path $PSScriptRoot Images
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory $out | Out-Null

# Draws the logo centred on a transparent canvas; $fill = share of the shorter side the logo takes.
function Save([string]$name, [int]$w, [int]$h, [double]$fill) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [int][math]::Round([math]::Min($w, $h) * $fill)
    $g.DrawImage($logo, [int](($w - $s) / 2), [int](($h - $s) / 2), $s, $s)
    $g.Dispose()
    $bmp.Save((Join-Path $out $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

foreach ($scale in 100, 125, 150, 200, 400) {
    $f = $scale / 100
    Save "Square44x44Logo.scale-$scale.png"   ([int](44 * $f))  ([int](44 * $f))  1.0
    Save "Square150x150Logo.scale-$scale.png" ([int](150 * $f)) ([int](150 * $f)) 0.66
    Save "Wide310x150Logo.scale-$scale.png"   ([int](310 * $f)) ([int](150 * $f)) 0.66
    Save "SmallTile.scale-$scale.png"         ([int](71 * $f))  ([int](71 * $f))  0.8
    Save "LargeTile.scale-$scale.png"         ([int](310 * $f)) ([int](310 * $f)) 0.6
    Save "StoreLogo.scale-$scale.png"         ([int](50 * $f))  ([int](50 * $f))  1.0
    Save "SplashScreen.scale-$scale.png"      ([int](620 * $f)) ([int](300 * $f)) 0.66
}
# Taskbar, Start list and file-type icons at exact sizes (plated and unplated).
foreach ($t in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
    Save "Square44x44Logo.targetsize-$t.png" $t $t 1.0
    Save "Square44x44Logo.targetsize-$($t)_altform-unplated.png" $t $t 1.0
    Save "Square44x44Logo.targetsize-$($t)_altform-lightunplated.png" $t $t 1.0
}
$logo.Dispose()
Write-Host "Images written to $out"
