using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NTMiner.Modern;

/// <summary>Agent termination closes the job, terminating the miner and descendants.</summary>
public sealed class WindowsJob : IProcessContainment
{
    private readonly SafeFileHandle _handle;
    public WindowsJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new ExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = 0x00002000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        int size = Marshal.SizeOf<ExtendedLimitInformation>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, memory, false);
            if (!SetInformationJobObject(_handle, 9, memory, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { _handle.Dispose(); throw; }
        finally { Marshal.FreeHGlobal(memory); }
    }
    public void Attach(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法建立内核进程保护，已取消启动。");
    }
    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
