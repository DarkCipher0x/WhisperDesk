# Releases the WhisperDesk version in WhisperDesk.csproj: build, checks, the sanitised public repository, the GitHub
# Release, the website hand-over and its verification, and the Discord card - in that order, every time.
# The whole design, the kill switch and how to recover: docs\RELEASE-PIPELINE.md.
#
#   .\release.ps1                 run every step for this version (stops at the first failure)
#   .\release.ps1 -DryRun         every step, nothing public changes (builds into dist\_dryrun\<ver>\)
#   .\release.ps1 -Resume         continue a run that stopped (a failure, the kill switch, the website deploy)
#   .\release.ps1 -LocalOnly      build, verify, sanitise and scan only
#   .\release.ps1 -Step scan      run one step again (every earlier step must be done)
#   .\release.ps1 -RetractDiscord delete this version's Discord card
#
# Kill switch: the file %USERPROFILE%\.darkcipher\publishing-disabled, or DCX_PUBLISH=off. Either stops every public
# step (mirror, release, website, discord); the local steps still run.
param([switch]$DryRun, [switch]$Resume, [switch]$LocalOnly, [string]$Step, [switch]$RetractDiscord)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tools\release\common.ps1')
. (Join-Path $PSScriptRoot 'tools\release\steps.ps1')

$version = Get-AppVersion
if ($RetractDiscord) { Remove-DiscordPost $version; return }

# The build goes to dist\<ver>\. The run's own files (state, public stage, website drop) live OUTSIDE the project folder,
# in ..\WhisperDesk-release\<ver>\: the project compiles every .cs/.xaml under its folder, so a staged copy of the
# source inside it would be built twice.
$work = Join-Path $Projects 'WhisperDesk-release'
$c = @{
    Version = $version; DryRun = [bool]$DryRun
    Out = if ($DryRun) { Join-Path $Root "dist\_dryrun\$version" } else { Join-Path $Root "dist\$version" }
    RelDir = if ($DryRun) { Join-Path $work "_dryrun\$version" } else { Join-Path $work $version }
}
$c.StateFile = Join-Path $c.RelDir 'state.json'
if ($DryRun -and -not $Resume -and (Test-Path $c.RelDir)) { Remove-Item $c.RelDir -Recurse -Force }   # a fresh dry run starts clean
New-Item -ItemType Directory -Force $c.RelDir | Out-Null
$c.State = Read-State $c.StateFile $version

$started = @($c.State.steps.Keys | Where-Object { $_ -ne 'preflight' -and (Get-StepStatus $c.State $_) -eq 'done' })
if ($started -and -not ($Resume -or $Step -or $DryRun)) {
    throw "A release run for $version already exists ($($c.StateFile): $($started -join ', ') done). Use -Resume to continue it."
}

$steps = if ($Step) {
    if ($StepOrder -notcontains $Step) { throw "unknown step '$Step' (steps: $($StepOrder -join ', '))" }
    @($Step)
} elseif ($LocalOnly) { $LocalSteps } else { $StepOrder }

Say ("WhisperDesk {0} release{1}" -f $version, $(if ($DryRun) { ' (DRY RUN: nothing public changes)' } else { '' })) Cyan
$ok = if ($DryRun) { 'done', 'dry-run' } else { 'done' }
foreach ($name in $steps) {
    # Strict order: every earlier step must have finished for this version
    $i = [array]::IndexOf($StepOrder, $name)
    foreach ($prev in $StepOrder[0..([Math]::Max(0, $i - 1))] | Where-Object { $_ -ne $name }) {
        if ($ok -notcontains (Get-StepStatus $c.State $prev)) { throw "step '$name' needs '$prev' first (it is '$(Get-StepStatus $c.State $prev)')" }
    }
    $status = Get-StepStatus $c.State $name
    # Done public steps are not repeated; local checks always run again (they are cheap or detect their own reuse)
    if ($status -eq 'done' -and $PublicSteps -contains $name -and -not $Step) { Say "[$name] done earlier" DarkGray; continue }
    if ($name -eq 'verify-website' -and $status -eq 'done' -and -not $Step) { Say "[$name] done earlier" DarkGray; continue }

    Say "[$name]" White
    if ($PublicSteps -contains $name -and -not $DryRun) {
        $block = Get-PublishingBlock
        if ($block) { Set-Step $c.State $c.StateFile $name 'blocked' @{} $block; Say "  BLOCKED by the kill switch ($block). Run -Resume when publishing is back on." Yellow; exit 2 }
    }
    if ($name -eq 'verify-website' -and -not $DryRun -and (Get-StepStatus $c.State 'website') -eq 'waiting') {
        Say '  checking whether the website now serves this release...'
    }
    try {
        $fn = 'Step-' + ((($name -split '-') | ForEach-Object { $_.Substring(0, 1).ToUpper() + $_.Substring(1) }) -join '')
        $r = & $fn $c
    } catch {
        Set-Step $c.State $c.StateFile $name 'failed' @{} $_.Exception.Message
        Say "  FAILED: $($_.Exception.Message)" Red
        Say "  Record: $($c.StateFile). Fix the cause, then run release.ps1 -Resume." Red
        exit 1
    }
    Set-Step $c.State $c.StateFile $name $r.status $r.data $r.note
    if ($name -eq 'website' -and $r.status -eq 'waiting') {
        # The website step is complete once the live site is verified
        Set-Step $c.State $c.StateFile 'website' 'done' $r.data 'handed over'
    }
    if ($r.status -eq 'blocked') { Say "  BLOCKED: $($r.note)" Yellow; exit 2 }
    Say ("  {0}{1}" -f $r.status, $(if ($r.note) { ": $($r.note)" } else { '' })) $(if ($r.status -eq 'done') { 'Green' } else { 'Cyan' })
    if ($name -eq 'website' -and -not $DryRun) {
        # Verification only passes once the Website & Deployments session has deployed the drop
        try { $v8 = Step-VerifyWebsite $c; Set-Step $c.State $c.StateFile 'verify-website' $v8.status $v8.data $v8.note }
        catch { Set-Step $c.State $c.StateFile 'verify-website' 'waiting' @{} 'the website does not serve this release yet'; Say '  The website does not serve this release yet. After the deploy, run release.ps1 -Resume.' Cyan; exit 3 }
    }
}
Say ("WhisperDesk {0}: {1}" -f $version, (($StepOrder | ForEach-Object { "$_=$(Get-StepStatus $c.State $_)" }) -join ' ')) Green
