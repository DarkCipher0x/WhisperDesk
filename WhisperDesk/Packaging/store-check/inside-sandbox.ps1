# Runs INSIDE Windows Sandbox (started by run-store-check.ps1): a clean Windows with no Visual C++ runtime, no GPU driver
# and no WhisperDesk. Installs the real Store package the way a customer gets it (signed here with a throw-away test
# certificate, standing in for Microsoft's) and runs WhisperDesk's self-test inside the package, then from the portable
# zip. Everything happens in the sandbox, which is discarded when it closes. Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'
$dist = 'C:\check\dist'; $tools = 'C:\check\tools'; $out = 'C:\check\out'
$log = Join-Path $out 'sandbox.txt'
function Say([string]$t) { Add-Content -LiteralPath $log -Value $t -Encoding UTF8 }
# Windows' app activation (IApplicationActivationManager), so the package runs exactly as when started from Start.
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IApplicationActivationManager {
    int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
    int ActivateForFile(); int ActivateForProtocol();
}
[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] class ApplicationActivationManager { }
public static class WdActivation {
    public static uint Launch(string aumid, string args) {
        var m = (IApplicationActivationManager)new ApplicationActivationManager();
        uint pid; int hr = m.ActivateApplication(aumid, args, 0x2 /* AO_NOERRORUI */, out pid);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return pid;
    }
}
'@
function Shot([string]$file) {
    try {
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $b = [Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object Drawing.Bitmap $b.Width, $b.Height
        $g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size); $g.Dispose()
        $bmp.Save($file, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    } catch { Say "    (no screenshot: $($_.Exception.Message))" }
}

try {
    $msi = Get-ChildItem $dist -Filter 'WhisperDesk-*-x64.msi' | Select-Object -First 1
    $ver = $msi.Name -replace '^WhisperDesk-(.+)-x64\.msi$', '$1'
    Say "Windows Sandbox $([Environment]::OSVersion.Version), WhisperDesk $ver"
    foreach ($f in 'msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140_1.dll', 'vcomp140.dll', 'vulkan-1.dll') {
        Say ("  System32\{0} present: {1}" -f $f, (Test-Path "$env:windir\System32\$f"))
    }
    # Smart App Control / App Control for Business: a fresh Windows (like certification's) may enforce code integrity
    $sac = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState
    Say ("  Smart App Control state: {0} (0 off, 1 on, 2 evaluation)" -f $(if ($null -ne $sac) { $sac } else { 'not set' }))
    if ($sac -and $sac -ne 0) {
        # Smart App Control trusts the Store's Microsoft signature, which a local test can't have: the throw-away test
        # certificate (and the unsigned portable exe, and even the WACK's own resource DLLs) are blocked. This sandbox
        # is discarded afterwards, so switch it off here and test what the Store copy does once Windows lets it run.
        Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -Name VerifiedAndReputablePolicyState -Value 0 -Type DWord
        & "$env:windir\System32\CiTool.exe" --refresh --json 2>&1 | Out-Null
        Start-Sleep -Seconds 3
        Say "  Smart App Control switched off in this sandbox (it would block the test-signed copy; the Store's signature passes it)"
    }
    try { $dg = Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard; Say "  code integrity: kernel $($dg.CodeIntegrityPolicyEnforcementStatus), user mode $($dg.UsermodeCodeIntegrityPolicyEnforcementStatus) (0 off, 1 audit, 2 enforced)" } catch { }
    Say "  PowerShell language mode: $($ExecutionContext.SessionState.LanguageMode)"

    # ---- 1. The Microsoft Store package, installed and run with its package identity ----
    try {
        $pkgFile = 'C:\wd\WhisperDesk.msix'
        New-Item -ItemType Directory -Force C:\wd | Out-Null
        Copy-Item "$dist\store\WhisperDesk-$ver-x64.msix" $pkgFile
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $z = [IO.Compression.ZipFile]::OpenRead($pkgFile)
        try { $r = New-Object IO.StreamReader($z.GetEntry('AppxManifest.xml').Open()); [xml]$man = $r.ReadToEnd(); $r.Dispose() } finally { $z.Dispose() }
        $publisher = $man.Package.Identity.Publisher
        $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName 'WhisperDesk sandbox test' `
            -CertStoreLocation Cert:\CurrentUser\My -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
        Export-Certificate -Cert $cert -FilePath C:\wd\test.cer | Out-Null
        Import-Certificate -FilePath C:\wd\test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
        & "$tools\signtool\signtool.exe" sign /fd SHA256 /sha1 $cert.Thumbprint $pkgFile 2>&1 | Out-Null
        if ($LASTEXITCODE) { throw "signtool failed ($LASTEXITCODE)" }
        Add-AppxPackage -Path $pkgFile
        $pkg = Get-AppxPackage -Name $man.Package.Identity.Name
        Say "  installed $($pkg.PackageFullName) at $($pkg.InstallLocation)"
        $report = Join-Path $out 'store-package.txt'
        # Started the way Start and the Store start it: Windows' own app activation, with the package's identity.
        $aumid = "$($pkg.PackageFamilyName)!WhisperDesk"
        $appPid = [WdActivation]::Launch($aumid, "--selftest `"$report`"")
        Say "  started $aumid (process $appPid)"
        $deadline = (Get-Date).AddMinutes(20)
        while (-not (Test-Path ([IO.Path]::ChangeExtension($report, '.json'))) -and (Get-Date) -lt $deadline) {
            if (-not (Get-Process -Id $appPid -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 3; break }
            Start-Sleep -Seconds 5
        }
        Start-Sleep -Seconds 2
        if (Test-Path ([IO.Path]::ChangeExtension($report, '.json'))) {
            Say '  store package self-test: finished'
            # Again on the processor only, as on a PC with no usable GPU
            $cpuReport = Join-Path $out 'store-package-cpu.txt'
            $cpuPid = [WdActivation]::Launch($aumid, "--selftest `"$cpuReport`" --cpu")
            $cpuDeadline = (Get-Date).AddMinutes(20)
            while (-not (Test-Path ([IO.Path]::ChangeExtension($cpuReport, '.json'))) -and (Get-Date) -lt $cpuDeadline -and (Get-Process -Id $cpuPid -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 5 }
            Start-Sleep -Seconds 2
            Say ('  store package self-test, CPU only: ' + $(if (Test-Path ([IO.Path]::ChangeExtension($cpuReport, '.json'))) { 'finished' } else { 'NO RESULT' }))
        }
        else {
            $alive = [bool](Get-Process -Id $appPid -ErrorAction SilentlyContinue)
            Say ("  store package self-test: NO RESULT (" + $(if ($alive) { 'still running after 20 minutes' } else { 'the process ended without one' }) + "); screen saved as store-package.png")
            Shot (Join-Path $out 'store-package.png')
            Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 2; StartTime = (Get-Date).AddMinutes(-25) } -MaxEvents 5 -ErrorAction SilentlyContinue |
                ForEach-Object { Say ("    event {0} {1}: {2}" -f $_.Id, $_.ProviderName, ($_.Message -split "`n")[0..6] -join ' | ') }
            if ($alive) { Stop-Process -Id $appPid -Force -ErrorAction SilentlyContinue }
        }
    } catch { Say "  STORE PACKAGE ERROR: $($_.Exception.Message)" }

    # ---- 2. The portable zip (same program files, no package identity) ----
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory("$dist\WhisperDesk-$ver-x64-portable.zip", 'C:\wd-portable')
        $exe = Get-ChildItem C:\wd-portable -Recurse -Filter WhisperDesk.exe | Select-Object -First 1
        $p = Start-Process $exe.FullName -ArgumentList '--selftest', "`"$out\portable.txt`"" -PassThru
        if ($p.WaitForExit(20 * 60 * 1000)) { Say "  portable self-test exit code: $($p.ExitCode)" }
        else { Say '  portable self-test: still running after 20 minutes; screen saved as portable.png'; Shot (Join-Path $out 'portable.png'); $p.Kill() }
    } catch { Say "  PORTABLE ERROR: $($_.Exception.Message)" }

    # ---- 3. Windows App Certification Kit on the installed Store package (when the host has the kit) ----
    $pkg = Get-AppxPackage -Name 'DarkCipherSystems.WhisperDesk'
    if ((Test-Path 'C:\check\kits\App Certification Kit\appcert.exe') -and $pkg) {
        try {
            # A writable copy: the kit keeps its working files next to itself
            # Set up where the SDK installer puts it (writable, with the registry root the kit looks up)
            $kits = "${env:ProgramFiles(x86)}\Windows Kits\10"
            New-Item -ItemType Directory -Force $kits | Out-Null
            foreach ($d in 'App Certification Kit', 'Testing') { Copy-Item "C:\check\kits\$d" $kits -Recurse -Force }
            foreach ($k in 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots', 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots') {
                New-Item -Force $k | Out-Null; Set-ItemProperty $k -Name KitsRoot10 -Value "$kits\"
            }
            $appcert = "$kits\App Certification Kit\appcert.exe"
            & $appcert reset *> (Join-Path $out 'wack-log.txt')
            $wack = Join-Path $out 'wack.xml'
            $p = Start-Process $appcert -ArgumentList 'test', '-appxpackagepath', 'C:\wd\WhisperDesk.msix', '-reportoutputpath', "`"$wack`"" -PassThru -Wait -WindowStyle Hidden `
                -RedirectStandardOutput (Join-Path $out 'wack-out.txt') -RedirectStandardError (Join-Path $out 'wack-err.txt')
            if (Test-Path $wack) {
                [xml]$rep = Get-Content $wack
                Say "  WACK overall: $($rep.REPORT.OVERALL_RESULT) (exit $($p.ExitCode))"
                foreach ($t in $rep.SelectNodes('//TEST')) {
                    $res = $t.RESULT.InnerText
                    if ($res -and $res -ne 'PASS') {
                        $first = @($t.SelectNodes('.//MESSAGE') | ForEach-Object { $_.TEXT })[0]
                        Say ("    {0}: {1} (optional: {2}){3}" -f $t.NAME, $res, $t.OPTIONAL, $(if ($first) { ' - ' + $first.Substring(0, [Math]::Min(160, $first.Length)) }))
                    }
                }
            } else { Say "  WACK: no report (exit $($p.ExitCode))" }
        } catch { Say "  WACK ERROR: $($_.Exception.Message)" }
    } elseif (-not (Test-Path 'C:\check\kits\App Certification Kit\appcert.exe')) { Say '  WACK: the App Certification Kit is not installed on the host; skipped' }
}
catch { Say "ERROR: $($_.Exception.Message)" }
finally {
    # What code integrity blocked or would block (Smart App Control / App Control), with the file and the reason
    try {
        $ci = @(Get-WinEvent -LogName 'Microsoft-Windows-CodeIntegrity/Operational' -MaxEvents 400 -ErrorAction Stop |
            Where-Object { $_.Id -in 3033, 3034, 3076, 3077, 3089, 3118 } )
        Say "  code integrity events: $($ci.Count)"
        $ci | Select-Object -First 25 | ForEach-Object { Say ("    {0} {1}: {2}" -f $_.Id, $_.TimeCreated.ToString('HH:mm:ss'), (($_.Message -replace '\s+', ' ').Substring(0, [Math]::Min(400, ($_.Message -replace '\s+', ' ').Length)))) }
    } catch { Say "  code integrity log: $($_.Exception.Message)" }
    Set-Content -LiteralPath (Join-Path $out 'done.txt') -Value 'done'
    shutdown.exe /s /t 3 | Out-Null
}
