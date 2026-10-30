# Shared helpers for release.ps1 (dot-sourced). See docs\RELEASE-PIPELINE.md.
# Windows PowerShell 5.1, ASCII only.

$script:App = 'whisperdesk'
$script:Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent          # ...\WhisperDesk
$script:Projects = Split-Path $Root -Parent
$script:Owner = 'DarkCipher0x'
$script:RepoName = 'WhisperDesk'
$script:Api = "https://api.github.com/repos/$Owner/$RepoName"
$script:ExpectedOrigin = "https://github.com/$Owner/$RepoName.git"
$script:PublicClone = if ($env:WD_PUBLIC_REPO) { $env:WD_PUBLIC_REPO } else { Join-Path $Projects 'WhisperDesk-public' }
$script:Site = 'https://darkciphersystems.com/'
$script:Git = 'C:\Program Files\Git\cmd\git.exe'
if (-not (Test-Path $Git)) { $script:Git = 'git' }
$script:CfgDir = Join-Path $env:USERPROFILE '.darkcipher'
$script:KillFile = Join-Path $CfgDir 'publishing-disabled'
$script:PostedFile = Join-Path $CfgDir 'discord-posted.json'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Get-AppVersion {
    [xml]$p = Get-Content (Join-Path $Root 'WhisperDesk.csproj')
    $v = $p.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ($v -notmatch '^\d+\.\d+\.\d+$') { throw "WhisperDesk.csproj <Version> '$v' is not x.y.z" }
    $v
}

function Get-Sha256([string]$path) { (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Write-Utf8([string]$path, [string]$text) {
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false))
}

function Say([string]$msg, [string]$color = 'Gray') { Write-Host $msg -ForegroundColor $color }

# ---- kill switch -------------------------------------------------------------------------------------------------
# Public steps run only when this returns $null; otherwise it returns the reason. Checked at the start of every public
# step, so creating the file stops a run in progress before its next public step.
function Get-PublishingBlock {
    if ($env:DCX_PUBLISH -and $env:DCX_PUBLISH.Trim().ToLowerInvariant() -in 'off', '0', 'false', 'no') { return "DCX_PUBLISH=$($env:DCX_PUBLISH)" }
    if (Test-Path $KillFile) {
        $why = (Get-Content $KillFile -Raw -ErrorAction SilentlyContinue)
        return "kill switch file $KillFile$(if ($why) { ': ' + $why.Trim() })"
    }
    $null
}

# ---- state (dist\<ver>\release-state.json, or the dry-run folder's) ----------------------------------------------
function Read-State([string]$file, [string]$version) {
    if (Test-Path $file) {
        $s = Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($s.version -ne $version) { throw "$file is for $($s.version), not $version" }
        $h = [ordered]@{ version = $s.version; steps = [ordered]@{} }
        foreach ($p in $s.steps.PSObject.Properties) { $h.steps[$p.Name] = $p.Value }
        return $h
    }
    [ordered]@{ version = $version; steps = [ordered]@{} }
}
function Save-State($state, [string]$file) { Write-Utf8 $file ($state | ConvertTo-Json -Depth 8) }
function Set-Step($state, [string]$file, [string]$name, [string]$status, $data = @{}, [string]$note = '') {
    # Through JSON, so a step's data reads the same in this run as after a -Resume (PSCustomObject, not hashtable)
    if ($null -eq $data) { $data = @{} }
    $data = $data | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    $state.steps[$name] = [pscustomobject]@{ status = $status; at = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK'); note = $note; data = $data }
    Save-State $state $file
}
function Get-StepStatus($state, [string]$name) { if ($state.steps.Contains($name)) { $state.steps[$name].status } else { $null } }
function Get-StepData($state, [string]$name) { if ($state.steps.Contains($name)) { $state.steps[$name].data } else { $null } }

# ---- git ---------------------------------------------------------------------------------------------------------
# Invoke-Git <dir> <git arguments...> (a simple function on purpose: git's own -q/--flags pass through as text)
function Invoke-Git {
    $dir = $args[0]; $rest = @($args | Select-Object -Skip 1)
    $out = & $Git -C $dir @rest
    if ($LASTEXITCODE) { throw "git $($rest -join ' ') failed in $dir" }
    $out
}
function Assert-Origin {
    if (-not (Test-Path (Join-Path $PublicClone '.git'))) { throw "no public clone at $PublicClone" }
    $url = (& $Git -C $PublicClone remote get-url origin).Trim()
    if ($url -ne $ExpectedOrigin) { throw "the public clone's origin is '$url', expected '$ExpectedOrigin' - refusing to push" }
    $push = (& $Git -C $PublicClone remote get-url --push origin).Trim()
    if ($push -ne $ExpectedOrigin) { throw "the public clone's push URL is '$push', expected '$ExpectedOrigin'" }
}

# ---- GitHub (token from Git Credential Manager, memory only) -----------------------------------------------------
$script:GhHeaders = $null
function Get-GhHeaders {
    if ($script:GhHeaders) { return $script:GhHeaders }
    $psi = New-Object Diagnostics.ProcessStartInfo $Git, 'credential fill'
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true
    $inEnc = [Console]::InputEncoding
    [Console]::InputEncoding = New-Object Text.UTF8Encoding $false     # git rejects a byte-order mark before "protocol="
    try { $p = [Diagnostics.Process]::Start($psi) } finally { [Console]::InputEncoding = $inEnc }
    $p.StandardInput.Write("protocol=https`nhost=github.com`n`n"); $p.StandardInput.Close()
    $lines = $p.StandardOutput.ReadToEnd() -split "`n"; $p.WaitForExit()
    $tok = $lines | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
    if (-not $tok) { throw 'no GitHub sign-in in Git Credential Manager: push once with git so it asks you to sign in' }
    $script:GhHeaders = @{ Authorization = "Bearer $($tok.TrimEnd("`r").Substring(9))"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'WhisperDesk-release' }
    $script:GhHeaders
}
function Invoke-GitHub([string]$method, [string]$uri, $body = $null) {
    $json = $null
    if ($null -ne $body) { $json = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json $body -Depth 6)) }   # (not an if-expression: it would unroll the byte[])
    Invoke-RestMethod -Method $method -Uri $uri -Headers (Get-GhHeaders) -Body $json -ContentType 'application/json; charset=utf-8'
}
function Get-GhRelease([string]$tag) {
    # The tag endpoint only returns published releases; drafts are found in the list
    try { return Invoke-GitHub GET "$Api/releases/tags/$tag" } catch { if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw } }
    $all = @(Invoke-GitHub GET "$Api/releases?per_page=50")
    $all | Where-Object { $_.tag_name -eq $tag -or $_.name -eq "WhisperDesk $($tag.TrimStart('v'))" } | Select-Object -First 1
}

# ---- Windows Credential Manager (Discord webhook) ----------------------------------------------------------------
if (-not ('DcxCred' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DcxCred {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL { public int Flags; public int Type; public string TargetName; public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten; public int CredentialBlobSize; public IntPtr CredentialBlob;
        public int Persist; public int AttributeCount; public IntPtr Attributes; public string TargetAlias; public string UserName; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredReadW(string target, int type, int flags, out IntPtr cred);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWriteW(ref CREDENTIAL cred, int flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr cred);
    public static string Read(string target) {
        IntPtr p; if (!CredReadW(target, 1, 0, out p)) return null;
        try { var c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
              return c.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2); }
        finally { CredFree(p); }
    }
    public static void Write(string target, string user, string secret) {
        var c = new CREDENTIAL(); c.Type = 1; c.TargetName = target; c.UserName = user; c.Persist = 2;
        c.CredentialBlobSize = secret.Length * 2; c.CredentialBlob = Marshal.StringToCoTaskMemUni(secret);
        try { if (!CredWriteW(ref c, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(c.CredentialBlob); }
    }
}
'@
}
$script:WebhookTarget = 'DarkCipher/discord/whisperdesk'
$script:WebhookRx = '^https://(discord|discordapp)\.com/api/webhooks/\d+/[\w-]+$'

# ---- glob allowlist ----------------------------------------------------------------------------------------------
function ConvertTo-GlobRegex([string]$glob) {
    $g = $glob.Trim().Replace('\', '/')
    $sb = New-Object Text.StringBuilder '^'
    for ($i = 0; $i -lt $g.Length; $i++) {
        $c = $g[$i]
        if ($c -eq '*') {
            if ($i + 1 -lt $g.Length -and $g[$i + 1] -eq '*') {
                $i++
                if ($i + 1 -lt $g.Length -and $g[$i + 1] -eq '/') { $i++; [void]$sb.Append('(?:.*/)?') } else { [void]$sb.Append('.*') }
            } else { [void]$sb.Append('[^/]*') }
        } elseif ($c -eq '?') { [void]$sb.Append('[^/]') }
        else { [void]$sb.Append([regex]::Escape([string]$c)) }
    }
    [void]$sb.Append('$')
    New-Object regex ($sb.ToString(), [Text.RegularExpressions.RegexOptions]::IgnoreCase)
}
function Read-Globs([string]$file) {
    Get-Content $file -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object { ConvertTo-GlobRegex $_ }
}
