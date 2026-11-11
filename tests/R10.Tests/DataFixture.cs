using System.Buffers.Binary;
using System.Collections.Concurrent;
using R10.Core.Data;
using R10.Core.Memory;

namespace R10.Tests;

internal sealed class DataFixture
{
    public DataFakeMemory Memory { get; } = new();
    public R10DataEngine Engine { get; }
    public uint Offsets = 0x10040000, Turn = 0x20000000, Resources = 0x20001000, Party = 0x20002000,
        Campaign = 0x20004000, Enemy = 0x20005000, CharacterArray = 0x20009000,
        Character1 = 0x20010000, Character2 = 0x20011000, CommonStatus = 0x20012000;
    public uint CardContainer => Descriptor(0x120);
    public const uint PointerBase = 0x10010000;
    public DataFixture()
    {
        Memory.Put(Memory.ModuleBase + 0x3FCBB0, 0x10000000);
        Memory.Put(0x10000008, PointerBase); Memory.Put(PointerBase, 0x10020000);
        Memory.Put(0x10020000, 0x10030000); Memory.Put(0x10030010, Offsets);
        SetTable(0x3F8, 0x100, Turn); SetTable(0x400, 0x104, Resources);
        SetTable(0x410, 0x12C, Party); Bind(0x108, 0x20003000); Bind(0x130, CommonStatus);
        SetTable(0x500, 0x10C, Campaign); SetTable(0x48C, 0x110, Enemy);
        SetTable(0x490, 0x114, 0x20006000); Bind(0x118, 0x20007000);
        Memory.Put(0x20006000 + 13 * 4, 0x118); Memory.Put(0x20007000, 1);
        Memory.Put(Turn + 8, 0); Memory.Put(Turn + 12, 1);
        Memory.Put(Party, 0x108); Memory.Put(Party + 0x10, 500); Memory.Put(Party + 0x34, 2);
        Memory.Put(0x20003000, 1000); Memory.Put(0x20003004, 0x130); Memory.Put(CommonStatus + 0x34, 0);
        Memory.Put(Enemy, 500); Memory.Put(Enemy + 0x28, 500);
        Memory.Put(Resources, 2); Memory.Put(Resources + 4, 1); Memory.Put(Resources + 8, 2); Memory.Put(Resources + 0x20, 35);
        Memory.Put(Offsets + 0x560, 8);
        Memory.Put(Campaign + 0x94, 125); Memory.Put(Campaign + 0x90, 12); Memory.Put(Campaign + 0x88, 37500); Memory.Put(Campaign + 0x84, 262500); Memory.Put(Campaign + 0x8C, 123456);
        for (uint i = 0; i < 4; i++) { Memory.Put(Campaign + 0x64 + i * 4, checked((int)((i + 1) * 100))); Memory.Put(Campaign + 0xBC + i * 4, 0); }
        SetTable(0x404, 0x11C, 0x20008000); Memory.Put(0x20008000, 0x120);
        Bind(0x120, CharacterArray); Memory.Put(CardContainer + 0x14, 8);
        Memory.Put(CharacterArray, 0x124); Memory.Put(CharacterArray + 4, 0x128);
        Bind(0x124, Character1); Bind(0x128, Character2);
        foreach (uint character in new[] { Character1, Character2 })
        { Memory.Put(character + 4, 20); Memory.Put(character + 8, 100); Memory.Put(character + 12, 300); }
        Engine = new R10DataEngine(Memory);
    }
    public void InjectDeathRateRollbackConflict(int conflictedValue)
    {
        Memory.BeforeWrite = (address, _) =>
        {
            if (address != Campaign + 0x90) return;
            Memory.BeforeWrite = null;
            Memory.Put(Campaign + 0x94, conflictedValue);
            throw new IOException("runtime rollback conflict");
        };
    }
    private void SetTable(uint table, int token, uint owner) { Bind(token, owner); Memory.Put(Offsets + table, token); }
    private static uint Descriptor(int token) => checked(0x12000000u + (uint)token * 0x40);
    public void Bind(int token, uint owner)
    {
        uint remainder = (uint)(token & 3), aligned = (uint)token - remainder;
        uint block = 0x11000000u + aligned * 0x40;
        Memory.Put(PointerBase + aligned, block); Memory.Put(block + remainder * 4, Descriptor(token)); Memory.Put(Descriptor(token) + 0x10, owner);
    }
    public void Copy(uint source, uint destination, int length)
    { for (uint i = 0; i < length; i++) Memory.PutBytes(destination + i, [Memory.ByteOrZero(source + i)]); }
}

internal sealed class DataFakeMemory : IProcessMemory
{
    private readonly ConcurrentDictionary<uint, byte> _bytes = [];
    public int ProcessId => 1234;
    public uint ModuleBase => 0x400000;
    public bool IsAlive => true;
    public Action<uint, int>? BeforeRead { get; set; }
    public Action<uint, byte[]>? BeforeWrite { get; set; }
    public ConcurrentQueue<(uint Address, byte[] Bytes)> Writes { get; } = [];
    public byte[] Read(uint address, int length)
    {
        BeforeRead?.Invoke(address, length);
        var result = new byte[length];
        for (int i = 0; i < length; i++) if (!_bytes.TryGetValue(address + (uint)i, out result[i])) throw new IOException($"unmapped 0x{address + i:X}");
        return result;
    }
    public void Write(uint address, ReadOnlySpan<byte> bytes)
    {
        byte[] copy = bytes.ToArray(); Writes.Enqueue((address, copy)); BeforeWrite?.Invoke(address, copy); PutBytes(address, copy);
    }
    public int Int(uint address) => BinaryPrimitives.ReadInt32LittleEndian(Read(address, 4));
    public void Put(uint address, int value) { var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); PutBytes(address, bytes); }
    public void Put(uint address, uint value) => Put(address, unchecked((int)value));
    public void PutBytes(uint address, ReadOnlySpan<byte> bytes) { for (int i = 0; i < bytes.Length; i++) _bytes[address + (uint)i] = bytes[i]; }
    public byte ByteOrZero(uint address) => _bytes.GetValueOrDefault(address);
    public void Remove(uint address) => _bytes.TryRemove(address, out _);
    public IReadOnlyList<MemoryRegion> QueryRegions() => [];
    public void Dispose() { }
}
