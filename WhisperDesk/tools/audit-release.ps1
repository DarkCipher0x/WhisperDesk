# Privacy audit of one version's downloads in dist\ before anything is published (GitHub Release, website).
# Looks inside every file, not just at the outside:
#   - the MSI is unpacked with an administrative extract (msiexec /a: copies the files out, installs nothing);
#   - the portable zip and the add-on zips are expanded;
#   - WhisperDesk.exe is a compressed single-file bundle a scan can't read, so the app is also published unbundled
#     (PublishSingleFile=false) into a temporary folder and every DLL in it is scanned.
# Fails on: a term from tools\privacy-terms.local.txt (plain terms in text files, "binary:" terms in exe/dll/msi/cab, where
# short names give false hits), debug symbols, key files, databases, recordings, sessions or voiceprints.
#
# Usage: .\tools\audit-release.ps1 [-Version 1.8.1] [-Dist <folder>]      (release.ps1 runs it in its verify step)
param([string]$Version, [string]$Dist)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) { [xml]$p = Get-Content (Join-Path $root 'WhisperDesk.csproj'); $Version = $p.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$dist = if ($Dist) { $Dist } else { Join-Path $root "dist\$Version" }
$termsFile = Join-Path $PSScriptRoot 'privacy-terms.local.txt'
if (-not (Test-Path $termsFile)) { throw 'tools\privacy-terms.local.txt is missing: the audit cannot run without it' }
$lines = Get-Content $termsFile -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') }
$textTerms = @($lines | Where-Object { -not $_.StartsWith('binary:') })
$binaryTerms = @($lines | Where-Object { $_.StartsWith('binary:') } | ForEach-Object { $_.Substring(7) })
if (-not $binaryTerms) { throw 'tools\privacy-terms.local.txt has no binary: terms' }

$msi = Join-Path $dist "WhisperDesk-$Version-x64.msi"
$zips = @("WhisperDesk-$Version-x64-portable.zip", "WhisperDesk-$Version-Addon-SDK.zip", "WhisperDesk-$Version-Addon-WordExport.zip") | ForEach-Object { Join-Path $dist $_ }
foreach ($f in @($msi) + $zips + (Join-Path $dist "WhisperDesk-$Version-User-Admin-Guide.pdf")) { if (-not (Test-Path $f)) { throw "missing: $f" } }

$audit = Join-Path ([IO.Path]::GetTempPath()) ('wd-audit-' + [guid]::NewGuid().ToString('N'))
$problems = New-Object System.Collections.Generic.List[string]
try {
    New-Item -ItemType Directory -Force "$audit\msi" | Out-Null
    $proc = Start-Process msiexec.exe -ArgumentList '/a', "`"$msi`"", '/qn', "TARGETDIR=`"$audit\msi`"" -Wait -PassThru
    if ($proc.ExitCode) { $problems.Add("$(Split-Path $msi -Leaf): could not be unpacked for the audit (msiexec $($proc.ExitCode))") }
    Copy-Item $msi "$audit\msi"                       # the MSI's own tables and summary information too
    foreach ($z in $zips) { Expand-Archive $z (Join-Path $audit ([IO.Path]::GetFileNameWithoutExtension($z))) }
    Copy-Item (Join-Path $dist "WhisperDesk-$Version-User-Admin-Guide.pdf") $audit

    # This differently-shaped publish uses a throwaway lock file, so it never rewrites packages.lock.json (it once
    # rewrote it without Microsoft.NET.ILLink.Tasks, which the single-file build needs, and broke locked restore)
    $lockBefore = Get-FileHash (Join-Path $root 'packages.lock.json')
    dotnet publish (Join-Path $root 'WhisperDesk.csproj') -c Release -nologo -v q -o "$audit\unbundled" -p:PublishSingleFile=false "-p:NuGetLockFilePath=$audit\unbundled.lock.json" 2>&1 | Out-String -OutVariable pubLog | Out-Null
    if ((Get-FileHash (Join-Path $root 'packages.lock.json')).Hash -ne $lockBefore.Hash) { throw 'the audit publish changed packages.lock.json - restore it from git before going on' }
    if ($LASTEXITCODE) { throw "the unbundled publish for the audit failed:`n$((($pubLog -split "`n") | Where-Object { $_ -match 'error' } | Select-Object -First 8) -join "`n")" }

    $files = Get-ChildItem $audit -Recurse -File
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($audit.Length + 1)
        if ($f.Name -match '\.(pdb|pfx|p12|snk|pem|key|db|sqlite3?|db-wal|db-shm|wav|mp3|m4a|aac|flac|ogg|opus|webm|mp4|mkv|mov|wdsession|wdbackup)$|^voices\.json$') { $problems.Add("${rel}: must never be published"); continue }
        $terms = if ($f.Extension -match '^\.(exe|dll|msi|cab|bin)$') { $binaryTerms } else { $textTerms }
        $bytes = [IO.File]::ReadAllBytes($f.FullName)
        foreach ($t in [Text.Encoding]::UTF8.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes)) {
            foreach ($term in $terms) { if ($t.IndexOf($term, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $problems.Add("${rel}: a private term") } }
        }
    }
    if ($problems.Count) { throw "WhisperDesk $Version failed the privacy audit - nothing was published:`n  " + (($problems | Select-Object -Unique) -join "`n  ") }
    Write-Host ("Privacy audit passed: {0} files inside the {1} downloads and the unbundled app." -f $files.Count, $Version) -ForegroundColor Green
}
finally { Remove-Item $audit -Recurse -Force -ErrorAction SilentlyContinue }
