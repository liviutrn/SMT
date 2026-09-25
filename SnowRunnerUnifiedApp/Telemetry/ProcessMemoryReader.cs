using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SnowRunnerTelemetry;

internal sealed class ProcessMemoryReader : IDisposable
{
    private const int ModuleReadBlockSize = 0x10000;

    private ProcessMemoryReader(Process process, nint handle, nint moduleBase, int moduleSize, string modulePath)
    {
        Process = process;
        Handle = handle;
        ModuleBase = moduleBase;
        ModuleSize = moduleSize;
        ModulePath = modulePath;
        VersionText = TryGetVersionText(modulePath);
    }

    public Process Process { get; }
    public nint Handle { get; private set; }
    public nint ModuleBase { get; }
    public int ModuleSize { get; }
    public string ModulePath { get; }
    public string VersionText { get; }

    public static bool TryAttach(out ProcessMemoryReader? reader, out string message)
    {
        reader = null;

        var candidates = FindSnowRunnerCandidates().ToArray();
        if (candidates.Length == 0)
        {
            message = "No SnowRunner process found. Start the game; this tool will attach automatically.";
            return false;
        }

        var failures = new List<string>();

        foreach (var process in candidates)
        {
            ProcessModule? module;
            try
            {
                module = process.MainModule;
            }
            catch (Win32Exception ex)
            {
                failures.Add($"{ProcessLabel(process)}: cannot inspect module ({ex.Message})");
                continue;
            }
            catch (InvalidOperationException ex)
            {
                failures.Add($"{ProcessLabel(process)}: cannot inspect module ({ex.Message})");
                continue;
            }

            if (module is null)
            {
                failures.Add($"{ProcessLabel(process)}: module information unavailable");
                continue;
            }

            if (!IsSnowRunnerExecutable(module.FileName))
            {
                failures.Add($"{ProcessLabel(process)}: ignored because it is not SnowRunner.exe");
                continue;
            }

            var handle = NativeMethods.OpenProcess(
                ProcessAccess.QueryLimitedInformation | ProcessAccess.QueryInformation | ProcessAccess.VirtualMemoryRead,
                inheritHandle: false,
                process.Id);

            if (handle == 0)
            {
                failures.Add($"{ProcessLabel(process)}: read-only attach failed ({Marshal.GetLastWin32Error()})");
                continue;
            }

            reader = new ProcessMemoryReader(process, handle, module.BaseAddress, module.ModuleMemorySize, module.FileName);
            message = $"Attached to {Path.GetFileName(module.FileName)} pid {process.Id}.";
            return true;
        }

        message = failures.Count == 0
            ? "SnowRunner-like process found, but no readable game process could be opened."
            : "Could not attach read-only. " + string.Join(" | ", failures.Take(3)) + " If SnowRunner is running as Administrator, run this tool as Administrator too.";
        return false;
    }

    public static string DescribeSnowRunnerProcesses()
    {
        var candidates = FindSnowRunnerCandidates().ToArray();
        if (candidates.Length == 0)
        {
            return "Detected processes: none with SnowRunner in name or window title.";
        }

        var builder = new StringBuilder("Detected processes: ");
        foreach (var process in candidates.Take(8))
        {
            builder.Append(ProcessLabel(process));
            builder.Append("; ");
        }

        return builder.ToString().TrimEnd(' ', ';');
    }

    private static IEnumerable<Process> FindSnowRunnerCandidates()
    {
        var currentPid = Environment.ProcessId;
        return Process.GetProcesses()
            .Where(p => p is not null && p.Id != currentPid && IsSnowRunnerLike(p))
            .OrderByDescending(p => string.Equals(SafeProcessName(p), "SnowRunner", StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Id);
    }

    private static bool IsSnowRunnerLike(Process process)
    {
        var name = SafeProcessName(process);
        return IsSnowRunnerProcessName(name);
    }

    private static bool IsSnowRunnerProcessName(string processName)
    {
        return string.Equals(processName, "SnowRunner", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSnowRunnerExecutable(string modulePath)
    {
        return string.Equals(Path.GetFileName(modulePath), "SnowRunner.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string ProcessLabel(Process process)
    {
        var title = SafeWindowTitle(process);
        return string.IsNullOrWhiteSpace(title)
            ? $"{SafeProcessName(process)} pid {process.Id}"
            : $"{SafeProcessName(process)} pid {process.Id} \"{title}\"";
    }

    private static string SafeProcessName(Process process)
    {
        try
        {
            return process.ProcessName ?? "";
        }
        catch
        {
            return "<unknown>";
        }
    }

    private static string SafeWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle ?? "";
        }
        catch
        {
            return "";
        }
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
        if (IntPtr.Size == 8)
        {
            if (TryRead<ulong>(address, out var raw))
            {
                pointer = unchecked((nint)(long)raw);
                return pointer != 0;
            }
        }
        else if (TryRead<uint>(address, out var raw))
        {
            pointer = (nint)raw;
            return pointer != 0;
        }

        pointer = 0;
        return false;
    }

    public bool TryReadBytes(nint address, byte[] buffer)
    {
        if (Handle == 0 || buffer.Length == 0)
        {
            return false;
        }

        var ok = NativeMethods.ReadProcessMemory(Handle, address, buffer, (nuint)buffer.Length, out var bytesRead);
        return ok && bytesRead == (nuint)buffer.Length;
    }

    public byte[] ReadRangeLossy(nint address, int length)
    {
        var output = new byte[length];

        for (var offset = 0; offset < length; offset += ModuleReadBlockSize)
        {
            var count = Math.Min(ModuleReadBlockSize, length - offset);
            var block = new byte[count];
            if (TryReadBytes(address + offset, block))
            {
                Buffer.BlockCopy(block, 0, output, offset, count);
            }
        }

        return output;
    }

    public bool IsReadable(nint address, int bytes = 1)
    {
        if (address == 0 || bytes <= 0)
        {
            return false;
        }

        var result = NativeMethods.VirtualQueryEx(
            Handle,
            address,
            out var info,
            (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>());

        if (result == 0 || info.State != NativeMethods.MemCommit)
        {
            return false;
        }

        if ((info.Protect & NativeMethods.PageNoAccess) != 0 || (info.Protect & NativeMethods.PageGuard) != 0)
        {
            return false;
        }

        var regionEnd = (long)info.BaseAddress + (long)info.RegionSize;
        return (long)address + bytes <= regionEnd;
    }

    public IEnumerable<MemoryRegion> EnumerateReadableRegions()
    {
        var address = 0x10000L;
        const long userModeLimit = 0x0000800000000000L;
        var mbiSize = (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();

        while (address > 0 && address < userModeLimit)
        {
            var result = NativeMethods.VirtualQueryEx(Handle, (nint)address, out var info, mbiSize);
            if (result == 0)
            {
                address += 0x10000;
                continue;
            }

            var baseAddress = (long)info.BaseAddress;
            var regionSize = (long)info.RegionSize;
            if (regionSize <= 0)
            {
                address += 0x10000;
                continue;
            }

            if (info.State == NativeMethods.MemCommit && IsReadableProtection(info.Protect))
            {
                yield return new MemoryRegion(info.BaseAddress, regionSize, info.Protect, info.Type);
            }

            var next = baseAddress + regionSize;
            if (next <= address)
            {
                break;
            }

            address = next;
        }
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            NativeMethods.CloseHandle(Handle);
            Handle = 0;
        }

        Process.Dispose();
    }

    private static string TryGetVersionText(string modulePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(modulePath);
            return string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion ?? "" : info.ProductVersion;
        }
        catch
        {
            return "";
        }
    }

    private static bool IsReadableProtection(uint protect)
    {
        return (protect & NativeMethods.PageNoAccess) == 0 && (protect & NativeMethods.PageGuard) == 0;
    }
}

internal sealed record MemoryRegion(nint BaseAddress, long Size, uint Protect, uint Type);
