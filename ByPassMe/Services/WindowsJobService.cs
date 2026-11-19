using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ByPassMe.Services;

/// <summary>
/// Дочерние core-процессы завершаются вместе с ByPassMe (v2rayN WindowsJobService).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsJobService : IDisposable
{
    private nint _handle;
    private bool _disposed;

    public WindowsJobService()
    {
        _handle = CreateJobObject(nint.Zero, null);
        var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = 0x2000 };
        var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = info,
        };

        nint extendedInfoPtr = nint.Zero;
        try
        {
            var length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            extendedInfoPtr = Marshal.AllocHGlobal(length);
            Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);
            if (!SetInformationJobObject(_handle, JobObjectInfoType.ExtendedLimitInformation,
                    extendedInfoPtr, (uint)length))
            {
                throw new InvalidOperationException(
                    $"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            if (extendedInfoPtr != nint.Zero)
                Marshal.FreeHGlobal(extendedInfoPtr);
        }
    }

    public bool AddProcess(Process process)
    {
        if (_disposed || process.HasExited) return false;
        try
        {
            return AssignProcessToJobObject(_handle, process.Handle);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != nint.Zero)
        {
            CloseHandle(_handle);
            _handle = nint.Zero;
        }
        GC.SuppressFinalize(this);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObject(nint a, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        nint hJob, JobObjectInfoType infoType, nint lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
{
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
    public IO_COUNTERS IoInfo;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IO_COUNTERS
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

internal enum JobObjectInfoType
{
    ExtendedLimitInformation = 9,
}

[SupportedOSPlatform("windows")]
internal static class CoreProcessJob
{
    private static WindowsJobService? _job;

    public static void Assign(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            _job ??= new WindowsJobService();
            _job.AddProcess(process);
        }
        catch
        {
            /* ignore */
        }
    }
}
