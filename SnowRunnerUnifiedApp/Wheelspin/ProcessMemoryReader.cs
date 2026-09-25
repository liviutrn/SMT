using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SnowRunnerWheelspinFinder;

internal sealed class ProcessMemoryReader : IDisposable
{
    private ProcessMemoryReader(Process process, SafeProcessHandle handle, nint moduleBase, int moduleSize, string modulePath)
    {
        Process = process;
        Handle = handle;
        ModuleBase = moduleBase;
        ModuleSize = moduleSize;
        ModulePath = modulePath;
        VersionText = GetVersionText(modulePath);
        ProcessStartTimeUtc = GetStartTimeUtc(process);
    }

    public Process Process { get; }
    public SafeProcessHandle Handle { get; }
    public nint ModuleBase { get; }
    public int ModuleSize { get; }
    public string ModulePath { get; }
    public string VersionText { get; }
    public DateTime ProcessStartTimeUtc { get; }
    public bool IsAlive
    {
        get
        {
            try
            {
                return !Handle.IsClosed && !Handle.IsInvalid && !Process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool TryAttach(out ProcessMemoryReader? reader, out string message)
    {
        reader = null;
        var candidates = Process.GetProcessesByName("SnowRunner").OrderBy(process => process.Id).ToArray();
        if (candidates.Length == 0)
        {
            message = "SnowRunner is not running. Waiting for SnowRunner.exe.";
            return false;
        }

        var failures = new List<string>();
        foreach (var process in candidates)
        {
            try
            {
                var module = process.MainModule;
                if (module is null || !string.Equals(Path.GetFileName(module.FileName), "SnowRunner.exe", StringComparison.OrdinalIgnoreCase))
                {
                    process.Dispose();
                    continue;
                }

                var handle = NativeMethods.OpenProcess(
                    ProcessAccess.QueryInformation | ProcessAccess.QueryLimitedInformation | ProcessAccess.VirtualMemoryRead,
                    inheritHandle: false,
                    process.Id);
                if (handle.IsInvalid)
                {
                    failures.Add($"pid {process.Id}: read-only attach failed ({Marshal.GetLastWin32Error()})");
                    handle.Dispose();
                    process.Dispose();
                    continue;
                }

                reader = new ProcessMemoryReader(process, handle, module.BaseAddress, module.ModuleMemorySize, module.FileName);
                message = $"Attached read-only to SnowRunner.exe, pid {process.Id}.";
                return true;
            }
            catch (Win32Exception ex)
            {
                failures.Add($"pid {process.Id}: {ex.Message}");
                process.Dispose();
            }
            catch (InvalidOperationException ex)
            {
                failures.Add($"pid {process.Id}: {ex.Message}");
                process.Dispose();
            }
        }

        message = failures.Count == 0
            ? "SnowRunner.exe was found but could not be inspected."
            : "Could not attach read-only. " + string.Join(" | ", failures.Take(3));
        return false;
    }

    public bool TryRead<T>(nint address, out T value) where T : unmanaged
    {
        var buffer = new byte[Marshal.SizeOf<T>()];
        if (!TryReadBytes(address, buffer))
        {
            value = default;
            return false;
        }

        value = MemoryMarshal.Read<T>(buffer);
        return true;
    }

    public bool TryReadPointer(nint address, out nint pointer)
    {
        if (TryRead<ulong>(address, out var raw) && IsCanonicalUserPointer(raw))
        {
            pointer = unchecked((nint)(long)raw);
            return true;
        }

        pointer = 0;
        return false;
    }

    public bool TryReadBytes(nint address, byte[] buffer)
    {
        if (buffer.Length == 0 || !IsAlive || !ValidateRange(address, buffer.Length))
        {
            return false;
        }

        return NativeMethods.ReadProcessMemory(Handle, address, buffer, (nuint)buffer.Length, out var bytesRead) &&
               bytesRead == (nuint)buffer.Length;
    }

    public byte[]? TryReadBlock(nint address, int size)
    {
        if (size <= 0)
        {
            return null;
        }

        var buffer = new byte[size];
        return TryReadBytes(address, buffer) ? buffer : null;
    }

    public bool ValidateRange(nint address, int size)
    {
        if (size <= 0 || !IsCanonicalUserPointer(unchecked((ulong)(long)address)))
        {
            return false;
        }

        var current = (long)address;
        long end;
        try
        {
            end = checked(current + size);
        }
        catch (OverflowException)
        {
            return false;
        }

        var infoSize = (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();
        while (current < end)
        {
            if (NativeMethods.VirtualQueryEx(Handle, (nint)current, out var info, infoSize) == 0 ||
                info.State != NativeMethods.MemCommit || !IsReadableProtection(info.Protect))
            {
                return false;
            }

            var regionStart = (long)info.BaseAddress;
            var regionEnd = regionStart + (long)info.RegionSize;
            if (current < regionStart || regionEnd <= current)
            {
                return false;
            }

            current = Math.Min(end, regionEnd);
        }

        return true;
    }

    public IEnumerable<MemoryRegion> EnumerateReadableRegions()
    {
        const long userLimit = 0x0000800000000000L;
        var address = 0x10000L;
        var infoSize = (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();
        while (address > 0 && address < userLimit && IsAlive)
        {
            if (NativeMethods.VirtualQueryEx(Handle, (nint)address, out var info, infoSize) == 0)
            {
                address += 0x10000;
                continue;
            }

            var start = (long)info.BaseAddress;
            var size = (long)info.RegionSize;
            if (size <= 0)
            {
                address += 0x10000;
                continue;
            }

            if (info.State == NativeMethods.MemCommit && IsReadableProtection(info.Protect))
            {
                yield return new MemoryRegion(info.BaseAddress, size, info.Protect, info.Type);
            }

            var next = start + size;
            if (next <= address)
            {
                yield break;
            }

            address = next;
        }
    }

    public byte[] ReadModule()
    {
        const int blockSize = 0x10000;
        var output = new byte[ModuleSize];
        for (var offset = 0; offset < ModuleSize; offset += blockSize)
        {
            var count = Math.Min(blockSize, ModuleSize - offset);
            var block = new byte[count];
            if (TryReadBytes(ModuleBase + offset, block))
            {
                Buffer.BlockCopy(block, 0, output, offset, count);
            }
        }

        return output;
    }

    public string ModuleFingerprint(byte[] module)
    {
        var sampleLength = Math.Min(module.Length, 4 * 1024 * 1024);
        var hash = SHA256.HashData(module.AsSpan(0, sampleLength));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    public void Dispose()
    {
        Handle.Dispose();
        Process.Dispose();
    }

    public static string DescribeProcesses()
    {
        var processes = Process.GetProcessesByName("SnowRunner");
        try
        {
            return processes.Length == 0
                ? "No SnowRunner process detected."
                : string.Join(", ", processes.Select(process => $"SnowRunner pid {process.Id}"));
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool IsReadableProtection(uint protection)
    {
        return (protection & NativeMethods.PageNoAccess) == 0 && (protection & NativeMethods.PageGuard) == 0;
    }

    private static bool IsCanonicalUserPointer(ulong value)
    {
        return value >= 0x10000 && value < 0x0000800000000000UL;
    }

    private static string GetVersionText(string modulePath)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(modulePath);
            return string.IsNullOrWhiteSpace(version.ProductVersion) ? version.FileVersion ?? "unknown" : version.ProductVersion;
        }
        catch
        {
            return "unknown";
        }
    }

    private static DateTime GetStartTimeUtc(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}

internal sealed record MemoryRegion(nint BaseAddress, long Size, uint Protection, uint Type);
