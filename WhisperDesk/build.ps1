# Builds WhisperDesk release artifacts into .\dist\<ver>\ (one folder per version; a built version is never overwritten)
#   WhisperDesk-<ver>-x64.msi          installer (Start menu shortcut, upgrades older versions)
#   WhisperDesk-<ver>-x64-portable.zip  unzip-and-run folder, no install needed
#   WhisperDesk-<ver>-Addon-SDK.zip     add-on SDK for developers (contract DLL, XML docs, README, sample source)
#   WhisperDesk-<ver>-Addon-WordExport.zip  the sample Word export add-on, ready to install
#   WhisperDesk-<ver>-User-Admin-Guide.pdf
#   WhisperDesk-<ver>-SHA256SUMS.txt    checksums of the release files above
#   store\WhisperDesk-<ver>-x64.msix    Microsoft Store package (upload to Partner Center; see docs\MICROSOFT-STORE.md)
#
# This script only builds; it never publishes anything. Releasing (checks, the public repository, the GitHub Release,
# the website hand-over and Discord) is release.ps1, which runs this as its build step: see docs\RELEASE-PIPELINE.md.
# -OutDir builds somewhere else (release.ps1 -DryRun uses it); -Overwrite replaces an existing dist\<ver>\ build of a
# version that hasn't been released.
#
# Supply-chain checks (the build stops if any fails):
#   * restore in locked mode: packages.lock.json must match exactly (no silent version changes)
#   * NuGet audit + "dotnet list package --vulnerable": no known-vulnerable package, direct or transitive
#   * the security test suite (..\WhisperDesk.Tests) passes
# Optional Authenticode signing: set WD_SIGN_THUMBPRINT to a code-signing certificate's thumbprint (CurrentUser\My or
# LocalMachine\My) and have signtool.exe (Windows SDK) on PATH. Unsigned builds print a warning.
param([switch]$SkipTests, [string]$OutDir, [switch]$Overwrite)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

[xml]$proj = Get-Content .\WhisperDesk.csproj
$version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = if ($OutDir) { $OutDir } else { Join-Path $PSScriptRoot "dist\$version" }
Write-Host "Building WhisperDesk $version into $out" -ForegroundColor Cyan
if ((Test-Path "$out\WhisperDesk-$version-x64.msi") -and -not $Overwrite) {
    throw "$out already holds a $version build. Bump <Version> in WhisperDesk.csproj (bug fix: x.y.Z+1, feature: x.Y+1.0), or pass -Overwrite for a version that hasn't been released."
}
if ($SkipTests) { Write-Warning "-SkipTests: this build must not be released (release.ps1 always runs the tests)." }

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
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

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
$msi = "$out\WhisperDesk-$version-x64.msi"
dotnet tool run wix build Installer\WhisperDesk.wxs -arch x64 -ext WixToolset.UI.wixext `
    -d Version=$version -d PublishDir="$PSScriptRoot\publish" -wx -o $msi
if ($LASTEXITCODE) { throw "wix build failed" }
Sign $msi

Remove-Item "$out\*.wixpdb" -ErrorAction SilentlyContinue
$zip = "$out\WhisperDesk-$version-x64-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path publish\* -DestinationPath $zip

Copy-Item docs\WhisperDesk-User-Admin-Guide.pdf "$out\WhisperDesk-$version-User-Admin-Guide.pdf" -Force

# ---- Microsoft Store package (MSIX) ----
# Same publish folder, packaged with Packaging\AppxManifest.xml. Left unsigned on purpose: the Store signs every package
# with Microsoft's certificate after certification. Kept apart in <out>\store (it isn't for the website or SHA256SUMS).
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
New-Item -ItemType Directory -Force $out\store | Out-Null
$msix = if ($storeReady) { "$out\store\WhisperDesk-$version-x64.msix" } else { "$out\store\WhisperDesk-$version-x64-localtest.msix" }
Remove-Item $out\store\*.msix -ErrorAction SilentlyContinue
& $makeappx pack /d $msixRoot /p $msix /o | Out-Null
if ($LASTEXITCODE) { throw "makeappx failed" }
Write-Host ("MSIX: {0} ({1})" -f $msix, $(if ($storeReady) { 'ready to upload to Partner Center' } else { 'local test identity' })) -ForegroundColor Green

# ---- add-on SDK and the Word export add-on ----
# Built in a temporary folder with no PDB and no local paths, from sdk\ (contract DLL, XML docs, README, sample source)
$addonStage = Join-Path ([IO.Path]::GetTempPath()) ('wd-addons-' + [guid]::NewGuid().ToString('N'))
try {
    $sdkStage = Join-Path $addonStage 'sdk'
    New-Item -ItemType Directory -Force $sdkStage | Out-Null
    Get-ChildItem sdk -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object {
        $dest = Join-Path $sdkStage $_.FullName.Substring((Resolve-Path sdk).Path.Length + 1)
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item $_.FullName $dest
    }
    dotnet build ..\WhisperDesk.Addons\WhisperDesk.Addons.csproj -c Release -nologo -v q -o "$addonStage\contract" -p:Deterministic=true -p:ContinuousIntegrationBuild=true | Out-Null
    if ($LASTEXITCODE) { throw 'building the add-on contract failed' }
    Copy-Item "$addonStage\contract\WhisperDesk.Addons.dll", "$addonStage\contract\WhisperDesk.Addons.xml" $sdkStage -Force
    $sample = Join-Path $sdkStage 'SampleWordExport'
    dotnet build $sample -c Release -nologo -v q -o "$addonStage\sample" -p:DebugType=none -p:DebugSymbols=false `
        -p:Deterministic=true "-p:PathMap=$sample=/_/" -p:ContinuousIntegrationBuild=true | Out-Null
    if ($LASTEXITCODE) { throw 'building the Word export sample failed' }
    Get-ChildItem $sample -Directory | Where-Object Name -in 'bin', 'obj' | Remove-Item -Recurse -Force
    $sdkZip = "$out\WhisperDesk-$version-Addon-SDK.zip"
    if (Test-Path $sdkZip) { Remove-Item $sdkZip }
    Compress-Archive -Path "$sdkStage\*" -DestinationPath $sdkZip

    $wx = Join-Path $addonStage 'wordexport'
    New-Item -ItemType Directory -Force "$wx\WordExport" | Out-Null
    Copy-Item "$addonStage\sample\SampleWordExport.dll", "$sample\addon.json" "$wx\WordExport"
    [IO.File]::WriteAllText("$wx\INSTALL.txt", @'
Word export (sample) add-on for WhisperDesk 1.7 or later
========================================================
1. In WhisperDesk open Tools > Add-ons > Open add-ons folder.
2. Copy the whole WordExport folder (SampleWordExport.dll + addon.json) into that folder.
3. Restart WhisperDesk and open Tools > Add-ons. Select "Word export (sample)", click
   Review permissions..., approve "finished transcripts" and pick a new, empty folder to export to,
   then switch it on and click Save settings.
The add-on runs in a sandbox: it can only write to the folder you picked and has no network access.
Source code: https://github.com/DarkCipher0x/WhisperDesk (WhisperDesk\sdk\SampleWordExport).
'@)
    $wxZip = "$out\WhisperDesk-$version-Addon-WordExport.zip"
    if (Test-Path $wxZip) { Remove-Item $wxZip }
    Compress-Archive -Path "$wx\*" -DestinationPath $wxZip
}
finally { Remove-Item $addonStage -Recurse -Force -ErrorAction SilentlyContinue }

# ---- checksums for this version's files (the checksum file itself is left out) ----
$sumsName = "WhisperDesk-$version-SHA256SUMS.txt"
$files = Get-ChildItem $out -File | Where-Object { $_.Name -like "WhisperDesk-$version-*" -and $_.Name -ne $sumsName }
$sums = $files | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
Set-Content -Path "$out\$sumsName" -Value $sums -Encoding ascii
Get-ChildItem $out -File | Where-Object { $_.Name -like "WhisperDesk-$version-*" } | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
Write-Host "WhisperDesk $version built in $out" -ForegroundColor Green
