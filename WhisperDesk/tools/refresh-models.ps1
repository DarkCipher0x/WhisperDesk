<#
    Checks every model in models\catalog.json against its upstream and reports what changed; with -Apply it updates
    the list. Read-only on the network: it only calls the public Hugging Face and GitHub APIs, downloads no model.

      .\tools\refresh-models.ps1            report: files changed upstream since the pinned revision, new GGML files
      .\tools\refresh-models.ps1 -Apply     re-pin changed files to the newest revision (url, bytes, sha256 from the
                                            upstream LFS record), bump "sequence" and "published"

    Hugging Face models: the newest commit of the repository's main branch, then each pinned file's LFS SHA-256 and
    size at that commit. GitHub release assets: the asset's size and, where GitHub reports it, its SHA-256 digest
    (an asset can be replaced under the same name; the app's SHA-256 check refuses a replaced file until the list
    is updated).

    New models are listed, not added: write their entry by hand (id, role, name, notes), then run -Apply to fill in
    nothing else, or copy bytes/sha256 from the report. After any change: build.ps1 embeds the list and publishes
    it signed as models.json with the release, and installed copies (the Microsoft Store one too) pick it up with
    Tools > Models > Check for new models.
#>
param([switch]$Apply, [string]$Catalog)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
if (-not $Catalog) { $Catalog = Join-Path (Split-Path $PSScriptRoot -Parent) 'models\catalog.json' }

$text = [IO.File]::ReadAllText($Catalog, (New-Object Text.UTF8Encoding $false))
$doc = $text | ConvertFrom-Json
$headers = @{ 'User-Agent' = 'WhisperDesk-maintainer-refresh-models' }
function Api([string]$url) { Invoke-RestMethod -Uri $url -Headers $headers -TimeoutSec 60 }

$changes = New-Object Collections.Generic.List[object]
$heads = @{}
$trees = @{}
$problems = 0

foreach ($m in $doc.models) {
    $u = [Uri]$m.url
    if ($u.Host -eq 'huggingface.co') {
        if ($u.AbsolutePath -notmatch '^/([^/]+/[^/]+)/resolve/([0-9a-f]{40})/(.+)$') { Write-Warning "$($m.id): not a pinned Hugging Face URL"; $problems++; continue }
        $repo = $Matches[1]; $rev = $Matches[2]; $path = $Matches[3]
        if (-not $heads.ContainsKey($repo)) { $heads[$repo] = (Api "https://huggingface.co/api/models/$repo/revision/main").sha }
        $head = $heads[$repo]
        $key = "$repo@$head"
        if (-not $trees.ContainsKey($key)) {
            $t = @{}
            foreach ($e in (Api "https://huggingface.co/api/models/$repo/tree/$head`?recursive=1")) { if ($e.type -eq 'file') { $t[$e.path] = $e } }
            $trees[$key] = $t
        }
        $e = $trees[$key][$path]
        if (-not $e) { Write-Warning "$($m.id): $path no longer exists on $repo main; it stays pinned to $rev"; $problems++; continue }
        $sha = if ($e.lfs) { $e.lfs.oid } else { $null }
        $size = if ($e.lfs) { [long]$e.lfs.size } else { [long]$e.size }
        if (-not $sha) { Write-Warning "$($m.id): no LFS SHA-256 upstream; check it by hand"; $problems++; continue }
        if ($sha -ne $m.sha256 -or $size -ne [long]$m.bytes) {
            Write-Host ("CHANGED  {0,-28} {1} -> {2} ({3:N0} bytes)" -f $m.id, $m.sha256.Substring(0, 12), $sha.Substring(0, 12), $size) -ForegroundColor Yellow
            $changes.Add([pscustomobject]@{ Model = $m; Url = "https://huggingface.co/$repo/resolve/$head/$path"; Bytes = $size; Sha = $sha })
        } elseif ($rev -ne $head) {
            Write-Host ("same     {0,-28} (pinned {1}, newest {2}: file unchanged)" -f $m.id, $rev.Substring(0, 8), $head.Substring(0, 8))
        } else {
            Write-Host ("current  {0,-28}" -f $m.id) -ForegroundColor Green
        }
    } elseif ($u.Host -eq 'github.com') {
        if ($u.AbsolutePath -notmatch '^/([^/]+/[^/]+)/releases/download/([^/]+)/([^/]+)$') { Write-Warning "$($m.id): not a GitHub release asset URL"; $problems++; continue }
        $rel = Api "https://api.github.com/repos/$($Matches[1])/releases/tags/$($Matches[2])"
        $name = $Matches[3]
        $a = @($rel.assets | Where-Object { $_.name -eq $name })[0]
        if (-not $a) { Write-Warning "$($m.id): asset $name is gone from that release"; $problems++; continue }
        $digest = if ($a.digest -and $a.digest -like 'sha256:*') { $a.digest.Substring(7) } else { $null }
        if ([long]$a.size -ne [long]$m.bytes -or ($digest -and $digest -ne $m.sha256)) {
            Write-Host ("CHANGED  {0,-28} GitHub asset replaced: {1:N0} bytes, sha256 {2}" -f $m.id, $a.size, $(if ($digest) { $digest } else { 'not reported (download and hash it)' })) -ForegroundColor Yellow
            if ($digest) { $changes.Add([pscustomobject]@{ Model = $m; Url = $m.url; Bytes = [long]$a.size; Sha = $digest }) } else { $problems++ }
        } else {
            Write-Host ("current  {0,-28}{1}" -f $m.id, $(if ($digest) { '' } else { ' (size matches; GitHub reports no digest)' })) -ForegroundColor Green
        }
    } else { Write-Warning "$($m.id): unexpected host $($u.Host)"; $problems++ }
}

# New GGML models in the Whisper repositories the list already uses
foreach ($key in $trees.Keys) {
    $repo = $key.Split('@')[0]
    $known = @($doc.models | ForEach-Object { ([Uri]$_.url).AbsolutePath.Split('/')[-1] })
    $new = @($trees[$key].Keys | Where-Object { $_ -like 'ggml-*.bin' -and $_ -notin $known -and $_ -notlike '*-encoder*' } | Sort-Object)
    if ($new.Count) {
        Write-Host "`nNot in the list yet ($repo):" -ForegroundColor Cyan
        foreach ($n in $new) { $e = $trees[$key][$n]; Write-Host ("  {0,-34} {1,8:N0} MB  sha256 {2}" -f $n, ([long]$e.lfs.size / 1MB), $e.lfs.oid) }
    }
}

if ($changes.Count -eq 0) { Write-Host "`nNo model changed upstream." -ForegroundColor Green; if ($problems) { exit 1 }; return }
if (-not $Apply) { Write-Host "`n$($changes.Count) model(s) changed upstream. Run with -Apply to update models\catalog.json." -ForegroundColor Yellow; return }

# Text edits keep the file's layout (the signed bytes are this file exactly, so review the diff before building).
foreach ($c in $changes) {
    $id = [regex]::Escape($c.Model.id)
    $rx = New-Object Text.RegularExpressions.Regex ('("id": "' + $id + '",[\s\S]*?"url": ")[^"]+("[\s\S]*?"bytes": )\d+(,[\s\S]*?"sha256": ")[0-9a-f]{64}(")')
    if (-not $rx.IsMatch($text)) { throw "Couldn't find the entry for $($c.Model.id) in $Catalog" }
    $text = $rx.Replace($text, { param($x) $x.Groups[1].Value + $c.Url + $x.Groups[2].Value + $c.Bytes + $x.Groups[3].Value + $c.Sha + $x.Groups[4].Value }, 1)
}
$seq = [int]$doc.sequence + 1
$text = $text -replace '"sequence": \d+,', "`"sequence`": $seq," -replace '"published": "[^"]*"', "`"published`": `"$(Get-Date -Format 'yyyy-MM-dd')`""
[IO.File]::WriteAllText($Catalog, $text, (New-Object Text.UTF8Encoding $false))
Write-Host "`nUpdated $($changes.Count) model(s); the list is now sequence $seq. Review the diff, run the tests, then build." -ForegroundColor Green
