# The ten release steps (dot-sourced by release.ps1 after common.ps1). See docs\RELEASE-PIPELINE.md.
# Each step takes the run context $c and returns @{ status = 'done' | 'dry-run' | 'blocked' | 'waiting'; data = @{...}; note = '' }
# or throws. A step must be safe to run again: it detects work already done and skips it.
# Windows PowerShell 5.1, ASCII only.

$script:StepOrder = 'preflight', 'build', 'verify', 'sanitise', 'scan', 'mirror', 'release', 'website', 'verify-website', 'discord'
$script:PublicSteps = 'mirror', 'release', 'website', 'discord'
$script:LocalSteps = 'preflight', 'build', 'verify', 'sanitise', 'scan'

# The signed update information for the in-app updater (build.ps1 writes it; Services\Updater.cs reads it). Uploaded to
# the GitHub Release with the downloads, but not a website download and not in SHA256SUMS (its signatures cover it).
$script:UpdateAssetNames = 'update.json', 'update.json.sig', 'update.json.pq.sig', 'models.json', 'models.json.sig', 'models.json.pq.sig'

function Get-AssetNames([string]$v) {
    "WhisperDesk-$v-x64.msi", "WhisperDesk-$v-x64-portable.zip", "WhisperDesk-$v-User-Admin-Guide.pdf",
    "WhisperDesk-$v-Addon-SDK.zip", "WhisperDesk-$v-Addon-WordExport.zip", "WhisperDesk-$v-SHA256SUMS.txt"
}
$script:AssetLabels = @{
    'x64.msi' = @('Windows installer (.msi)', 'installer'); 'x64-portable.zip' = @('Portable (.zip)', 'portable')
    'User-Admin-Guide.pdf' = @('User & admin guide (.pdf)', 'guide'); 'Addon-SDK.zip' = @('Add-on SDK for developers (.zip)', 'sdk')
    'Addon-WordExport.zip' = @('Word export add-on (.zip)', 'addon'); 'SHA256SUMS.txt' = @('SHA-256 checksums (.txt)', 'checksums')
}
function Get-AssetLabel([string]$name, [string]$v) { $AssetLabels[$name.Substring("WhisperDesk-$v-".Length)] }

function Get-ChangelogSection([string]$v) {
    $text = [IO.File]::ReadAllText((Join-Path $Root 'CHANGELOG.md'), [Text.Encoding]::UTF8)
    $m = [regex]::Match($text, "(?ms)^## $([regex]::Escape($v))\s*\r?\n(.*?)(?=^## |\z)")
    if (-not $m.Success) { return $null }
    $m.Groups[1].Value.Trim()
}

function Get-ReleaseNotes([string]$v) {
    (Get-ChangelogSection $v) + "`n`n## Downloads`n`n| File | What it is |`n|---|---|`n" +
    "| ``WhisperDesk-$v-x64.msi`` | Installer (upgrades older versions in place) |`n" +
    "| ``WhisperDesk-$v-x64-portable.zip`` | Unzip and run, no install needed |`n" +
    "| ``WhisperDesk-$v-User-Admin-Guide.pdf`` | The user and administrator guide |`n" +
    "| ``WhisperDesk-$v-Addon-SDK.zip`` | Add-on SDK: contract DLL, XML docs, sample |`n" +
    "| ``WhisperDesk-$v-Addon-WordExport.zip`` | The Word export add-on, ready to install |`n" +
    "| ``WhisperDesk-$v-SHA256SUMS.txt`` | SHA-256 of every file above |`n`n" +
    "Windows 10/11 x64. Reading most audio/video formats needs FFmpeg (``winget install Gyan.FFmpeg``). The MSI and zip " +
    "aren't code-signed yet, so SmartScreen may show ""unknown publisher"": check the file against the SHA256SUMS file " +
    "(``Get-FileHash .\WhisperDesk-$v-x64.msi``). The Microsoft Store edition is the same program, signed by Microsoft."
}

# Hash of a whole tree: SHA-256 over the sorted "relative/path  sha256" lines (paths with forward slashes)
function Get-TreeHash([string]$dir) {
    $base = (Resolve-Path $dir).Path
    $lines = Get-ChildItem $base -Recurse -File -Force | ForEach-Object { '{0}  {1}' -f $_.FullName.Substring($base.Length + 1).Replace('\', '/'), (Get-Sha256 $_.FullName) } | Sort-Object
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    (($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join '')
}

# SHA-256 of every entry of a zip/MSIX, keyed by its path ('/' separators, %-escapes decoded)
function Get-ZipEntryHashes([string]$zip, [string[]]$skip = @()) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead($zip)
    $h = @{}
    try {
        foreach ($e in $z.Entries) {
            if ($e.FullName.EndsWith('/') -or $e.FullName.EndsWith('\')) { continue }
            $name = [Uri]::UnescapeDataString($e.FullName.Replace('\', '/'))
            if ($skip | Where-Object { $name -like $_ }) { continue }
            $s = $e.Open(); try { $sha = [Security.Cryptography.SHA256]::Create(); $h[$name] = (($sha.ComputeHash($s) | ForEach-Object { $_.ToString('x2') }) -join '') } finally { $s.Dispose() }
        }
    } finally { $z.Dispose() }
    $h
}

# Files in $zip (under $prefix) vs $folder: names must be the same set, and each file the same bytes or the same bytes
# apart from carriage returns (line endings). Returns the paths that differ; empty means they match.
function Compare-ZipToFolder([string]$zip, [string]$folder, [string]$prefix) {
    if (-not ('ReleaseEol' -as [type])) {
        Add-Type -TypeDefinition 'public static class ReleaseEol { public static bool Same(byte[] a, byte[] b) { int i = 0, j = 0; while (true) { while (i < a.Length && a[i] == 13) i++; while (j < b.Length && b[j] == 13) j++; if (i == a.Length || j == b.Length) return i == a.Length && j == b.Length; if (a[i++] != b[j++]) return false; } } }'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $base = (Resolve-Path $folder).Path
    $files = @{}; Get-ChildItem $base -Recurse -File -Force | ForEach-Object { $files[$_.FullName.Substring($base.Length + 1).Replace('\', '/')] = $_.FullName }
    $diff = [System.Collections.Generic.List[string]]::new(); $seen = @{}
    $z = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($e in $z.Entries) {
            if ($e.FullName.EndsWith('/')) { continue }
            $p = $e.FullName.Replace([char]92, [char]47).Substring($prefix.Length); $seen[$p] = $true   # Windows PowerShell zips use backslashes
            if (-not $files.ContainsKey($p)) { $diff.Add("$p (only in the zip)"); continue }
            $ms = [IO.MemoryStream]::new(); $s = $e.Open(); try { $s.CopyTo($ms) } finally { $s.Dispose() }
            if (-not [ReleaseEol]::Same($ms.ToArray(), [IO.File]::ReadAllBytes($files[$p]))) { $diff.Add($p) }
        }
    } finally { $z.Dispose() }
    foreach ($p in $files.Keys) { if (-not $seen.ContainsKey($p)) { $diff.Add("$p (missing from the zip)") } }
    $diff
}

# ======================================== 0. preflight ===========================================================
function Step-Preflight($c) {
    $v = $c.Version; $warn = New-Object Collections.Generic.List[string]; $fail = New-Object Collections.Generic.List[string]
    $block = Get-PublishingBlock
    if ($block) { Say "  Kill switch: publishing is OFF ($block). Public steps will be recorded as blocked." Yellow } else { Say '  Kill switch: publishing is on.' }

    if (-not (Get-ChangelogSection $v)) { $fail.Add("CHANGELOG.md has no '## $v' section (it becomes the release notes)") }
    # Version written by hand anywhere it should come from <Version>
    foreach ($f in 'README.md', 'public-repo\README.md') {
        $t = [IO.File]::ReadAllText((Join-Path $Root $f))
        if ($t -match '(?i)version-\d+\.\d+\.\d+|\bv\d+\.\d+\.\d+ \S') { $fail.Add("$f has a hard-coded version ($($Matches[0])); use `$VERSION`$ (filled in from WhisperDesk.csproj)") }
    }
    if ([IO.File]::ReadAllText((Join-Path $Root 'Packaging\AppxManifest.xml')) -notmatch 'Version="\$VERSION\$"') { $fail.Add('Packaging\AppxManifest.xml must take its version from $VERSION$') }

    # Already released?
    $published = $null
    try { $r = Get-GhRelease "v$v"; if ($r -and -not $r.draft) { $published = $r } } catch { $fail.Add("GitHub API: $($_.Exception.Message)") }
    if ($published -and (Get-StepStatus $c.State 'release') -ne 'done') {
        $msg = "WhisperDesk $v is already released on GitHub ($($published.html_url)). Bump <Version> (bug fix x.y.Z+1, feature x.Y+1.0) and add a CHANGELOG section."
        if ($c.DryRun) { $warn.Add("$msg (dry run continues)") } else { $fail.Add($msg) }
    }

    # The public clone and its remote
    try { Assert-Origin } catch { $fail.Add($_.Exception.Message) }
    if (Test-Path (Join-Path $PublicClone '.git')) {
        if (& $Git -C $PublicClone status --porcelain) { $fail.Add("the public clone $PublicClone has local changes") }
        $br = (& $Git -C $PublicClone rev-parse --abbrev-ref HEAD).Trim()
        if ($br -ne 'main') { $fail.Add("the public clone is on '$br', not main") }
    }

    # The private sources: committed, so the release records exactly what it was built from
    $heads = [ordered]@{}
    foreach ($d in 'WhisperDesk', 'WhisperDesk.Addons', 'WhisperDesk.Tests') {
        $dir = Join-Path $Projects $d
        if (-not (Test-Path (Join-Path $dir '.git'))) { $fail.Add("$d is not a git repository"); continue }
        $heads[$d] = (& $Git -C $dir rev-parse HEAD).Trim()
        if (& $Git -C $dir status --porcelain) {
            $msg = "$d has uncommitted changes: commit them so the release records what it was built from"
            if ($c.DryRun) { $warn.Add($msg) } else { $fail.Add($msg) }
        }
    }

    # Tools and local-only inputs
    if (-not (Test-Path (Join-Path $Root 'tools\privacy-terms.local.txt'))) { $fail.Add('tools\privacy-terms.local.txt is missing (the privacy scans need it)') }
    $approved = Join-Path $Projects 'DarkCipherSystems-Website\tools\approved-images.txt'
    if (-not (Test-Path $approved)) { $fail.Add("the approved-images list is missing ($approved)") }
    $pin = @{}; Get-Content (Join-Path $PSScriptRoot 'gitleaks-pin.txt') | Where-Object { $_ -match '^(\w+)=(.+)$' } | ForEach-Object { $pin[$Matches[1]] = $Matches[2].Trim() }
    $gl = Join-Path $Root 'tools\bin\gitleaks.exe'
    if (-not (Test-Path $gl)) { $fail.Add("gitleaks $($pin.version) is missing: download $($pin.zip) from $($pin.source), check its SHA-256 is $($pin.zip_sha256), and put gitleaks.exe in tools\bin\") }
    elseif ((Get-Sha256 $gl) -ne $pin.exe_sha256) { $fail.Add("tools\bin\gitleaks.exe doesn't match the pinned SHA-256 in tools\release\gitleaks-pin.txt") }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $fail.Add('the .NET SDK (dotnet) is not on PATH') }
    if (-not (Test-Path (Join-Path $Root "release\discord-$v.json"))) { $warn.Add("no release\discord-$v.json: the Discord card will be generated from the CHANGELOG") }

    foreach ($w in $warn) { Say "  warning: $w" Yellow }
    if ($fail.Count) { throw "preflight failed:`n  " + ($fail -join "`n  ") }
    @{ status = 'done'; data = @{ heads = $heads; published = [bool]$published }; note = "$($warn.Count) warning(s)" }
}

# ======================================== 1. build ===============================================================
function Step-Build($c) {
    $prev = Get-StepData $c.State 'build'
    if ((Get-StepStatus $c.State 'build') -eq 'done' -and $prev -and (Test-Path $c.Out)) {
        $same = $true
        foreach ($p in $prev.files.PSObject.Properties) { $f = Join-Path $c.Out $p.Name; if (-not (Test-Path $f) -or (Get-Sha256 $f) -ne $p.Value) { $same = $false } }
        if ($same) { Say '  already built, files unchanged'; return @{ status = 'done'; data = $prev; note = 'reused' } }
    }
    $buildArgs = @{ OutDir = $c.Out }
    if ($c.DryRun -or (Test-Path $c.Out)) { $buildArgs.Overwrite = $true }
    & (Join-Path $Root 'build.ps1') @buildArgs
    $files = [ordered]@{}
    foreach ($n in (Get-AssetNames $c.Version) + $UpdateAssetNames + "store\WhisperDesk-$($c.Version)-x64.msix") { $f = Join-Path $c.Out $n; if (Test-Path $f) { $files[$n] = Get-Sha256 $f } }
    @{ status = 'done'; data = @{ files = $files } }
}

# ======================================== 2. verify ==============================================================
function Step-Verify($c) {
    $v = $c.Version; $o = $c.Out; $fail = New-Object Collections.Generic.List[string]
    $names = Get-AssetNames $v
    foreach ($n in $names + $UpdateAssetNames + "store\WhisperDesk-$v-x64.msix") { if (-not (Test-Path (Join-Path $o $n))) { $fail.Add("missing $n$(if ($n -like '*.json*') { " (sign it: tools\sign-update.ps1 -Path dist\<ver>\$($n -replace '\.(pq\.)?sig$', ''))" })") } }
    if ($fail.Count) { throw "verify failed:`n  " + ($fail -join "`n  ") }

    # Checksums: exactly the five downloads, each matching, and not the checksum file itself
    $sums = @{}
    Get-Content (Join-Path $o "WhisperDesk-$v-SHA256SUMS.txt") | Where-Object { $_ -match '^([0-9a-f]{64})\s+\*?(.+)$' } | ForEach-Object { $sums[$Matches[2].Trim()] = $Matches[1] }
    foreach ($n in $names | Where-Object { $_ -notlike '*SHA256SUMS*' }) {
        if (-not $sums.ContainsKey($n)) { $fail.Add("SHA256SUMS doesn't list $n") }
        elseif ($sums[$n] -ne (Get-Sha256 (Join-Path $o $n))) { $fail.Add("SHA256SUMS doesn't match $n") }
    }
    foreach ($k in $sums.Keys) { if ($names -notcontains $k -or $k -like '*SHA256SUMS*') { $fail.Add("SHA256SUMS lists an unexpected file: $k") } }

    # Store parity: the MSIX's program files are the portable zip's, byte for byte
    $msixSkip = 'AppxManifest.xml', 'AppxBlockMap.xml', '[[]Content_Types].xml', 'AppxSignature.p7x', 'resources.pri', 'Images/*', 'AppxMetadata/*'
    $m = Get-ZipEntryHashes (Join-Path $o "store\WhisperDesk-$v-x64.msix") $msixSkip
    $z = Get-ZipEntryHashes (Join-Path $o "WhisperDesk-$v-x64-portable.zip")
    # Left out of the MSIX on purpose (build.ps1): the AnyCPU add-on reference DLL the App Certification Kit flags
    $notInStore = 'sdk/WhisperDesk.Addons.dll', 'sdk/WhisperDesk.Addons.xml'
    foreach ($k in $z.Keys) { if ($notInStore -contains $k) { if ($m.ContainsKey($k)) { $fail.Add("Store parity: $k should not be in the MSIX") }; continue }
        if (-not $m.ContainsKey($k)) { $fail.Add("Store parity: $k is in the zip but not the MSIX") } elseif ($m[$k] -ne $z[$k]) { $fail.Add("Store parity: $k differs between the zip and the MSIX") } }
    foreach ($k in $m.Keys) { if (-not $z.ContainsKey($k)) { $fail.Add("Store parity: $k is in the MSIX but not the zip") } }

    # The version inside the program and the package
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('wd-ver-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $tmp | Out-Null
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zz = [IO.Compression.ZipFile]::OpenRead((Join-Path $o "WhisperDesk-$v-x64-portable.zip"))
        try { $e = $zz.Entries | Where-Object { $_.Name -eq 'WhisperDesk.exe' } | Select-Object -First 1; [IO.Compression.ZipFileExtensions]::ExtractToFile($e, "$tmp\WhisperDesk.exe") } finally { $zz.Dispose() }
        $pv = (Get-Item "$tmp\WhisperDesk.exe").VersionInfo.ProductVersion
        if ($pv -notlike "$v*") { $fail.Add("WhisperDesk.exe says version '$pv', expected $v") }
        $mz = [IO.Compression.ZipFile]::OpenRead((Join-Path $o "store\WhisperDesk-$v-x64.msix"))
        try { $e = $mz.GetEntry('AppxManifest.xml'); $r = New-Object IO.StreamReader($e.Open()); $man = $r.ReadToEnd(); $r.Dispose() } finally { $mz.Dispose() }
        if ($man -notmatch "Version=""$([regex]::Escape($v)).0""") { $fail.Add("the MSIX manifest isn't version $v.0") }
    } finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
    if ($fail.Count) { throw "verify failed:`n  " + ($fail -join "`n  ") }

    # The update information: both signatures verify against the pinned keys, and it describes exactly this MSI
    & (Join-Path $Root 'tools\sign-update.ps1') -Verify (Join-Path $o 'update.json')
    # The model list: both signatures verify, and it is byte-identical to models\catalog.json (the list built into this version)
    & (Join-Path $Root 'tools\sign-update.ps1') -Verify (Join-Path $o 'models.json')
    if ((Get-Sha256 (Join-Path $o 'models.json')) -ne (Get-Sha256 (Join-Path $Root 'models\catalog.json'))) { $fail.Add("models.json isn't byte-identical to models\catalog.json (rebuild)") }
    $uj = [IO.File]::ReadAllText((Join-Path $o 'update.json')) | ConvertFrom-Json
    $um = @($uj.packages | Where-Object { $_.kind -eq 'msi' -and $_.arch -eq 'x64' })
    $msiPath = Join-Path $o "WhisperDesk-$v-x64.msi"
    if ($uj.product -ne 'WhisperDesk' -or $uj.version -ne $v -or $um.Count -ne 1 -or $um[0].file -ne "WhisperDesk-$v-x64.msi" -or $um[0].url -or
        [long]$um[0].bytes -ne (Get-Item $msiPath).Length -or $um[0].sha256 -ne (Get-Sha256 $msiPath)) { $fail.Add("update.json doesn't describe WhisperDesk-$v-x64.msi exactly") }
    if ($fail.Count) { throw "verify failed:`n  " + ($fail -join "`n  ") }

    # Privacy audit inside the binaries (MSI admin-extract, zips, unbundled app)
    & (Join-Path $Root 'tools\audit-release.ps1') -Version $v -Dist $o
    @{ status = 'done'; data = @{ storeParityFiles = $z.Count; productVersion = $pv } }
}

# ======================================== 3. sanitise ============================================================
function Step-Sanitise($c) {
    $stage = Join-Path $c.RelDir 'public-stage'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null
    $allow = @(Read-Globs (Join-Path $PSScriptRoot 'public-allowlist.txt'))
    $private = @(Read-Globs (Join-Path $PSScriptRoot 'public-private.txt'))
    $hardDeny = '(^|/)(bin|obj|dist|publish|test-build|\.vs|\.idea|\.git|TestResults|node_modules)(/|$)|\.(pdb|pfx|p12|snk|pem|key|cer|user|suo|log|msi|msix|wav|mp3|m4a|flac|ogg|wdsession|wdbackup)$|\.local\.txt$|(^|/)voices\.json$|(^|/)tools/bin/'
    $sources = @(
        @{ from = $Root; to = 'WhisperDesk' },
        @{ from = (Join-Path $Projects 'WhisperDesk.Addons'); to = 'WhisperDesk.Addons' },
        @{ from = (Join-Path $Projects 'WhisperDesk.Tests'); to = 'WhisperDesk.Tests' },
        @{ from = (Join-Path $Root 'public-repo'); to = '' }
    )
    $copied = 0; $unlisted = New-Object Collections.Generic.List[string]; $codeUnlisted = New-Object Collections.Generic.List[string]
    foreach ($s in $sources) {
        $base = (Resolve-Path $s.from).Path
        foreach ($f in Get-ChildItem $base -Recurse -File -Force) {
            $rel = $f.FullName.Substring($base.Length + 1).Replace('\', '/')
            $pub = if ($s.to) { "$($s.to)/$rel" } else { $rel }
            if ($s.to -eq 'WhisperDesk' -and $rel -like 'public-repo/*') { continue }   # published at the root instead
            if ($pub -match $hardDeny) { continue }
            if ($allow | Where-Object { $_.IsMatch($pub) }) {
                $dest = Join-Path $stage $pub
                New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
                Copy-Item $f.FullName $dest; $copied++
            } elseif (-not ($private | Where-Object { $_.IsMatch($pub) })) {
                if ($pub -match '\.(cs|xaml|csproj|sln|ps1|py|wxs|xml|json|props|targets|manifest)$') { $codeUnlisted.Add($pub) } else { $unlisted.Add($pub) }
            }
        }
    }
    foreach ($u in $unlisted) { Say "  not published (not in the allowlist): $u" Yellow }
    if ($codeUnlisted.Count) { throw "code files in neither tools\release\public-allowlist.txt nor public-private.txt (add each to one of them):`n  " + ($codeUnlisted -join "`n  ") }
    Copy-Item (Join-Path $Root 'LICENSE') (Join-Path $stage 'LICENSE') -Force

    # $VERSION$ in Markdown comes from WhisperDesk.csproj
    foreach ($md in Get-ChildItem $stage -Recurse -File -Filter *.md) {
        $t = [IO.File]::ReadAllText($md.FullName)
        if ($t.Contains('$VERSION$')) { Write-Utf8 $md.FullName $t.Replace('$VERSION$', $c.Version) }
    }

    # The SDK contract DLL and its XML docs, rebuilt with no PDB and no local paths
    $b = Join-Path ([IO.Path]::GetTempPath()) ('wd-sdk-' + [guid]::NewGuid().ToString('N'))
    try {
        Copy-Item (Join-Path $Projects 'WhisperDesk.Addons') "$b\src" -Recurse
        Get-ChildItem "$b\src" -Directory | Where-Object Name -in 'bin', 'obj', '.git' | Remove-Item -Recurse -Force
        dotnet build "$b\src" -c Release -nologo -v q -o "$b\out" -p:DebugType=none -p:DebugSymbols=false -p:Deterministic=true "-p:PathMap=$b\src=/_/" -p:ContinuousIntegrationBuild=true | Out-Null
        if ($LASTEXITCODE) { throw 'building the SDK contract failed' }
        Copy-Item "$b\out\WhisperDesk.Addons.dll", "$b\out\WhisperDesk.Addons.xml" (Join-Path $stage 'WhisperDesk\sdk') -Force
    } finally { Remove-Item $b -Recurse -Force -ErrorAction SilentlyContinue }

    # The public tree must build on its own (locked restore), in a throwaway copy
    $t = Join-Path ([IO.Path]::GetTempPath()) ('wd-pubbuild-' + [guid]::NewGuid().ToString('N'))
    try {
        Copy-Item $stage $t -Recurse
        dotnet restore "$t\WhisperDesk.sln" --locked-mode -v q | Out-Null
        if ($LASTEXITCODE) { throw 'the public tree does not restore in locked mode' }
        dotnet build "$t\WhisperDesk.sln" -c Release -nologo -v q --no-restore | Out-Null
        if ($LASTEXITCODE) { throw 'the public tree does not build' }
    } finally { Remove-Item $t -Recurse -Force -ErrorAction SilentlyContinue }

    $hash = Get-TreeHash $stage
    Say "  $copied files staged, public tree builds, tree $($hash.Substring(0, 12))"
    @{ status = 'done'; data = @{ stage = $stage; files = $copied; tree = $hash; notPublished = @($unlisted) } }
}

# ======================================== 4. scan ================================================================
function Invoke-SecretScan([string]$dir, [string]$report) {
    $gl = Join-Path $Root 'tools\bin\gitleaks.exe'
    # Its own process: gitleaks logs to stderr, which Windows PowerShell would turn into a terminating error
    $log = "$report.log"
    $p = Start-Process -FilePath $gl -NoNewWindow -Wait -PassThru -RedirectStandardError $log -RedirectStandardOutput "$log.out" -ArgumentList @(
        'dir', "`"$dir`"", '--config', "`"$(Join-Path $PSScriptRoot 'gitleaks.toml')`"", '--no-banner', '--redact', '--log-level', 'warn',
        '--exit-code', '7', '--report-format', 'json', '--report-path', "`"$report`"")
    $code = $p.ExitCode
    if ($code -eq 7) {
        $hits = Get-Content $report -Raw | ConvertFrom-Json
        throw "gitleaks found $(@($hits).Count) secret(s) - nothing was published:`n  " + ((@($hits) | ForEach-Object { "$($_.File):$($_.StartLine) $($_.RuleID)" }) -join "`n  ")
    }
    if ($code) { throw "gitleaks failed to run (exit $code): $((Get-Content $log -Raw -ErrorAction SilentlyContinue))" }
    if (-not (Test-Path $report)) { throw 'gitleaks wrote no report: the scan did not run' }
}
function Invoke-PrivacyScan([string]$dir) {
    $terms = Get-Content (Join-Path $Root 'tools\privacy-terms.local.txt') -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') -and -not $_.StartsWith('binary:') }
    $approvedFile = Join-Path $Projects 'DarkCipherSystems-Website\tools\approved-images.txt'
    $approved = @{}; Get-Content $approvedFile | Where-Object { $_ -match '^[0-9a-f]{64}' } | ForEach-Object { $approved[$_.Substring(0, 64)] = $true }
    $problems = New-Object Collections.Generic.List[string]
    $base = (Resolve-Path $dir).Path
    foreach ($f in Get-ChildItem $base -Recurse -File -Force) {
        $rel = $f.FullName.Substring($base.Length + 1)
        $bytes = [IO.File]::ReadAllBytes($f.FullName)
        $u8 = [Text.Encoding]::UTF8.GetString($bytes); $u16 = [Text.Encoding]::Unicode.GetString($bytes)
        foreach ($t in $terms) { if ($u8.IndexOf($t, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $u16.IndexOf($t, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $problems.Add("${rel}: a private term") } }
        if ($u8 -match '(?i)[A-Z]:\\Users\\(?!Public\\|<|%)[A-Za-z0-9._-]+\\') { $problems.Add("${rel}: a local user path") }
        if ($f.Extension -match '^\.(png|jpe?g|gif|bmp|ico|svg|webp|tiff?)$' -and -not $approved.ContainsKey((Get-Sha256 $f.FullName))) { $problems.Add("${rel}: an image nobody has reviewed") }
        if ($f.Name -match '\.(wav|mp3|m4a|aac|flac|ogg|opus|webm|mp4|mkv|mov|wdsession|wdbackup|pdb|pfx|p12|snk|pem|key)$|^voices\.json$') { $problems.Add("${rel}: must never be published") }
    }
    if ($problems.Count) { throw "privacy scan - nothing was published:`n  " + (($problems | Select-Object -Unique) -join "`n  ") }
}
function Step-Scan($c) {
    $san = Get-StepData $c.State 'sanitise'
    $stage = $san.stage
    $now = Get-TreeHash $stage
    if ($now -ne $san.tree) { throw "the stage changed after it was sanitised ($($san.tree.Substring(0,12)) -> $($now.Substring(0,12))): run sanitise again" }
    $prev = Get-StepData $c.State 'scan'
    if ((Get-StepStatus $c.State 'scan') -eq 'done' -and $prev.tree -eq $now) { Say '  this exact tree was already scanned clean'; return @{ status = 'done'; data = $prev; note = 'reused' } }
    Invoke-SecretScan $stage (Join-Path $c.RelDir 'gitleaks-stage.json')
    Invoke-PrivacyScan $stage
    Say '  gitleaks and the privacy scan are clean'
    @{ status = 'done'; data = @{ tree = $now } }
}

# ======================================== 5. mirror (public) =====================================================
function Step-Mirror($c) {
    $v = $c.Version; $tag = "v$v"
    $stage = (Get-StepData $c.State 'sanitise').stage
    if ((Get-TreeHash $stage) -ne (Get-StepData $c.State 'scan').tree) { throw 'the stage is not the tree that was scanned' }
    Assert-Origin
    # .gitattributes decides line endings in the repository; no per-file CRLF conversion warnings in the clone
    & $Git -C $PublicClone config core.autocrlf false; & $Git -C $PublicClone config core.safecrlf false
    if (& $Git -C $PublicClone status --porcelain) { throw "the public clone has local changes" }
    if (-not $c.DryRun) { Invoke-Git $PublicClone fetch -q --tags origin | Out-Null; Invoke-Git $PublicClone checkout -q main | Out-Null; Invoke-Git $PublicClone pull -q --ff-only origin main | Out-Null }

    # Put the stage into the clone (everything but .git) and see what it changes
    Get-ChildItem $PublicClone -Force | Where-Object { $_.Name -ne '.git' } | Remove-Item -Recurse -Force
    Get-ChildItem $stage -Force | Copy-Item -Destination $PublicClone -Recurse
    Invoke-Git $PublicClone add -A | Out-Null
    $tagged = [bool](& $Git -C $PublicClone tag -l $tag)
    try {
        if ($tagged) {
            & $Git -C $PublicClone diff --cached --quiet $tag
            if ($LASTEXITCODE) {
                $msg = "$tag is already published with different source. A published version is never changed: bump the version."
                if ($c.DryRun) { Say "  (dry run) a real run would STOP here: $msg" Yellow; return @{ status = 'dry-run'; note = 'would stop: tag exists with different source' } }
                throw $msg
            }
            $remoteTag = & $Git -C $PublicClone ls-remote --tags origin "refs/tags/$tag"
            if ($c.DryRun) { return @{ status = 'dry-run'; note = "$tag already published and identical" } }
            Invoke-Git $PublicClone reset -q --hard | Out-Null
            if (-not $remoteTag) { Invoke-Git $PublicClone push -q origin main | Out-Null; Invoke-Git $PublicClone push -q origin $tag | Out-Null }
            return @{ status = 'done'; data = @{ tag = $tag; commit = (& $Git -C $PublicClone rev-list -n 1 $tag).Trim() }; note = 'already published, identical' }
        }
        $stat = & $Git -C $PublicClone diff --cached --shortstat
        if ($c.DryRun) { Say "  would commit 'WhisperDesk $v - sanitised public source' ($stat), tag $tag, push to $ExpectedOrigin" Cyan; return @{ status = 'dry-run'; note = "$stat" } }
        $id = @('-c', 'user.name=DarkCipher0x', '-c', 'user.email=DarkCipher0x@users.noreply.github.com')
        if (& $Git -C $PublicClone status --porcelain) { Invoke-Git $PublicClone @id commit -q -m "WhisperDesk $v - sanitised public source" | Out-Null }
        Invoke-Git $PublicClone @id tag -a $tag -m "WhisperDesk $v" | Out-Null
        Assert-Origin
        Invoke-Git $PublicClone push -q origin main | Out-Null
        Invoke-Git $PublicClone push -q origin $tag | Out-Null
        $commit = (& $Git -C $PublicClone rev-list -n 1 $tag).Trim()
        Say "  pushed $tag ($($commit.Substring(0, 10)), $stat)" Green
        @{ status = 'done'; data = @{ tag = $tag; commit = $commit } }
    }
    finally {
        # Back to exactly the committed state (a dry run or a failure leaves nothing staged in the clone)
        & $Git -C $PublicClone reset -q --hard; & $Git -C $PublicClone clean -q -fdx
    }
}

# ======================================== 6. GitHub Release (public) =============================================
function Step-Release($c) {
    $v = $c.Version; $tag = "v$v"; $o = $c.Out
    $assets = (Get-AssetNames $v) + $UpdateAssetNames | ForEach-Object { Join-Path $o $_ }
    $local = @{}; foreach ($a in $assets) { $local[(Split-Path $a -Leaf)] = Get-Sha256 $a }
    $rel = Get-GhRelease $tag
    if ($c.DryRun) {
        $what = if (-not $rel) { "create draft release '$tag', upload $($assets.Count) files, verify GitHub's SHA-256 of each, then publish" } elseif ($rel.draft) { 'resume the draft release' } else { 'nothing: the release is published (never modified)' }
        Say "  would $what" Cyan
        return @{ status = 'dry-run'; note = $what }
    }
    if ($rel -and -not $rel.draft) {
        $bad = @($rel.assets | Where-Object { $local.ContainsKey($_.name) -and $_.digest -ne "sha256:$($local[$_.name])" })
        $missing = @($local.Keys | Where-Object { ($rel.assets | ForEach-Object { $_.name }) -notcontains $_ })
        if ($bad -or $missing) { throw "release $tag is published but doesn't match dist (published releases are never changed): mismatched $($bad.name -join ', ') missing $($missing -join ', ')" }
        return @{ status = 'done'; data = @{ url = $rel.html_url; id = $rel.id }; note = 'already published, digests match' }
    }
    if (-not (& $Git -C $PublicClone ls-remote --tags origin "refs/tags/$tag")) { throw "tag $tag is not on GitHub: run the mirror step first" }

    Invoke-GitHub PATCH $Api @{ description = 'Private, on-device transcription for Windows: files and live audio, speaker identification, an audited transcript library. GPU (Vulkan), NPU or CPU; no cloud, no telemetry.'
                                homepage = 'https://darkciphersystems.com/software.html#whisperdesk' } | Out-Null
    Invoke-GitHub PUT "$Api/topics" @{ names = @('transcription', 'speech-to-text', 'whisper', 'whisper-cpp', 'speaker-diarization', 'windows', 'wpf', 'dotnet', 'privacy', 'offline', 'vulkan', 'npu') } | Out-Null

    $body = @{ tag_name = $tag; name = "WhisperDesk $v"; body = (Get-ReleaseNotes $v); draft = $true; prerelease = $false }
    if ($rel) { $rel = Invoke-GitHub PATCH "$Api/releases/$($rel.id)" $body } else { $rel = Invoke-GitHub POST "$Api/releases" $body }
    $existing = @(Invoke-GitHub GET "$Api/releases/$($rel.id)/assets?per_page=100")
    foreach ($a in $assets) {
        $leaf = Split-Path $a -Leaf
        $old = $existing | Where-Object { $_.name -eq $leaf }
        if ($old -and $old.digest -eq "sha256:$($local[$leaf])") { Say "  $leaf already uploaded"; continue }
        if ($old) { Invoke-GitHub DELETE "$Api/releases/assets/$($old.id)" | Out-Null }
        $type = switch -Regex ($leaf) { '\.msi$' { 'application/x-msi' } '\.zip$' { 'application/zip' } '\.pdf$' { 'application/pdf' } '\.json$' { 'application/json' } default { 'text/plain' } }
        Say ("  uploading {0} ({1:N1} MB)" -f $leaf, ((Get-Item $a).Length / 1MB))
        Invoke-RestMethod -Method POST -Uri "https://uploads.github.com/repos/$Owner/$RepoName/releases/$($rel.id)/assets?name=$([uri]::EscapeDataString($leaf))" -Headers (Get-GhHeaders) -ContentType $type -InFile $a -TimeoutSec 3600 | Out-Null
    }
    # GitHub's own SHA-256 of every upload must match before the release goes public
    # GitHub fills in an asset's digest a little after the upload, so an empty digest is waited for (up to 3 min); a
    # digest that's there but different fails at once.
    for ($try = 0; ; $try++) {
        $up = @(Invoke-GitHub GET "$Api/releases/$($rel.id)/assets?per_page=100")
        $pending = @($up | Where-Object { $local.ContainsKey($_.name) -and -not $_.digest })
        if (-not $pending -or $try -ge 18) { break }
        if ($try -eq 0) { Say "  waiting for GitHub to compute the SHA-256 of $($pending.Count) upload(s)…" }
        Start-Sleep -Seconds 10
    }
    $bad = foreach ($n in $local.Keys) { $u = $up | Where-Object { $_.name -eq $n }; if (-not $u -or $u.digest -ne "sha256:$($local[$n])") { $n } }
    if ($bad) { throw "GitHub's SHA-256 doesn't match for: $($bad -join ', '). The release stays a draft; run -Resume to upload again." }
    if (Get-PublishingBlock) { return @{ status = 'blocked'; note = 'kill switch turned on before publishing the draft' } }
    $rel = Invoke-GitHub PATCH "$Api/releases/$($rel.id)" @{ draft = $false; make_latest = 'true' }
    Say "  published $($rel.html_url)" Green
    @{ status = 'done'; data = @{ url = $rel.html_url; id = $rel.id; digests = $local } }
}

# ======================================== 7. website drop (public hand-over) =====================================
function Step-Website($c) {
    $v = $c.Version; $tag = "v$v"
    $drop = Join-Path $c.RelDir 'website-drop'
    if (Test-Path $drop) { Remove-Item $drop -Recurse -Force }
    New-Item -ItemType Directory -Force "$drop\downloads", "$drop\source" | Out-Null

    # Downloads: the Release assets, from dist, each checked against GitHub's own digest
    $digests = (Get-StepData $c.State 'release').digests
    $files = @()
    foreach ($n in Get-AssetNames $v) {
        $src = Join-Path $c.Out $n; $sha = Get-Sha256 $src
        if (-not $c.DryRun) { if ($digests.$n -ne $sha) { throw "$n in dist doesn't match the GitHub Release asset" } }
        Copy-Item $src "$drop\downloads\$n"
        $lab = Get-AssetLabel $n $v
        $files += [ordered]@{ label = $lab[0]; kind = $lab[1]; file = $n; url = "downloads/$n"; bytes = (Get-Item $src).Length; sha256 = $sha }
    }

    # Source: the tagged public tree itself (dry run: the stage, since the tag doesn't exist yet)
    $srcName = "WhisperDesk-src-v$v.zip"; $srcZip = "$drop\source\$srcName"
    if ($c.DryRun) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory((Get-StepData $c.State 'sanitise').stage, $srcZip)
        $commit = '(dry run)'
    } else {
        Invoke-Git $PublicClone archive --format=zip "--prefix=WhisperDesk-src/" -o $srcZip $tag | Out-Null
        $commit = (& $Git -C $PublicClone rev-list -n 1 $tag).Trim()
    }
    $manifest = [ordered]@{
        app = $App; version = $v; released = (Get-Date).ToString('yyyy-MM-dd'); tag = $tag; commit = $commit
        release = (Get-StepData $c.State 'release').url
        files = $files
        source = [ordered]@{ file = $srcName; url = "downloads/source/$srcName"; bytes = (Get-Item $srcZip).Length; sha256 = (Get-Sha256 $srcZip); fileCount = (Get-ZipEntryHashes $srcZip).Count }
        notes = (Get-ChangelogSection $v)
    }
    Write-Utf8 "$drop\manifest.json" ($manifest | ConvertTo-Json -Depth 6)
    $card = Join-Path $Root "release\discord-$v.json"
    if (Test-Path $card) { Copy-Item $card "$drop\discord-card.json" }

    # The drop is scanned like the repository before it leaves (the source zip is checked file-by-file instead)
    $scanDir = Join-Path ([IO.Path]::GetTempPath()) ('wd-dropscan-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory $scanDir | Out-Null
        Copy-Item "$drop\manifest.json" $scanDir; if (Test-Path "$drop\discord-card.json") { Copy-Item "$drop\discord-card.json" $scanDir }
        Copy-Item "$drop\downloads\WhisperDesk-$v-SHA256SUMS.txt" $scanDir
        Invoke-SecretScan $scanDir (Join-Path $c.RelDir 'gitleaks-drop.json')
        Invoke-PrivacyScan $scanDir
    } finally { Remove-Item $scanDir -Recurse -Force -ErrorAction SilentlyContinue }
    # The source zip must be the scanned tree. git archive applies the public repo's .gitattributes (text=auto: LF), the
    # stage has the working copy's CRLF, so a file may differ only in carriage returns; anything else is a mismatch.
    $diff = Compare-ZipToFolder $srcZip (Get-StepData $c.State 'sanitise').stage $(if ($c.DryRun) { '' } else { 'WhisperDesk-src/' })   # the dry-run zip is the stage itself, no prefix
    if ($diff) { throw "the source zip doesn't match the scanned public tree: $($diff -join ', ')" }

    Say "  website drop ready: $drop ($($files.Count) downloads + source, manifest $((Get-Sha256 "$drop\manifest.json").Substring(0,12)))" Green
    if ($c.DryRun) { return @{ status = 'dry-run'; data = @{ drop = $drop } } }
    Say '  Hand-over: the Website & Deployments session publishes this drop (manifest.json + downloads\ + source\) and deploys with the maintainer''s go; then run release.ps1 -Resume to verify.' Cyan
    @{ status = 'waiting'; data = @{ drop = $drop; manifest = (Get-Sha256 "$drop\manifest.json") }; note = 'handed over, waiting for the website deploy' }
}

# ======================================== 8. website verification ================================================
function Read-SiteJs([string]$name, [string]$var) {
    $js = (Invoke-WebRequest ($Site + "assets/js/$name") -UseBasicParsing -TimeoutSec 30 -Headers @{ 'Cache-Control' = 'no-cache' }).Content
    if ($js -is [byte[]]) { $js = [Text.Encoding]::UTF8.GetString($js) }
    (($js -replace '(?m)^//.*$', '') -replace "^\s*window\.$var\s*=\s*", '' -replace ';\s*$', '') | ConvertFrom-Json
}
function Get-RemoteSha([string]$url) {
    $tmp = [IO.Path]::GetTempFileName()
    try { Invoke-WebRequest $url -UseBasicParsing -OutFile $tmp -TimeoutSec 3600 -Headers @{ 'Cache-Control' = 'no-cache' }; Get-Sha256 $tmp } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
}
function Step-VerifyWebsite($c) {
    $v = $c.Version
    $drop = (Get-StepData $c.State 'website').drop
    $man = Get-Content "$drop\manifest.json" -Raw -Encoding UTF8 | ConvertFrom-Json
    $fail = New-Object Collections.Generic.List[string]
    $rel = Read-SiteJs 'releases.js' 'DC_RELEASES'
    $src = Read-SiteJs 'sources.js' 'DC_SOURCES'
    $live = @(); foreach ($k in 'whisperdesk', 'whisperdesk-addons') { if ($rel.$k) { $live += @($rel.$k.files) } }
    if ($rel.whisperdesk.version -ne $v) { $fail.Add("releases.js says WhisperDesk $($rel.whisperdesk.version), expected $v") }
    foreach ($f in $man.files) {
        $l = $live | Where-Object { $_.file -eq $f.file } | Select-Object -First 1
        if (-not $l) { if ($f.kind -ne 'checksums') { $fail.Add("$($f.file) isn't listed on the website") }; continue }
        if ($l.sha256 -ne $f.sha256) { $fail.Add("releases.js lists a different SHA-256 for $($f.file)") }
        $got = Get-RemoteSha ($Site + $l.url.TrimStart('/'))
        if ($got -ne $f.sha256) { $fail.Add("the live $($f.file) is not the release file (got $($got.Substring(0,12)))") } else { Say "  $($f.file): identical" }
    }
    foreach ($l in $live) { if (-not ($man.files | Where-Object { $_.file -eq $l.file })) { $fail.Add("the website lists $($l.file), which isn't in this release") } }
    if ($src.whisperdesk.sha256 -ne $man.source.sha256) { $fail.Add('sources.js lists a different source archive') }
    else {
        $got = Get-RemoteSha ($Site + $src.whisperdesk.url.TrimStart('/'))
        if ($got -ne $man.source.sha256) { $fail.Add('the live source zip is not git archive of the release tag') } else { Say "  $($man.source.file): identical to the tagged public tree" }
    }
    if ($fail.Count) {
        if ($c.DryRun) { foreach ($f in $fail) { Say "  (dry run) would fail: $f" Yellow }; return @{ status = 'dry-run'; note = "$($fail.Count) difference(s) on the live site" } }
        throw "the website does not match the release (Discord is withheld):`n  " + ($fail -join "`n  ")
    }
    @{ status = $(if ($c.DryRun) { 'dry-run' } else { 'done' }); note = 'live site = release, byte for byte' }
}

# ======================================== 9. Discord (public) ====================================================
function Get-DiscordCard([string]$v, $man) {
    $f = Join-Path $Root "release\discord-$v.json"
    if (Test-Path $f) { return Get-Content $f -Raw -Encoding UTF8 | ConvertFrom-Json }
    # Generated from the CHANGELOG: bold headings become highlights, their first bullet the text
    $sec = Get-ChangelogSection $v
    $hl = @(); $cur = $null
    foreach ($line in $sec -split "`r?`n") {
        if ($line -match '^\*\*(.+)\*\*\s*$') { $cur = $Matches[1] }
        elseif ($cur -and $line -match '^- (.+)$') { $hl += [pscustomobject]@{ title = $cur; text = (($Matches[1] -replace '`', '') -replace '\s+', ' ') }; $cur = $null }
    }
    [pscustomobject]@{ title = "WhisperDesk $v is out"; content = "**WhisperDesk $v** is now available on the website."
        summary = 'Private, offline transcription with speaker identification, live captions and a forensics view, all running on your own PC.'
        highlights = $hl; notes = 'software.html#whisperdesk-whats-new'; download_page = 'software.html#whisperdesk'
        links = @([pscustomobject]@{ emoji = '0x1F4BB'; label = 'Source'; text = 'Browse the code'; url = 'source.html#whisperdesk' })
        icon = 'assets/img/whisperdesk.png'; color = '#3dd6d0'; footer = 'Free & open source (MIT) - Windows 10/11 - runs offline on your own PC' }
}
function Build-DiscordPayload($card, $man) {
    function Abs($u) { if ($u -match '^https?://') { $u } else { $Site + $u.TrimStart('/') } }
    function Size($b) { if ($b -ge 1MB) { '{0:N0} MB' -f ($b / 1MB) } else { '{0:N0} KB' -f ($b / 1KB) } }
    function E($cp) { [char]::ConvertFromUtf32($cp) }
    $dot = [string][char]0x00B7
    $desc = $card.summary + "`n"; foreach ($h in $card.highlights) { $desc += "`n> **$($h.title)**: $($h.text)" }
    $dl = @(); foreach ($f in $man.files | Where-Object { $_.kind -ne 'checksums' }) { $dl += "[$($f.label)]($(Abs $f.url)) $dot $(Size $f.bytes)" }
    $dl += "[All downloads & install help]($(Abs $card.download_page))"
    $notes = Abs $card.notes
    $fields = @(@{ name = "$(E 0x2B07)$(E 0xFE0F) Download"; value = ($dl -join "`n"); inline = $false }, @{ name = "$(E 0x1F4C4) Release notes"; value = "[What's new in $($man.version)]($notes)"; inline = $true })
    foreach ($l in $card.links) { $fields += @{ name = "$(E ([Convert]::ToInt32($l.emoji, 16))) $($l.label)"; value = "[$($l.text)]($(Abs $l.url))"; inline = $true } }
    $msi = $man.files | Where-Object { $_.kind -eq 'installer' } | Select-Object -First 1
    $fields += @{ name = "$(E 0x1F512) SHA-256 $dot $($msi.file)"; value = "``$($msi.sha256)``"; inline = $false }
    $fields += @{ name = "$(E 0x1F512) SHA-256 $dot $($man.source.file)"; value = "[Download source]($(Abs $man.source.url)) $dot $(Size $man.source.bytes)`n``$($man.source.sha256)``"; inline = $false }
    [ordered]@{
        content = $card.content
        embeds = @([ordered]@{ author = @{ name = "DarkCipher Systems $dot New release"; url = $Site }; title = $card.title; url = $notes; description = $desc
            color = [Convert]::ToInt32($card.color.TrimStart('#'), 16); thumbnail = @{ url = (Abs $card.icon) }; fields = $fields; footer = @{ text = $card.footer }
            timestamp = "$($man.released)T12:00:00.000Z" })
        components = @(@{ type = 1; components = @(@{ type = 2; style = 5; label = 'Download'; url = (Abs $msi.url) }, @{ type = 2; style = 5; label = 'Release notes'; url = $notes }, @{ type = 2; style = 5; label = 'Website'; url = (Abs $card.download_page) }) })
        allowed_mentions = @{ parse = @() }
    }
}
function Read-Posted { $h = @{}; if (Test-Path $PostedFile) { (Get-Content $PostedFile -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $h[$_.Name] = $_.Value } }; $h }
function Step-Discord($c) {
    $v = $c.Version; $key = "whisperdesk-$v"
    $posted = Read-Posted
    if ($posted.ContainsKey($key)) { return @{ status = 'done'; data = @{ message = $posted[$key].message }; note = "already posted $($posted[$key].at)" } }
    # Discord-only hold (e.g. no WhisperDesk card until the Store listing is public): the file's text says why.
    # Everything else still publishes; delete the file and -Resume to post.
    $hold = Join-Path $CfgDir 'discord-hold-whisperdesk'
    if (Test-Path $hold) { return @{ status = 'blocked'; note = "Discord held: $(([IO.File]::ReadAllText($hold)).Trim()) (delete $hold and -Resume to post)" } }
    $man = Get-Content "$((Get-StepData $c.State 'website').drop)\manifest.json" -Raw -Encoding UTF8 | ConvertFrom-Json
    $payload = Build-DiscordPayload (Get-DiscordCard $v $man) $man
    Write-Utf8 (Join-Path $c.RelDir 'discord-payload.json') ($payload | ConvertTo-Json -Depth 12)
    $hook = [DcxCred]::Read($WebhookTarget)
    if ($c.DryRun) { Say "  would post the card in $(Join-Path $c.RelDir 'discord-payload.json') to #whisperdesk ($(if ($hook) { 'webhook is set' } else { 'webhook NOT set yet' }))" Cyan; return @{ status = 'dry-run' } }
    if (-not $hook -or $hook -notmatch $WebhookRx) { return @{ status = 'blocked'; note = "no Discord webhook in Credential Manager ($WebhookTarget): run tools\release\set-discord-webhook.ps1" } }
    $send = { param($p, $comp) $q = '?wait=true' + $(if ($comp) { '&with_components=true' } else { '' }); Invoke-RestMethod -Method Post -Uri ($hook + $q) -Body ([Text.Encoding]::UTF8.GetBytes(($p | ConvertTo-Json -Depth 12))) -ContentType 'application/json; charset=utf-8' }
    try { $r = & $send $payload $true }
    catch {
        $code = 0; if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
        if ($code -ne 400) { throw "Discord rejected the card (HTTP $code)" }
        $p2 = [ordered]@{}; $payload.GetEnumerator() | Where-Object { $_.Key -ne 'components' } | ForEach-Object { $p2[$_.Key] = $_.Value }
        $r = & $send $p2 $false
    }
    $posted[$key] = @{ at = (Get-Date -Format 's'); channel = 'whisperdesk'; message = [string]$r.id }
    New-Item -ItemType Directory -Force $CfgDir | Out-Null
    Write-Utf8 $PostedFile ($posted | ConvertTo-Json)
    Say "  posted to #whisperdesk (message $($r.id))" Green
    @{ status = 'done'; data = @{ message = [string]$r.id } }
}
function Remove-DiscordPost([string]$v) {
    $key = "whisperdesk-$v"; $posted = Read-Posted
    if (-not $posted.ContainsKey($key)) { throw "no recorded Discord post for $key" }
    $hook = [DcxCred]::Read($WebhookTarget)
    if (-not $hook) { throw "no Discord webhook in Credential Manager ($WebhookTarget)" }
    Invoke-RestMethod -Method Delete -Uri "$hook/messages/$($posted[$key].message)" | Out-Null
    $posted.Remove($key); Write-Utf8 $PostedFile ($posted | ConvertTo-Json)
    Say "Deleted the Discord card for $v." Green
}
