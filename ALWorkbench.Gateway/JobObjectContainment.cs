using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ALWorkbench.Gateway.Core.PowerShell;
using Microsoft.Win32.SafeHandles;

namespace ALWorkbench.Gateway;

/// <summary>
/// <see cref="IProcessContainment"/> with a Windows job object per child, set to kill
/// every process in it when its last handle closes. A process the child starts joins
/// the job too, so a grandchild cannot outlive the command even after its parent
/// exited, and closing the handle (or the gateway ending) takes everything with it.
///
/// <para>The child is assigned right after it starts, so in principle it could start
/// something in that instant before joining; PowerShell spends far longer than that
/// loading before it runs any script.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class JobObjectContainment : IProcessContainment
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    public IDisposable Contain(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a job object for the PowerShell process.");
        try
        {
            var limits = new JobObjectExtendedLimit
            {
                BasicLimitInformation = new JobObjectBasicLimit { LimitFlags = JobObjectLimitKillOnJobClose },
            };
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimit>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not set the job object to end its processes.");
            }
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not put the PowerShell process in its job object.");
            }
            return job;
        }
        catch
        {
            // Closing the job kills the child, which never ran unconstrained.
            job.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimit
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimit
    {
        public JobObjectBasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobObjectExtendedLimit info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
