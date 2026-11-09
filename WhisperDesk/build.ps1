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

# ---- the bundled Visual C++ runtime (WhisperDesk.csproj, BundleVcRuntime) ----
# Every copy must be Microsoft-signed and at least 14.40 (ONNX Runtime needs it), and every Visual C++ DLL that a native
# library in the folder imports must be one of them: 1.8.1 from the Store had none and couldn't load onnxruntime.dll.
function Get-PeImports([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    if ($b.Length -lt 64 -or $b[0] -ne 0x4D -or $b[1] -ne 0x5A) { return @() }
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    $nsec = [BitConverter]::ToUInt16($b, $pe + 6); $optSize = [BitConverter]::ToUInt16($b, $pe + 20); $opt = $pe + 24
    $dd = $opt + $(if ([BitConverter]::ToUInt16($b, $opt) -eq 0x20B) { 112 } else { 96 })
    $secs = 0..($nsec - 1) | ForEach-Object { $s = $opt + $optSize + $_ * 40
        [pscustomobject]@{ VSize = [BitConverter]::ToUInt32($b, $s + 8); VA = [BitConverter]::ToUInt32($b, $s + 12); RawSize = [BitConverter]::ToUInt32($b, $s + 16); Raw = [BitConverter]::ToUInt32($b, $s + 20) } }
    $off = { param($rva) foreach ($s in $secs) { if ($rva -ge $s.VA -and $rva -lt $s.VA + [math]::Max($s.VSize, $s.RawSize)) { return [int]($rva - $s.VA + $s.Raw) } }; -1 }
    $names = @()
    foreach ($dir in @(@{ Index = 1; Size = 20; NameAt = 12 }, @{ Index = 13; Size = 32; NameAt = 4 })) {   # imports, delay-load imports
        $rva = [BitConverter]::ToUInt32($b, $dd + $dir.Index * 8)
        if (-not $rva) { continue }
        $p = & $off $rva
        while ($p -ge 0 -and $p + $dir.Size -le $b.Length) {
            $nameRva = [BitConverter]::ToUInt32($b, $p + $dir.NameAt)
            if (-not $nameRva) { break }
            $n = & $off $nameRva; $e = [Array]::IndexOf($b, [byte]0, $n)
            $names += [Text.Encoding]::ASCII.GetString($b, $n, $e - $n).ToLowerInvariant()
            $p += $dir.Size
        }
    }
    $names
}
$vcFiles = 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'vcomp140.dll'
foreach ($f in $vcFiles) {
    $p = "publish\$f"
    if (-not (Test-Path $p)) { throw "$f isn't in the publish folder (BundleVcRuntime in WhisperDesk.csproj)" }
    $sig = Get-AuthenticodeSignature $p
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw "$f isn't validly signed by Microsoft ($($sig.Status))" }
    $v = (Get-Item $p).VersionInfo
    if ($v.FileMajorPart -ne 14 -or $v.FileMinorPart -lt 40) { throw "$f is $($v.FileVersion); update the Microsoft Visual C++ Redistributable (x64) on this PC to 14.40 or newer" }
}
$vcNeeded = Get-ChildItem publish -Recurse -Include *.dll, *.exe | ForEach-Object { Get-PeImports $_.FullName } |
    Where-Object { $_ -match '^(msvcp|vcruntime|vcomp|concrt|vccorlib)\d' -and $_ -ne 'vcruntime140_cor3.dll' } | Sort-Object -Unique
$vcMissing = $vcNeeded | Where-Object { $_ -notin $vcFiles }
if ($vcMissing) { throw "Native libraries import Visual C++ DLLs that aren't bundled: $($vcMissing -join ', '). Add them to VcRuntimeFile in WhisperDesk.csproj and Services\VcRuntime.cs." }
Write-Host ("Visual C++ runtime: {0} bundled, Microsoft-signed; covers every native import ({1})." -f (Get-Item publish\msvcp140.dll).VersionInfo.FileVersion, ($vcNeeded -join ', ')) -ForegroundColor Green

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
# The add-on contract DLL in sdk\ is an AnyCPU reference copy for developers (it's in the Add-on SDK zip too); in an x64
# package the App Certification Kit's "Platform appropriate files" test flags it, and the app itself never loads it.
Remove-Item "$msixRoot\sdk\WhisperDesk.Addons.dll", "$msixRoot\sdk\WhisperDesk.Addons.xml" -ErrorAction SilentlyContinue
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

# ---- signed update information for the in-app updater (Services\Updater.cs) ----
# update.json describes the MSI (size + SHA-256); the app offers it only when update.json.sig (ECDSA P-256) and
# update.json.pq.sig (ML-DSA-65) both verify against the keys pinned in Updater.cs. The package has no "url": the app
# resolves the file name next to update.json, so the same signed files work from GitHub or from a mirror of your own.
$notesText = ''
$cl = [regex]::Match([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CHANGELOG.md'), [Text.Encoding]::UTF8), "(?ms)^## $([regex]::Escape($version))\s*\r?\n(.*?)(?=^## |\z)")
if ($cl.Success) { $notesText = ($cl.Groups[1].Value.Trim() -replace '\*\*', '' -replace '`', '' -replace "\r\n", "`n") }
$msiItem = Get-Item $msi
$manifest = [ordered]@{
    product = 'WhisperDesk'; version = $version; released = (Get-Date -Format 'yyyy-MM-dd'); notes = $notesText
    packages = @([ordered]@{ kind = 'msi'; arch = 'x64'; file = $msiItem.Name; bytes = $msiItem.Length; sha256 = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant() })
}
$updateJson = "$out\update.json"
Remove-Item "$updateJson", "$updateJson.sig", "$updateJson.pq.sig" -ErrorAction SilentlyContinue
[IO.File]::WriteAllText($updateJson, ($manifest | ConvertTo-Json -Depth 4) + "`n", (New-Object Text.UTF8Encoding $false))
# models.json: the model list (models\catalog.json, byte for byte, also built into the app) signed with the same keys.
# Every copy, the Microsoft Store one included, fetches it from the latest release (Tools > Models > Check for new
# models), so new or updated models reach it without an app update. The app only uses a newer, validly signed list.
$modelsJson = "$out\models.json"
Remove-Item "$modelsJson", "$modelsJson.sig", "$modelsJson.pq.sig" -ErrorAction SilentlyContinue
Copy-Item (Join-Path $PSScriptRoot 'models\catalog.json') $modelsJson
$keyDir = Join-Path $env:LOCALAPPDATA 'DarkCipher\keys'
if ((Test-Path "$keyDir\DarkCipherWhisperDesk-UpdateSigning-ECDSAP256.dpapi") -and (Test-Path "$keyDir\DarkCipherWhisperDesk-UpdateSigning-MLDSA65.dpapi")) {
    & (Join-Path $PSScriptRoot 'tools\sign-update.ps1') -Path $updateJson
    & (Join-Path $PSScriptRoot 'tools\sign-update.ps1') -Path $modelsJson
} else {
    Write-Warning "update.json and models.json are NOT signed: this Windows account has no WhisperDesk release keys ($keyDir). Installed copies will ignore them until they are signed (tools\sign-update.ps1 -Path <file>); release.ps1 refuses to publish them unsigned."
}
Get-ChildItem $out -File | Where-Object { $_.Name -like "WhisperDesk-$version-*" } | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
Write-Host "WhisperDesk $version built in $out" -ForegroundColor Green
