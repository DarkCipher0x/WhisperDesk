# Publishes WhisperDesk's sanitised source to the GitHub repository https://github.com/DarkCipher0x/WhisperDesk
# (same pattern as DarkCipher ServerManagement's tools\publish-public.ps1).
#
# The public clone lives next to this project (..\..\WhisperDesk-public, or WD_PUBLIC_REPO). Each version becomes one commit
# "WhisperDesk <ver> - sanitised public source" and a tag v<ver>, authored as DarkCipher0x with the GitHub no-reply
# address. The public tree is replaced wholesale on every run, so edit the repository's own files (README, .github,
# .gitignore, the solution) in public-repo\ here, never on GitHub: an edit made there is overwritten by the next release.
#
# What is published: WhisperDesk, WhisperDesk.Addons and WhisperDesk.Tests, minus build output, IDE state, generated images
# (Packaging\Images and Packaging\StoreListing, which build.ps1 and make-store-art.ps1 redraw) and local-only files. The
# SDK contract DLL is rebuilt without embedded local paths.
#
# Gates (nothing is committed if one fails):
#   - no file may contain a term from tools\privacy-terms.local.txt (git-ignored, never published) or a local user path;
#   - every image must be listed by SHA-256 in the website's tools\approved-images.txt (a person has looked at it);
#   - no recordings, sessions, voiceprints, debug symbols, certificates or key files;
#   - the clone must be clean and on main, and the tag must not exist yet (a version is published once).
#
# Usage: .\tools\publish-public.ps1 [-NoPush]      then .\tools\publish-github-release.ps1 for the downloads
param([switch]$NoPush)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent                 # ...\WhisperDesk
$projects = Split-Path $root -Parent
$git = 'C:\Program Files\Git\cmd\git.exe'
if (-not (Test-Path $git)) { $git = 'git' }
$repo = if ($env:WD_PUBLIC_REPO) { $env:WD_PUBLIC_REPO } else { Join-Path $projects 'WhisperDesk-public' }
$approvedFile = Join-Path $projects 'DarkCipherSystems-Website\tools\approved-images.txt'
$termsFile = Join-Path $PSScriptRoot 'privacy-terms.local.txt'
$identity = @('-c', 'user.name=DarkCipher0x', '-c', 'user.email=DarkCipher0x@users.noreply.github.com')
[xml]$proj = Get-Content (Join-Path $root 'WhisperDesk.csproj')
$Version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

function Git { & $git -C $repo @args; if ($LASTEXITCODE) { throw "git $($args -join ' ') failed in $repo" } }

if (-not (Test-Path (Join-Path $repo '.git'))) { throw "no clone at $repo - clone https://github.com/DarkCipher0x/WhisperDesk.git there first" }
if (-not (Test-Path $termsFile)) { throw "tools\privacy-terms.local.txt is missing: the privacy scan can't run without it" }
if (-not (Test-Path $approvedFile)) { throw "the approved-images list is missing ($approvedFile)" }

# 1. The clone: clean, on main, up to date, and this version not yet published.
if (& $git -C $repo status --porcelain) { throw "the clone $repo has local changes - commit or discard them first" }
Git checkout -q main
if (-not $NoPush) { Git pull -q --ff-only origin main }
if (& $git -C $repo tag -l "v$Version") { throw "v$Version is already published in $repo" }

# 2. Stage the public tree in a private temporary folder.
$stage = Join-Path ([IO.Path]::GetTempPath()) ('wd-public-' + [guid]::NewGuid().ToString('N'))
$excludeDirs = '\\(bin|obj|dist|publish|test-build|public-repo|\.vs|\.idea|\.git|TestResults|node_modules)(\\|$)|^\\Packaging\\(Images|StoreListing)(\\|$)'
$excludeFiles = '\.(pdb|user|suo|pfx|snk|p12|pem|key|cer|log|msi|msix|zip)$|\.local\.txt$|^launchSettings\.json$'
function Copy-Tree([string]$from, [string]$to) {
    $base = (Resolve-Path $from).Path
    Get-ChildItem $base -Recurse -File -Force | ForEach-Object {
        $rel = $_.FullName.Substring($base.Length + 1)
        if ("\$rel" -match $excludeDirs -or $_.Name -match $excludeFiles) { return }
        $dest = Join-Path $to $rel
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item $_.FullName $dest
    }
}
try {
    Copy-Tree $root (Join-Path $stage 'WhisperDesk')
    Copy-Tree (Join-Path $projects 'WhisperDesk.Addons') (Join-Path $stage 'WhisperDesk.Addons')
    Copy-Tree (Join-Path $projects 'WhisperDesk.Tests') (Join-Path $stage 'WhisperDesk.Tests')
    Copy-Tree (Join-Path $root 'public-repo') $stage
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE')

    # The SDK contract DLL and its XML docs, rebuilt with no PDB and no local paths
    $build = Join-Path $stage '_sdkbuild'
    Copy-Tree (Join-Path $projects 'WhisperDesk.Addons') "$build\src"
    dotnet build "$build\src" -c Release -nologo -v q -o "$build\out" -p:DebugType=none -p:DebugSymbols=false `
        -p:Deterministic=true "-p:PathMap=$build\src=/_/" -p:ContinuousIntegrationBuild=true | Out-Null
    if ($LASTEXITCODE) { throw 'building the SDK contract failed' }
    Copy-Item "$build\out\WhisperDesk.Addons.dll", "$build\out\WhisperDesk.Addons.xml" (Join-Path $stage 'WhisperDesk\sdk') -Force
    Remove-Item $build -Recurse -Force

    # 3. Gates.
    $files = Get-ChildItem $stage -Recurse -File -Force
    $terms = Get-Content $termsFile -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') }
    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($stage.Length + 1)
        $bytes = [IO.File]::ReadAllBytes($f.FullName)
        $utf8 = [Text.Encoding]::UTF8.GetString($bytes)
        $utf16 = [Text.Encoding]::Unicode.GetString($bytes)
        foreach ($t in $terms) {
            if ($utf8.IndexOf($t, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $utf16.IndexOf($t, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $problems.Add("${rel}: a private term") }
        }
        if ($utf8 -match '(?i)[A-Z]:\\Users\\(?!Public\\|<|%)[A-Za-z0-9._-]+\\') { $problems.Add("${rel}: a local user path ($($Matches[0]))") }
        if ($f.Extension -match '^\.(png|jpe?g|gif|bmp|ico|svg|webp|tiff?)$') {
            $h = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            if (-not (Select-String -Path $approvedFile -Pattern "^$h" -Quiet)) { $problems.Add("${rel}: image nobody has reviewed (sha256 $h)") }
        }
        if ($f.Name -match '\.(wav|mp3|m4a|aac|flac|ogg|opus|wma|webm|mp4|mkv|mov|avi|wdsession|wdbackup|pdb|pfx|p12|snk|pem|key)$|^voices\.json$') { $problems.Add("${rel}: must never be published") }
    }
    if ($problems.Count) { throw "nothing was published:`n  " + (($problems | Select-Object -Unique) -join "`n  ") }

    # 4. Replace the public tree (everything but .git).
    Get-ChildItem $repo -Force | Where-Object { $_.Name -ne '.git' } | Remove-Item -Recurse -Force
    Get-ChildItem $stage -Force | Move-Item -Destination $repo
}
finally { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }

# 5. One commit and a tag per release.
Git add -A
if (& $git -C $repo status --porcelain) { Git @identity commit -q -m "WhisperDesk $Version - sanitised public source" }
else { Write-Host "The public tree already matches $Version; tagging only." }
Git @identity tag -a "v$Version" -m "WhisperDesk $Version"
$count = (& $git -C $repo ls-files | Measure-Object).Count

# 6. Push (skipped with -NoPush: the commit and tag wait in the clone).
if ($NoPush) { Write-Host "WhisperDesk $Version committed and tagged in $repo ($count files), not pushed (-NoPush)." -ForegroundColor Yellow; return }
Git push -q origin main
Git push -q origin "v$Version"
Write-Host "WhisperDesk $Version pushed to GitHub ($count files, tag v$Version)." -ForegroundColor Green
