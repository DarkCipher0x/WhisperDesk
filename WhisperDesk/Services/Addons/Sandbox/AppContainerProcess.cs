using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WhisperDesk.Services.Addons.Sandbox;

/// <summary>
/// Starts "WhisperDesk.exe --addon-host" inside a Windows AppContainer, the sandbox Windows uses for Store apps:
/// the process gets its own low-privilege identity that can't read the user's files, the registry outside its own
/// hive, or other processes, and has no network unless the network capability SIDs are added. It talks to WhisperDesk
/// only through two anonymous pipes. A job object keeps it to a single process (no child programs), caps its memory and
/// kills it when WhisperDesk exits.
/// </summary>
internal sealed class AppContainerProcess : IDisposable
{
    private const string InternetClient = "S-1-15-3-1";              // internetClient capability
    private const string PrivateNetworkClientServer = "S-1-15-3-3";  // privateNetworkClientServer (home/work LAN)

    private readonly SafeProcessHandle _process;
    private readonly IntPtr _job;
    public StreamWriter Input { get; }
    public StreamReader Output { get; }
    public int ProcessId { get; }

    private AppContainerProcess(SafeProcessHandle process, IntPtr job, int pid, SafeFileHandle stdinWrite, SafeFileHandle stdoutRead)
    {
        _process = process;
        _job = job;
        ProcessId = pid;
        Input = new StreamWriter(new FileStream(stdinWrite, FileAccess.Write, 1), new UTF8Encoding(false)) { AutoFlush = true };
        Output = new StreamReader(new FileStream(stdoutRead, FileAccess.Read, 1), Encoding.UTF8);
    }

    public bool HasExited => WaitForSingleObject(_process.DangerousGetHandle(), 0) == 0;

    /// <summary>A stable AppContainer name per add-on id (so its granted folders keep working across runs).</summary>
    public static string ContainerName(string addonId) =>
        "WhisperDesk.Addon." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(addonId)))[..20];

    public static SecurityIdentifier ContainerSid(string addonId)
    {
        var name = ContainerName(addonId);
        int hr = CreateAppContainerProfile(name, "WhisperDesk add-on", "Sandbox for a WhisperDesk add-on", IntPtr.Zero, 0, out var sid);
        if (hr == unchecked((int)0x800700B7))   // already exists
            hr = DeriveAppContainerSidFromAppContainerName(name, out sid);
        if (hr != 0) throw new Win32Exception(hr, "Could not create the add-on sandbox (AppContainer).");
        try { return new SecurityIdentifier(sid); }
        finally { FreeSid(sid); }
    }

    /// <summary>Gives the sandbox read (or read/write) access to one folder and everything in it.</summary>
    public static void Grant(SecurityIdentifier sid, string folder, bool write)
    {
        var dir = new DirectoryInfo(folder);
        if (!dir.Exists) dir.Create();
        var rights = write ? FileSystemRights.Modify | FileSystemRights.Synchronize : FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
        var acl = dir.GetAccessControl();
        foreach (FileSystemAccessRule r in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            if (r.IdentityReference == sid && r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & rights) == rights) return;
        acl.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                                   PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(acl);
    }

    /// <summary>Removes the sandbox's access to a folder (when the add-on is removed or the folder is changed).</summary>
    public static void Revoke(SecurityIdentifier sid, string folder)
    {
        try
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists) return;
            var acl = dir.GetAccessControl();
            acl.PurgeAccessRules(sid);
            dir.SetAccessControl(acl);
        }
        catch { }
    }

    /// <summary>Deletes the add-on's AppContainer profile (its sandbox identity and private storage) from this Windows account.</summary>
    public static void DeleteProfile(string addonId)
    {
        try { DeleteAppContainerProfile(ContainerName(addonId)); } catch { }
    }

    /// <summary>Starts the sandboxed host. <paramref name="network"/> adds the internet and local-network capabilities.</summary>
    public static AppContainerProcess Start(SecurityIdentifier container, bool network, string workingDirectory, string? exePath = null)
    {
        var exe = exePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("Can't find WhisperDesk.exe.");
        var capSids = new List<IntPtr>();
        IntPtr caps = IntPtr.Zero, containerSid = IntPtr.Zero, attrList = IntPtr.Zero, handleList = IntPtr.Zero, secCaps = IntPtr.Zero, env = IntPtr.Zero;
        SafeFileHandle? inRead = null, inWrite = null, outRead = null, outWrite = null;
        var job = IntPtr.Zero;
        try
        {
            var sidBytes = new byte[container.BinaryLength];
            container.GetBinaryForm(sidBytes, 0);
            containerSid = Marshal.AllocHGlobal(sidBytes.Length);
            Marshal.Copy(sidBytes, 0, containerSid, sidBytes.Length);

            if (network)
                foreach (var s in new[] { InternetClient, PrivateNetworkClientServer })
                {
                    if (!ConvertStringSidToSid(s, out var p)) throw new Win32Exception();
                    capSids.Add(p);
                }
            int saSize = Marshal.SizeOf<SidAndAttributes>();
            if (capSids.Count > 0)
            {
                caps = Marshal.AllocHGlobal(saSize * capSids.Count);
                for (int i = 0; i < capSids.Count; i++)
                    Marshal.StructureToPtr(new SidAndAttributes { Sid = capSids[i], Attributes = SeGroupEnabled }, caps + i * saSize, false);
            }
            secCaps = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(new SecurityCapabilities { AppContainerSid = containerSid, Capabilities = caps, CapabilityCount = (uint)capSids.Count }, secCaps, false);

            // Pipes: the child's ends are inheritable, ours are not.
            var sa = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), bInheritHandle = true };
            if (!CreatePipe(out inRead, out inWrite, ref sa, 0) || !CreatePipe(out outRead, out outWrite, ref sa, 0)) throw new Win32Exception();
            SetHandleInformation(inWrite, HandleFlagInherit, 0);
            SetHandleInformation(outRead, HandleFlagInherit, 0);

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 2, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)ProcThreadAttributeSecurityCapabilities, secCaps, (IntPtr)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();
            // Only these two handles are inherited, nothing else WhisperDesk has open.
            handleList = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(handleList, 0, inRead.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size, outWrite.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)ProcThreadAttributeHandleList, handleList, (IntPtr)(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var si = new StartupInfoEx();
            si.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            si.StartupInfo.dwFlags = StartfUseStdHandles;
            si.StartupInfo.hStdInput = inRead.DangerousGetHandle();
            si.StartupInfo.hStdOutput = outWrite.DangerousGetHandle();
            si.StartupInfo.hStdError = outWrite.DangerousGetHandle();
            si.lpAttributeList = attrList;

            var cmd = new StringBuilder($"\"{exe}\" {SandboxProtocol.HostArgument}");
            env = Marshal.StringToHGlobalUni(MinimalEnvironment(workingDirectory));
            if (!CreateProcess(exe, cmd, IntPtr.Zero, IntPtr.Zero, true,
                    ExtendedStartupInfoPresent | CreateNoWindow | CreateSuspended | CreateUnicodeEnvironment, env, workingDirectory, ref si, out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"Could not start the add-on sandbox (error {err}).");
            }

            var process = new SafeProcessHandle(pi.hProcess, true);
            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                var limits = new JobExtendedLimits();
                limits.Basic.LimitFlags = JobKillOnClose | JobActiveProcess | JobProcessMemory | JobDieOnUnhandledException;
                limits.Basic.ActiveProcessLimit = 1;                               // no child processes
                limits.ProcessMemoryLimit = (UIntPtr)(2UL << 30);                  // 2 GB
                if (job == IntPtr.Zero || !SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobExtendedLimits>()) ||
                    !AssignProcessToJobObject(job, pi.hProcess))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not limit the add-on sandbox.");
                // At most a quarter of the CPU, so an add-on can't slow down transcription.
                var cpu = new JobCpuRate { ControlFlags = 0x1 | 0x4, CpuRate = 2500 };   // ENABLE | HARD_CAP, 25.00 %
                SetInformationJobObjectCpu(job, 15, ref cpu, Marshal.SizeOf<JobCpuRate>());
                ResumeThread(pi.hThread);
            }
            catch
            {
                TerminateProcess(pi.hProcess, 1);
                process.Dispose();
                throw;
            }
            finally { CloseHandle(pi.hThread); }

            var result = new AppContainerProcess(process, job, pi.dwProcessId, inWrite, outRead);
            job = IntPtr.Zero;
            inWrite = null; outRead = null;   // owned by the streams now
            return result;
        }
        finally
        {
            inRead?.Dispose(); outWrite?.Dispose();   // the child has its copies
            inWrite?.Dispose(); outRead?.Dispose();
            if (job != IntPtr.Zero) CloseHandle(job);
            if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            if (secCaps != IntPtr.Zero) Marshal.FreeHGlobal(secCaps);
            if (caps != IntPtr.Zero) Marshal.FreeHGlobal(caps);
            if (containerSid != IntPtr.Zero) Marshal.FreeHGlobal(containerSid);
            if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
            foreach (var p in capSids) LocalFree(p);
        }
    }

    /// <summary>
    /// Only what Windows and .NET need: no tokens, keys, paths or anything else from WhisperDesk's environment reaches the
    /// add-on (the profile variables are required by Windows to set up the sandbox; the add-on still can't read those folders).
    /// TEMP points at its own data folder.
    /// </summary>
    private static string MinimalEnvironment(string dataFolder)
    {
        var sys = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = sys, ["windir"] = sys, ["SystemDrive"] = Path.GetPathRoot(sys)?.TrimEnd('\\') ?? "C:",
            ["PATH"] = $@"{sys}\System32;{sys}", ["TEMP"] = dataFolder, ["TMP"] = dataFolder, ["OS"] = "Windows_NT",
            ["NUMBER_OF_PROCESSORS"] = Environment.ProcessorCount.ToString(),
            // Windows needs these to give the sandbox its own private folders (it can't read the real ones).
            ["USERPROFILE"] = Environment.GetEnvironmentVariable("USERPROFILE") ?? "",
            ["LOCALAPPDATA"] = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "",
            ["APPDATA"] = Environment.GetEnvironmentVariable("APPDATA") ?? "",
            ["PROCESSOR_ARCHITECTURE"] = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "AMD64",
        };
        var sb = new StringBuilder();
        foreach (var (k, v) in vars) sb.Append(k).Append('=').Append(v).Append('\0');
        return sb.Append('\0').ToString();
    }

    public void Kill()
    {
        try { TerminateProcess(_process.DangerousGetHandle(), 1); } catch { }
    }

    public void Dispose()
    {
        try { Input.Dispose(); } catch { }
        try { Output.Dispose(); } catch { }
        if (_job != IntPtr.Zero) CloseHandle(_job);   // kill-on-close ends the process if it is still running
        _process.Dispose();
    }

    // ---------------- Win32 ----------------

    private const uint SeGroupEnabled = 0x4, HandleFlagInherit = 0x1, StartfUseStdHandles = 0x100;
    private const uint ExtendedStartupInfoPresent = 0x80000, CreateNoWindow = 0x08000000, CreateSuspended = 0x4, CreateUnicodeEnvironment = 0x400;
    private const int ProcThreadAttributeSecurityCapabilities = 0x00020009, ProcThreadAttributeHandleList = 0x00020002;
    private const uint JobKillOnClose = 0x2000, JobActiveProcess = 0x8, JobProcessMemory = 0x100, JobDieOnUnhandledException = 0x400;

    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities { public IntPtr AppContainerSid; public IntPtr Capabilities; public uint CapabilityCount; public uint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int nLength; public IntPtr lpSecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimits
    {
        public JobBasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr caps, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ConvertStringSidToSidW")]
    private static extern bool ConvertStringSidToSid(string sid, out IntPtr psid);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes sa, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle h, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? cwd,
                                             ref StartupInfoEx si, out ProcessInformation pi);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr sa, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int cls, ref JobExtendedLimits info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [StructLayout(LayoutKind.Sequential)] private struct JobCpuRate { public uint ControlFlags; public uint CpuRate; }
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetInformationJobObject")]
    private static extern bool SetInformationJobObjectCpu(IntPtr job, int cls, ref JobCpuRate info, int size);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
}
