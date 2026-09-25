using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace SnowRunnerWheelspinFinder;

[Flags]
internal enum ProcessAccess : uint
{
    QueryInformation = 0x0400,
    QueryLimitedInformation = 0x1000,
    VirtualMemoryRead = 0x0010
}

internal static class NativeMethods
{
    internal const uint MemCommit = 0x1000;
    internal const uint MemPrivate = 0x20000;
    internal const uint MemMapped = 0x40000;
    internal const uint PageNoAccess = 0x01;
    internal const uint PageGuard = 0x100;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(
        ProcessAccess desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(
        SafeProcessHandle process,
        nint baseAddress,
        [Out] byte[] buffer,
        nuint size,
        out nuint bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nuint VirtualQueryEx(
        SafeProcessHandle process,
        nint baseAddress,
        out MemoryBasicInformation buffer,
        nuint length);

    [DllImport("winmm.dll")]
    internal static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    internal static extern uint timeEndPeriod(uint period);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}
