using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TVBoxForWindows.Engine;

/// <summary>Keeps child processes tied to the lifetime of this application process.</summary>
internal static class ProcessLifetimeJob
{
    const uint JobObjectLimitKillOnJobClose = 0x00002000;
    const int ExtendedLimitInformationClass = 9;

    static readonly object Sync = new();
    static SafeJobHandle _job;
    static string _setupError;
    static bool _setupAttempted;

    public static bool TryPrepare(out string error)
    {
        lock (Sync)
        {
            if (!_setupAttempted)
            {
                _setupAttempted = true;
                try
                {
                    _job = CreateKillOnCloseJob(out _setupError);
                }
                catch (Exception e)
                {
                    _setupError = $"Unable to create the process lifetime job: {e.Message}";
                }
            }

            error = _setupError;
            return _job is { IsInvalid: false, IsClosed: false };
        }
    }

    public static bool TryAssign(Process process, out string error)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!TryPrepare(out error)) return false;
        try
        {
            if (AssignProcessToJobObject(_job, process.SafeHandle))
            {
                error = null;
                return true;
            }

            error = Win32Error("AssignProcessToJobObject", Marshal.GetLastWin32Error());
            return false;
        }
        catch (Exception e)
        {
            error = $"AssignProcessToJobObject failed: {e.Message}";
            return false;
        }
    }

    static SafeJobHandle CreateKillOnCloseJob(out string error)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            error = Win32Error("CreateJobObject", Marshal.GetLastWin32Error());
            job.Dispose();
            return null;
        }

        var limits = new JobObjectExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(
                job,
                ExtendedLimitInformationClass,
                ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            error = Win32Error("SetInformationJobObject", Marshal.GetLastWin32Error());
            job.Dispose();
            return null;
        }

        error = null;
        return job;
    }

    static string Win32Error(string operation, int code) =>
        $"{operation} failed with Win32 error {code}: {new Win32Exception(code).Message}";

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        SafeJobHandle() : base(true) { }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int infoClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
}
