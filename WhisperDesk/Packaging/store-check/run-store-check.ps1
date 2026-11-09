<#
    Microsoft Store pre-flight in a clean Windows: starts Windows Sandbox (no Visual C++ runtime, no GPU driver, nothing
    installed), installs dist\<ver>\store\WhisperDesk-<ver>-x64.msix there the way a customer gets it, and runs
    WhisperDesk's self-test (WhisperDesk.exe --selftest) inside the package and from the portable zip: the bundled VC++
    runtime, ONNX Runtime, model downloads with their SHA-256, whisper.cpp (CPU fallback without a GPU) and a real live
    session on Windows' own text-to-speech. The sandbox is thrown away afterwards; nothing changes on this PC.

      .\Packaging\store-check\run-store-check.ps1 [-Version 1.9.0] [-Gpu]

    -Gpu gives the sandbox this PC's GPU (default: none, like most certification machines). Needs the Windows Sandbox
    feature (Windows Pro/Enterprise). Opens the Sandbox window for a few minutes; it closes itself. Exit code 0 = pass.
    Windows PowerShell 5.1.
#>
param([string]$Version, [switch]$Gpu, [int]$TimeoutMinutes = 40)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $Version) { [xml]$proj = Get-Content (Join-Path $root 'WhisperDesk.csproj'); $Version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$dist = Join-Path $root "dist\$Version"
foreach ($f in "store\WhisperDesk-$Version-x64.msix", "WhisperDesk-$Version-x64-portable.zip", "WhisperDesk-$Version-x64.msi") {
    if (-not (Test-Path (Join-Path $dist $f))) { throw "$f not found in $dist (run build.ps1 first)" }
}
$sandbox = Join-Path $env:windir 'System32\WindowsSandbox.exe'
if (-not (Test-Path $sandbox)) { throw 'Windows Sandbox is not enabled (Turn Windows features on or off -> Windows Sandbox).' }
if (Get-Process WindowsSandbox, WindowsSandboxClient, WindowsSandboxRemoteSession -ErrorAction SilentlyContinue) { throw 'Windows Sandbox is already running; close it first (only one can run).' }

# signtool from the same pinned Microsoft.Windows.SDK.BuildTools package build.ps1 uses for the MSIX
[xml]$tools = Get-Content (Join-Path $root 'Packaging\StoreTools.csproj')
$tv = ($tools.Project.ItemGroup.PackageReference | Where-Object Include -eq 'Microsoft.Windows.SDK.BuildTools').Version
$signtool = Get-ChildItem (Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools\$tv\bin") -Recurse -Filter signtool.exe |
    Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found in Microsoft.Windows.SDK.BuildTools $tv (run build.ps1 once to restore it)." }

$work = Join-Path ([IO.Path]::GetTempPath()) ('wd-storecheck-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$toolDir = Join-Path $work 'tools'; $outDir = Join-Path $work 'out'
New-Item -ItemType Directory -Force "$toolDir\signtool", $outDir | Out-Null
Copy-Item "$($signtool.Directory.FullName)\*" "$toolDir\signtool" -Recurse
Copy-Item (Join-Path $PSScriptRoot 'inside-sandbox.ps1') $toolDir

# The Windows App Certification Kit (Windows SDK): run inside the sandbox when this PC has it (no UAC prompt here)
# The whole Windows Kits\10 folder: the kit runs its tests with TAEF, which is in Testing\Runtimes next to it.
$kitsDir = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$wackDir = Join-Path $kitsDir 'App Certification Kit'
$wackMap = if (Test-Path (Join-Path $wackDir 'appcert.exe')) { "<MappedFolder><HostFolder>$kitsDir</HostFolder><SandboxFolder>C:\check\kits</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>" } else { '' }

$wsb = Join-Path $work 'store-check.wsb'
[IO.File]::WriteAllText($wsb, @"
<Configuration>
  <VGpu>$(if ($Gpu) { 'Enable' } else { 'Disable' })</VGpu>
  <Networking>Enable</Networking>
  <MemoryInMB>8192</MemoryInMB>
  <AudioInput>Disable</AudioInput>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <PrinterRedirection>Disable</PrinterRedirection>
  <MappedFolders>
    <MappedFolder><HostFolder>$dist</HostFolder><SandboxFolder>C:\check\dist</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$toolDir</HostFolder><SandboxFolder>C:\check\tools</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$outDir</HostFolder><SandboxFolder>C:\check\out</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
    $wackMap
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\check\tools\inside-sandbox.ps1</Command></LogonCommand>
</Configuration>
"@)

Write-Host "Starting Windows Sandbox ($(if ($Gpu) { 'with' } else { 'without' }) a GPU) for WhisperDesk $Version... (it closes itself)" -ForegroundColor Cyan
Start-Process $sandbox -ArgumentList "`"$wsb`""
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while (-not (Test-Path "$outDir\done.txt") -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 10 }
if (-not (Test-Path "$outDir\done.txt")) { Write-Warning "The sandbox didn't finish within $TimeoutMinutes minutes." }

$pass = $true
foreach ($name in 'sandbox', 'store-package', 'store-package-cpu', 'portable') {
    $txt = Join-Path $outDir "$name.txt"
    Write-Host "`n===== $name =====" -ForegroundColor Cyan
    if (Test-Path $txt) { Get-Content $txt -Encoding UTF8 | Write-Host } else { Write-Host '(no report)' -ForegroundColor Red }
    if ($name -ne 'sandbox') {
        $json = [IO.Path]::ChangeExtension($txt, '.json')
        if (-not (Test-Path $json) -or -not (Get-Content $json -Raw | ConvertFrom-Json).passed) { $pass = $false }
    }
}
$wackXml = Join-Path $outDir 'wack.xml'
$wackNote = 'App Certification Kit not run'
if (Test-Path $wackXml) {
    # Required tests must pass. Reported but not failing: optional tests (Partner Center doesn't block on them), warnings,
    # and the Windows Runtime metadata tests when this PC's kit has no platform metadata (the package has no .winmd to check).
    $rep = [xml](Get-Content $wackXml -Raw -Encoding UTF8)
    $required = @(); $other = @()
    foreach ($t in $rep.SelectNodes('//TEST')) {
        $res = "$($t.RESULT.InnerText)".Trim()
        if (-not $res -or $res -eq 'PASS') { continue }
        $msgs = @($t.SelectNodes('.//MESSAGE') | ForEach-Object { $_.TEXT })
        $envOnly = [bool]($msgs | Where-Object { $_ -like '*platform metadata directory*' })
        if ($res -eq 'FAIL' -and $t.OPTIONAL -eq 'FALSE' -and -not $envOnly) { $required += "$($t.NAME): $($msgs -join ' | ')" }
        else { $other += "$($t.NAME): $res$(if ($envOnly) { ' (no platform metadata in this kit; nothing to check)' } elseif ($t.OPTIONAL -eq 'TRUE') { ' (optional)' })" }
    }
    Write-Host "`n===== App Certification Kit ($($rep.REPORT.OVERALL_RESULT) as reported) =====" -ForegroundColor Cyan
    foreach ($r in $required) { Write-Host "  REQUIRED FAIL  $r" -ForegroundColor Red }
    foreach ($o in $other) { Write-Host "  not blocking   $o" -ForegroundColor Yellow }
    $wackNote = if ($required.Count) { "App Certification Kit: $($required.Count) required test(s) failed" } else { "App Certification Kit: required tests pass ($($other.Count) non-blocking notes)" }
    if ($required.Count) { $pass = $false }
} elseif ($wackMap) { Write-Warning 'The App Certification Kit is installed but produced no report in the sandbox (see wack-*.txt); run it on its own before submitting.' }
Write-Host "`nReports: $outDir"
if ($pass) { Write-Host "STORE CHECK: PASS (Store package on GPU and CPU, portable copy, clean Windows; $wackNote)" -ForegroundColor Green; exit 0 }
Write-Host 'STORE CHECK: FAIL' -ForegroundColor Red; exit 1
