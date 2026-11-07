using System.Buffers.Binary;
using System.IO.Compression;
using R10.Core;
using R10.Core.Memory;
using R10.Core.Patching;

namespace R10.Tests;

internal static class BridgeAinTests
{
    internal static void Run(Action<string, Action> test, string path)
    {
        path = Path.GetFullPath(path);
        test("bridge AIN exact script identity survives recompression but rejects changed script", () =>
        {
            Require(GameConnection.ReadAinFingerprint(path) == GameConnection.SupportedBridgeAinFingerprint);
            Require(!GameConnection.IsSupportedAinFingerprint(GameConnection.ReadAinFingerprint(path, false), false));
            byte[] plain = ReadPlain(path);
            string temporary = Path.Combine(Path.GetTempPath(), $"r10-ain-identity-{Guid.NewGuid():N}.tmp");
            try
            {
                WriteAin(temporary, plain);
                Require(GameConnection.ReadAinFingerprint(temporary) == GameConnection.SupportedBridgeAinFingerprint);
                File.WriteAllBytes(temporary, plain);
                Require(!GameConnection.IsSupportedAinFingerprint(GameConnection.ReadAinFingerprint(temporary)));
                plain[100] ^= 1;
                WriteAin(temporary, plain);
                Require(!GameConnection.IsSupportedAinFingerprint(GameConnection.ReadAinFingerprint(temporary)));
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });
        test("bridge actual CODE locates both script features and restores every byte", () =>
        {
            using var memory = new CodeMemory(ReadCode(path));
            byte[] original = (byte[])memory.Image.Clone();
            var engine = new R10PatchEngine(memory, path);
            engine.SetRequested(FeatureId.Redraw, true);
            engine.SetRequested(FeatureId.Redistribute, true);
            for (int i = 0; i < 5000 && !engine.Observations.Values.All(value => value.Active); i++) engine.Maintain();
            Require(engine.Observations.Values.All(value => value.Active) && memory.Writes == 3 && engine.HasRecoveryDebt);
            engine.RestoreAll();
            Require(!engine.HasRecoveryDebt && memory.Writes == 6 && memory.Image.AsSpan().SequenceEqual(original));
        });
        test("bridge CODE recovery retains debt while appended tail still exists", () =>
        {
            using var memory = new CodeMemory(ReadCode(path));
            var engine = new R10PatchEngine(memory, path);
            engine.SetRequested(FeatureId.Redistribute, true);
            for (int i = 0; i < 5000 && !engine.Observations[FeatureId.Redistribute].Active; i++) engine.Maintain();
            Require(engine.HasRecoveryDebt && memory.Writes == 1);
            memory.Regions = [new(memory.CodeBase, BugFixProfile.CodeLength, 0x10000, 0, 0),
                new(memory.CodeBase + BugFixProfile.CodeLength, GameConnection.SupportedBridgeCodeLength - BugFixProfile.CodeLength, 0x2000, 0, 0)];
            bool failed = false;
            try { engine.RestoreAll(); } catch (InvalidOperationException) { failed = true; }
            Require(failed && engine.HasRecoveryDebt && memory.Writes == 1);
            memory.Regions = [new(memory.CodeBase, GameConnection.SupportedBridgeCodeLength, 0x10000, 0, 0)];
            engine.RestoreAll();
            Require(!engine.HasRecoveryDebt && memory.Writes == 1);
        });
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Bridge AIN fixture assertion failed"); }
    private static byte[] ReadPlain(string path)
    {
        using var source = File.OpenRead(path); source.Position = 16;
        using var compressed = new ZLibStream(source, CompressionMode.Decompress);
        using var plain = new MemoryStream(); compressed.CopyTo(plain);
        return plain.ToArray();
    }
    private static byte[] ReadCode(string path)
    {
        byte[] plain = ReadPlain(path);
        Require(plain.AsSpan(8, 4).SequenceEqual("CODE"u8));
        int length = BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(12, 4));
        Require(length == GameConnection.SupportedBridgeCodeLength);
        return plain.AsSpan(16, length).ToArray();
    }
    private static void WriteAin(string path, byte[] plain)
    {
        using var payload = new MemoryStream();
        using (var zipped = new ZLibStream(payload, CompressionLevel.Fastest, true)) zipped.Write(plain);
        using var file = File.Create(path);
        file.Write("AI2\0\0\0\0\0"u8);
        Span<byte> lengths = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(lengths, plain.Length);
        BinaryPrimitives.WriteInt32LittleEndian(lengths[4..], checked((int)payload.Length));
        file.Write(lengths); payload.Position = 0; payload.CopyTo(file);
    }
    private sealed class CodeMemory(byte[] image) : IProcessMemory
    {
        internal byte[] Image { get; } = image;
        internal uint CodeBase => 0x10000000;
        internal int Writes;
        internal MemoryRegion[] Regions = [new(0x10000000, checked((ulong)image.Length), 0x1000, 4, 0x20000)];
        public int ProcessId => 42;
        public uint ModuleBase => 0x400000;
        public bool IsAlive => true;
        public byte[] Read(uint address, int length)
        {
            if (!Regions.Any(region => region.IsReadable && address >= region.BaseAddress && (ulong)address + (uint)length <= region.BaseAddress + region.Size))
                throw new IOException("fixture mapping is not readable");
            return Image.AsSpan(checked((int)(address - CodeBase)), length).ToArray();
        }
        public void Write(uint address, ReadOnlySpan<byte> bytes) { Writes++; bytes.CopyTo(Image.AsSpan(checked((int)(address - CodeBase)))); }
        public IReadOnlyList<MemoryRegion> QueryRegions() => Regions;
        public void Dispose() { }
    }
}
