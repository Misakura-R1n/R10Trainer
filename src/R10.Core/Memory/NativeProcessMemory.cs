using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace R10.Core.Memory;

public sealed class NativeProcessMemory : IProcessMemory
{
    private nint _handle;
    public int ProcessId { get; }
    public uint ModuleBase { get; }
    public string ExecutablePath { get; }
    public bool IsAlive
    {
        get
        {
            EnsureOpen();
            uint result = WaitForSingleObject(_handle, 0);
            return result switch { 0 => false, 258 => true, _ => throw Error("查询进程存活状态") };
        }
    }
    public NativeProcessMemory(int processId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("仅支持 Windows。");
        ProcessId = processId;
        _handle = OpenProcess(0x100000 | 0x400 | 0x10 | 0x20 | 0x8, false, processId);
        if (_handle == 0) throw Error("打开游戏进程（请检查双方的权限级别）");
        try
        {
            if (!IsWow64Process2(_handle, out var processMachine, out var nativeMachine)) throw Error("检查进程架构");
            if (processMachine != 0x14c && !(processMachine == 0 && nativeMachine == 0x14c))
                throw new NotSupportedException("当前版本只支持 32 位 Rance10 游戏。");
            var path = new StringBuilder(32768); uint length = (uint)path.Capacity;
            if (!QueryFullProcessImageName(_handle, 0, path, ref length)) throw Error("读取游戏路径");
            ExecutablePath = path.ToString();
            using var process = Process.GetProcessById(processId);
            if (!GetProcessTimes(_handle, out long creation, out _, out _, out _)) throw Error("检查进程身份");
            if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != creation) throw new IOException("进程已被替换，请重新连接。");
            var module = process.MainModule ?? throw new IOException("未找到游戏主模块。");
            ModuleBase = unchecked((uint)module.BaseAddress.ToInt64());
            if (!IsAlive || ModuleBase == 0 || !string.Equals(module.FileName, ExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("连接期间游戏身份发生变化。");
        }
        catch { Dispose(); throw; }
    }
    public byte[] Read(uint address, int length)
    {
        EnsureOpen();
        if (length < 0 || length > 32 * 1024 * 1024 || (ulong)address + (uint)length > 0x1_0000_0000UL)
            throw new ArgumentOutOfRangeException(nameof(length));
        var bytes = new byte[length];
        if (length == 0) return bytes;
        if (!ReadProcessMemory(_handle, Pointer(address), bytes, (nuint)length, out var read) || read != (nuint)length)
            throw Error($"读取 0x{address:X8}（{length} 字节）");
        return bytes;
    }
    public void Write(uint address, ReadOnlySpan<byte> bytes)
    {
        EnsureOpen();
        if ((ulong)address + (uint)bytes.Length > 0x1_0000_0000UL) throw new ArgumentOutOfRangeException(nameof(address));
        if (bytes.Length == 0) return;
        var buffer = bytes.ToArray();
        if (!WriteProcessMemory(_handle, Pointer(address), buffer, (nuint)buffer.Length, out var written) || written != (nuint)buffer.Length)
            throw Error($"写入 0x{address:X8}（{buffer.Length} 字节）");
    }
    public IReadOnlyList<MemoryRegion> QueryRegions()
    {
        EnsureOpen(); var regions = new List<MemoryRegion>(); ulong address = 0;
        while (address < 0x1_0000_0000UL)
        {
            if (VirtualQueryEx(_handle, Pointer((uint)address), out var info, (nuint)Marshal.SizeOf<NativeRegion>()) == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == 87 && address >= 0x7fff0000UL) break;
                throw new Win32Exception(error, $"查询内存区域 0x{address:X8} 失败");
            }
            ulong start = (ulong)(nuint)info.BaseAddress, size = info.RegionSize;
            if (size == 0 || start > address || start + size <= address) throw new IOException("内存区域枚举未前进。");
            if (start >= 0x1_0000_0000UL) break;
            regions.Add(new((uint)start, Math.Min(size, 0x1_0000_0000UL - start), info.State, info.Protect, info.Type));
            address = start + size;
        }
        return regions.AsReadOnly();
    }
    public void Dispose()
    {
        if (_handle == 0) return;
        if (!CloseHandle(_handle)) throw Error("关闭游戏句柄");
        _handle = 0; GC.SuppressFinalize(this);
    }
    ~NativeProcessMemory() { if (_handle != 0) CloseHandle(_handle); }
    private void EnsureOpen() { if (_handle == 0) throw new ObjectDisposedException(nameof(NativeProcessMemory)); }
    private static nint Pointer(uint address) => IntPtr.Size == 4 ? new(unchecked((int)address)) : new((long)address);
    private static Win32Exception Error(string action) => new(Marshal.GetLastPInvokeError(), action + "失败");
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRegion { public nint BaseAddress, AllocationBase; public uint AllocationProtect; public nuint RegionSize; public uint State, Protect, Type; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWow64Process2(nint handle, out ushort processMachine, out ushort nativeMachine);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(nint handle, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(nint handle, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadProcessMemory(nint handle, nint address, [Out] byte[] buffer, nuint length, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WriteProcessMemory(nint handle, nint address, byte[] buffer, nuint length, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint VirtualQueryEx(nint handle, nint address, out NativeRegion info, nuint length);
}
