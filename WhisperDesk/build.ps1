# Builds WhisperDesk release artifacts into .\dist
#   WhisperDesk-<ver>-x64.msi           installer (Start menu shortcut, upgrades older versions)
#   WhisperDesk-<ver>-x64-portable.zip  unzip-and-run folder, no install needed
#   SHA256SUMS.txt                      checksums of the release files (publish them with the release)
#   store\WhisperDesk-<ver>-x64.msix    Microsoft Store package (upload to Partner Center; see docs\MICROSOFT-STORE.md)
#
# Supply-chain checks (the build stops if any fails):
#   * restore in locked mode: packages.lock.json must match exactly (no silent version changes)
#   * NuGet audit + "dotnet list package --vulnerable": no known-vulnerable package, direct or transitive
#   * the security test suite (..\WhisperDesk.Tests) passes
# Optional Authenticode signing: set WD_SIGN_THUMBPRINT to a code-signing certificate's thumbprint (CurrentUser\My or
# LocalMachine\My) and have signtool.exe (Windows SDK) on PATH. Unsigned builds print a warning.
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

[xml]$proj = Get-Content .\WhisperDesk.csproj
$version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "Building WhisperDesk $version" -ForegroundColor Cyan

# ---- dependencies: locked, audited ----
dotnet restore .\WhisperDesk.csproj --locked-mode -nologo -v q
if ($LASTEXITCODE) { throw "Restore failed: packages.lock.json doesn't match the project (run 'dotnet restore --force-evaluate' deliberately, review the diff, commit it)." }
$vuln = dotnet list .\WhisperDesk.csproj package --vulnerable --include-transitive 2>&1 | Out-String
if ($vuln -match 'has the following vulnerable packages') { Write-Host $vuln; throw "Vulnerable packages found. Update or pin them before releasing." }
Write-Host "Dependencies: locked, no known vulnerabilities." -ForegroundColor Green

# ---- tests ----
if (-not $SkipTests -and (Test-Path ..\WhisperDesk.Tests\WhisperDesk.Tests.csproj)) {
    dotnet test ..\WhisperDesk.Tests\WhisperDesk.Tests.csproj -nologo -v q
    if ($LASTEXITCODE) { throw "Security tests failed." }
}

if (Test-Path publish) { Remove-Item publish -Recurse -Force }
New-Item -ItemType Directory -Force dist | Out-Null

dotnet publish .\WhisperDesk.csproj -c Release -o publish -nologo -v q
if ($LASTEXITCODE) { throw "dotnet publish failed" }

# ---- signing (optional) ----
function Sign([string]$file) {
    if (-not $env:WD_SIGN_THUMBPRINT) { return }
    & signtool.exe sign /sha1 $env:WD_SIGN_THUMBPRINT /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /d "WhisperDesk" $file
    if ($LASTEXITCODE) { throw "signing failed: $file" }
}
if ($env:WD_SIGN_THUMBPRINT) { Sign "publish\WhisperDesk.exe" } else { Write-Warning "Not code-signed (set WD_SIGN_THUMBPRINT to sign). Users will see an 'unknown publisher' warning." }

dotnet tool restore | Out-Null
dotnet tool run wix extension add -g WixToolset.UI.wixext/5.0.2 | Out-Null
$msi = "dist\WhisperDesk-$version-x64.msi"
dotnet tool run wix build Installer\WhisperDesk.wxs -arch x64 -ext WixToolset.UI.wixext `
    -d Version=$version -d PublishDir="$PSScriptRoot\publish" -wx -o $msi
if ($LASTEXITCODE) { throw "wix build failed" }
Sign $msi

Remove-Item "dist\*.wixpdb" -ErrorAction SilentlyContinue
$zip = "dist\WhisperDesk-$version-x64-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path publish\* -DestinationPath $zip

Copy-Item docs\WhisperDesk-User-Admin-Guide.pdf "dist\WhisperDesk-$version-User-Admin-Guide.pdf" -Force

# ---- Microsoft Store package (MSIX) ----
# Same publish folder, packaged with Packaging\AppxManifest.xml. Left unsigned on purpose: the Store signs every package
# with Microsoft's certificate after certification. Kept apart in dist\store (it isn't for the website or SHA256SUMS).
dotnet restore .\Packaging\StoreTools.csproj --locked-mode -nologo -v q
if ($LASTEXITCODE) { throw "Restore of the MSIX tools failed: Packaging\packages.lock.json doesn't match." }
[xml]$tools = Get-Content .\Packaging\StoreTools.csproj
$toolsVersion = ($tools.Project.ItemGroup.PackageReference | Where-Object Include -eq 'Microsoft.Windows.SDK.BuildTools').Version
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE .nuget\packages }
$sdkBin = Get-ChildItem (Join-Path $nugetRoot "microsoft.windows.sdk.buildtools\$toolsVersion\bin\*\x64") -Directory | Select-Object -First 1
if (-not $sdkBin) { throw "makeappx.exe not found in Microsoft.Windows.SDK.BuildTools $toolsVersion." }
$makeappx = Join-Path $sdkBin.FullName makeappx.exe
$makepri = Join-Path $sdkBin.FullName makepri.exe

$identity = Get-Content .\Packaging\store-identity.json -Raw | ConvertFrom-Json
$storeReady = $identity.IdentityName -and $identity.PublisherDisplayName -and ($identity.Publisher -match '^CN=[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$')
if (-not $storeReady) {
    Write-Warning "Packaging\store-identity.json isn't filled in from Partner Center: the MSIX is for local testing only and can't be uploaded."
    $identity = [pscustomobject]@{ IdentityName = 'WhisperDesk.LocalTest'; Publisher = 'CN=WhisperDesk Local Test'; PublisherDisplayName = 'WhisperDesk (local test)' }
}
# The tile images are generated from Assets\logo.png and not kept in the public source: draw them when missing
if (-not (Test-Path .\Packaging\Images)) { & .\Packaging\make-images.ps1 | Out-Null }
$esc = { param($s) [System.Security.SecurityElement]::Escape($s) }
$msixRoot = "obj\msix\root"; $priDir = "obj\msix\pri"
if (Test-Path obj\msix) { Remove-Item obj\msix -Recurse -Force }
New-Item -ItemType Directory -Force $msixRoot, $priDir | Out-Null
Copy-Item publish\* $msixRoot -Recurse
$manifest = (Get-Content .\Packaging\AppxManifest.xml -Raw).
    Replace('$IDENTITY_NAME$', (& $esc $identity.IdentityName)).
    Replace('$PUBLISHER_DISPLAY_NAME$', (& $esc $identity.PublisherDisplayName)).
    Replace('$PUBLISHER$', (& $esc $identity.Publisher)).
    Replace('$VERSION$', "$version.0")
foreach ($dir in $msixRoot, $priDir) {
    Set-Content (Join-Path $dir AppxManifest.xml) $manifest -Encoding utf8
    Copy-Item .\Packaging\Images (Join-Path $dir Images) -Recurse
}
# resources.pri maps Images\Square44x44Logo.png etc. to the scale/size variants. Indexed from a folder holding only the
# images and the manifest, so the program files aren't listed as resources.
# The default config splits scale variants into separate resource packs (for bundles); this is a single MSIX, so every
# variant stays in the one resources.pri.
& $makepri createconfig /cf obj\msix\priconfig.xml /dq en-US /pv 10.0.0 /o | Out-Null
[xml]$priConfig = Get-Content obj\msix\priconfig.xml
$priConfig.SelectNodes('//packaging') | ForEach-Object { $_.ParentNode.RemoveChild($_) } | Out-Null
$priConfig.Save((Resolve-Path obj\msix\priconfig.xml).Path)
& $makepri new /pr $priDir /cf obj\msix\priconfig.xml /mn "$priDir\AppxManifest.xml" /of "$msixRoot\resources.pri" /o | Out-Null
if ($LASTEXITCODE) { throw "makepri failed" }
New-Item -ItemType Directory -Force dist\store | Out-Null
$msix = if ($storeReady) { "dist\store\WhisperDesk-$version-x64.msix" } else { "dist\store\WhisperDesk-$version-x64-localtest.msix" }
Remove-Item dist\store\*.msix -ErrorAction SilentlyContinue
& $makeappx pack /d $msixRoot /p $msix /o | Out-Null
if ($LASTEXITCODE) { throw "makeappx failed" }
Write-Host ("MSIX: {0} ({1})" -f $msix, $(if ($storeReady) { 'ready to upload to Partner Center' } else { 'local test identity' })) -ForegroundColor Green

# ---- checksums for this version's files ----
$files = Get-ChildItem dist -File | Where-Object { $_.Name -like "WhisperDesk-$version-*" }
$sums = $files | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
Set-Content -Path "dist\WhisperDesk-$version-SHA256SUMS.txt" -Value $sums -Encoding ascii
Get-ChildItem dist | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
