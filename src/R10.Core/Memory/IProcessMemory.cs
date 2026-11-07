using System.Buffers.Binary;

namespace R10.Core.Memory;

public sealed record MemoryRegion(uint BaseAddress, ulong Size, uint State, uint Protect, uint Type)
{
    public bool IsReadable => State == 0x1000 && (Protect & 0x100) == 0 && (Protect & 0xff) is 2 or 4 or 8 or 0x20 or 0x40 or 0x80;
}
// All members are called exclusively by the runtime's serial worker.
public interface IProcessMemory : IDisposable
{
    int ProcessId { get; }
    uint ModuleBase { get; }
    bool IsAlive { get; } // Query failures throw; they must never be interpreted as process exit.
    byte[] Read(uint address, int length); // Exact reads/writes only; failures and short I/O throw.
    void Write(uint address, ReadOnlySpan<byte> bytes);
    IReadOnlyList<MemoryRegion> QueryRegions();
}
public static class MemoryExtensions
{
    public static int ReadInt32(this IProcessMemory memory, uint address) => BinaryPrimitives.ReadInt32LittleEndian(memory.Read(address, 4));
    public static uint ReadUInt32(this IProcessMemory memory, uint address) => BinaryPrimitives.ReadUInt32LittleEndian(memory.Read(address, 4));
    public static void WriteInt32(this IProcessMemory memory, uint address, int value)
    {
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); memory.Write(address, bytes);
    }
    public static uint Add(uint address, int offset) => unchecked(address + (uint)offset);
}
