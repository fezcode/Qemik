using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Qemik.Core;

/// <summary>
/// A Windows job object that kills its processes when its handle closes. The handle
/// is only closed by <see cref="Dispose"/> or by Windows when the owning process ends,
/// so guests cannot outlive Qemik after End task, a crash or sign-out.
/// </summary>
public sealed class ChildProcessJob : IDisposable
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    private readonly SafeFileHandle? job;

    public ChildProcessJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        job = CreateJobObject(0, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create a job for guest processes.");
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = Marshal.GetLastWin32Error(); job.Dispose();
            throw new Win32Exception(error, "Windows could not configure the guest process job.");
        }
    }

    public void Add(Process process)
    {
        if (job is null) return;
        if (!AssignProcessToJobObject(job, process.SafeHandle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not tie the guest to Qemik's lifetime.");
    }

    public void Dispose() => job?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadTransfer, WriteTransfer, OtherTransfer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
