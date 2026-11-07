using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using R10.Core;
using R10.Core.Data;
using R10.Core.Memory;

namespace R10.Tests;

public static class DataTests
{
    public static void Run(Action<string, Action> test)
    {
        test("data: valid prologue, mixed armies and honest observations", () =>
        {
            var f = new DataFixture();
            var scene = f.Engine.Refresh();
            Check(scene.GameplayReady && scene.BattleReady && scene.DeathRateWritable, "Valid turn zero rejected");
            var values = f.Engine.ReadValues();
            Check(values[FeatureId.Army] is null && values[FeatureId.Star] is null, "Mixed/batch data presented as a single value");
            Check(values[FeatureId.DeathRate] == 12.5m && values[FeatureId.Food] == 2, "Observed units wrong");
            Check(f.Memory.Writes.Count == 0, "Observation wrote memory");
        });
        test("data: scalar routes and input boundaries", () =>
        {
            var f = new DataFixture(); f.Engine.Refresh();
            f.Engine.WriteValue(FeatureId.Difficulty, -3); Check(f.Memory.Int(f.Offsets + 0x560) == -3, "Difficulty route");
            f.Engine.WriteValue(FeatureId.Turn, 12); Check(f.Memory.Int(f.Turn + 8) == 12, "Turn route");
            f.Engine.WriteValue(FeatureId.Food, 3); f.Engine.WriteValue(FeatureId.Medal, 2); f.Engine.WriteValue(FeatureId.Friendship, 1);
            Check(f.Memory.Int(f.Resources) == 3 && f.Memory.Int(f.Resources + 4) == 2 && f.Memory.Int(f.Resources + 8) == 1, "Resources routes");
            f.Engine.WriteValue(FeatureId.Unit, 999); Check(f.Memory.Int(f.Resources + 0x20) == 999, "Unit route");
            int written = f.Memory.Writes.Count;
            Expect<ArgumentOutOfRangeException>(() => f.Engine.WriteValue(FeatureId.Food, 4));
            Expect<ArgumentOutOfRangeException>(() => f.Engine.WriteValue(FeatureId.DeathRate, 1.25m));
            Expect<ArgumentOutOfRangeException>(() => f.Engine.WriteValue(FeatureId.Star, 100));
            Check(written == f.Memory.Writes.Count, "Invalid input wrote memory");
        });
        test("data: continuous routes and enemy near-death does not revive", () =>
        {
            var f = new DataFixture(); f.Engine.Refresh();
            f.Engine.MaintainToggle(FeatureId.Hp); Check(f.Memory.Int(f.Party + 0x10) == 1000, "HP target");
            f.Engine.MaintainToggle(FeatureId.EnemyNear); Check(f.Memory.Int(f.Enemy) == 1, "Near-death target");
            f.Engine.MaintainToggle(FeatureId.EnemyKill); Check(f.Memory.Int(f.Enemy) == 0, "Kill target");
            f.Engine.MaintainToggle(FeatureId.EnemyNear); Check(f.Memory.Int(f.Enemy) == 0, "Revived dead enemy");
            f.Engine.MaintainToggle(FeatureId.Resources);
            Check(new[] { 0u, 4u, 8u }.All(x => f.Memory.Int(f.Resources + x) == 3), "Resource maximum");
            f.Engine.MaintainToggle(FeatureId.War);
            Check(new[] { 0xBCu, 0xC0u, 0xC4u, 0xC8u }.All(x => f.Memory.Int(f.Campaign + x) == 3), "War maximum");
            f.Engine.MaintainToggle(FeatureId.Treasure); Check(f.Memory.Int(f.Party + 0x34) == 100, "Treasure bonus");
        });
        test("data: death-rate exact write set and same-R derived repair", () =>
        {
            var f = new DataFixture(); f.Engine.Refresh();
            f.Engine.WriteValue(FeatureId.DeathRate, 25m);
            Check(f.Memory.Writes.Select(x => x.Address).SequenceEqual(new[] { f.Campaign + 0x94, f.Campaign + 0x90, f.Campaign + 0x88, f.Campaign + 0x84 }), "Unexpected death write set/order");
            Check(f.Memory.Int(f.Campaign + 0x94) == 250 && f.Memory.Int(f.Campaign + 0x90) == 25 && f.Memory.Int(f.Campaign + 0x88) == 75000 && f.Memory.Int(f.Campaign + 0x84) == 225000, "Death formula");
            Check(f.Memory.Int(f.Campaign + 0x8C) == 123456, "Stage cache S changed");
            f.Memory.Put(f.Campaign + 0x90, 999); f.Memory.Writes.Clear();
            f.Engine.WriteValue(FeatureId.DeathRate, 25m);
            Check(f.Memory.Int(f.Campaign + 0x90) == 25 && f.Memory.Writes.Count == 1, "Same R skipped derived repair");
        });
        test("data: phase 16 pauses death modification only", () =>
        {
            var f = new DataFixture(); f.Engine.Refresh();
            var locked = f.Engine.CaptureLock(FeatureId.DeathRate);
            f.Memory.Put(f.Turn + 12, 16);
            var scene = f.Engine.Refresh();
            Check(scene.GameplayReady && !scene.DeathRateWritable, "Phase 16 readiness");
            Expect<InvalidOperationException>(() => f.Engine.MaintainLock(locked));
            Expect<InvalidOperationException>(() => f.Engine.WriteValue(FeatureId.DeathRate, 50));
            Check(f.Memory.Writes.Count == 0, "Phase 16 wrote death data");
            f.Engine.WriteValue(FeatureId.Medal, 3);
            f.Memory.Put(f.Turn + 12, 1); f.Memory.Put(f.Campaign + 0x94, 200);
            Check(f.Engine.MaintainLock(locked), "Phase pause discarded lock");
            Check(f.Memory.Int(f.Campaign + 0x94) == 125, "Original target not preserved across phase pause");
        });
        test("data: army locks retain four independent values", () =>
        {
            var f = new DataFixture(); f.Engine.Refresh(); var locked = f.Engine.CaptureLock(FeatureId.Army);
            f.Engine.WriteValue(FeatureId.Army, 900);
            Check(f.Engine.MaintainLock(locked), "Army lock lost");
            Check(f.Engine.ReadArmies().SequenceEqual(new[] { 100, 200, 300, 400 }), "Four values collapsed");
        });
        test("data: same-table owner replacement prevents stale lock writes", () =>
        {
            var f = new DataFixture(); f.Engine.Refresh(); var locked = f.Engine.CaptureLock(FeatureId.Army);
            uint replacement = 0x21004000; f.Copy(f.Campaign, replacement, 0xD0); f.Bind(0x10C, replacement);
            f.Memory.Put(replacement + 0x64, 777);
            Check(!f.Engine.MaintainLock(locked), "Stale owner accepted");
            Check(f.Memory.Int(replacement + 0x64) == 777 && f.Memory.Writes.Count == 0, "New owner overwritten");
            var recaptured = f.Engine.CaptureLock(FeatureId.Army);
            Check(recaptured.Owner == replacement && recaptured.Values[0] == 777, "New owner/value not captured");
        });
        test("data: changing scene between UI observation and command is rejected", () =>
        {
            var f = new DataFixture(); f.Engine.Refresh();
            uint replacement = 0x21000000; f.Copy(f.Turn, replacement, 16); f.Bind(0x100, replacement);
            Expect<InvalidOperationException>(() => f.Engine.WriteValue(FeatureId.Turn, 99));
            Check(f.Memory.Writes.Count == 0, "Stale UI authority wrote a new scene");
        });
        test("data: partial native write restores attempted fields", () =>
        {
            var f = new DataFixture(); f.Engine.Refresh(); bool injected = false;
            f.Memory.BeforeWrite = (address, bytes) =>
            {
                if (address != f.Campaign + 0x90 || injected) return;
                injected = true; f.Memory.PutBytes(address, bytes.AsSpan(0, 2)); throw new IOException("injected short write");
            };
            var error = Expect<DataWriteException>(() => f.Engine.WriteValue(FeatureId.DeathRate, 50));
            Check(error.RollbackComplete, "Recoverable partial write not restored");
            Check(f.Memory.Int(f.Campaign + 0x94) == 125 && f.Memory.Int(f.Campaign + 0x90) == 12, "Original values lost");
            Check(f.Memory.Int(f.Campaign + 0x88) == 37500, "Unattempted field changed");
        });
        test("data: rollback never overwrites third-party conflict", () =>
        {
            var f = new DataFixture(); f.Engine.Refresh(); bool injected = false;
            f.Memory.BeforeWrite = (address, _) =>
            {
                if (address != f.Campaign + 0x90 || injected) return;
                injected = true; f.Memory.Put(f.Campaign + 0x94, 777); throw new IOException("competing writer");
            };
            var error = Expect<DataWriteException>(() => f.Engine.WriteValue(FeatureId.DeathRate, 50));
            Check(!error.RollbackComplete && error.UnrestoredAddresses.Contains(f.Campaign + 0x94), "Conflict evidence lost");
            Check(f.Memory.Int(f.Campaign + 0x94) == 777, "Third-party value overwritten");
        });
        test("data: owner loss during transaction forbids rollback into unknown objects", () =>
        {
            var f = new DataFixture(); f.Engine.Refresh(); bool injected = false;
            uint replacement = 0x21004000; f.Copy(f.Campaign, replacement, 0xD0);
            f.Memory.BeforeWrite = (address, _) =>
            { if (address == f.Campaign + 0x90 && !injected) { injected = true; f.Bind(0x10C, replacement); } };
            var error = Expect<DataWriteException>(() => f.Engine.WriteValue(FeatureId.DeathRate, 50));
            Check(!error.RollbackComplete, "Unknown ownership silently restored");
            Check(f.Memory.Writes.Count == 2 && f.Memory.Int(replacement + 0x94) == 125, "Wrote into replacement/attempted unsafe rollback");
        });
        test("data: card batch uses created records, clears EXP and deduplicates", () =>
        {
            var f = new DataFixture(); f.Engine.Refresh(); f.Engine.WriteValue(FeatureId.Star, 40);
            foreach (uint record in new[] { f.Character1, f.Character2 })
                Check(f.Memory.Int(record + 4) == 40 && f.Memory.Int(record + 8) == 0 && f.Memory.Int(record + 12) == 4525, "Character growth fields wrong");
            Check(f.Memory.Writes.Count == 2 && f.Memory.Writes.All(x => x.Bytes.Length == 12), "Expected one contiguous write per character");
            f.Memory.Writes.Clear(); f.Memory.Put(f.CharacterArray + 4, 0x124);
            Expect<IOException>(() => f.Engine.WriteValue(FeatureId.Star, 80));
            Check(f.Memory.Writes.Count == 0, "Duplicate character passed preflight");
        });
        test("data: card array replacement interrupts batch and retains conflict", () =>
        {
            var f = new DataFixture(); f.Engine.Refresh(); bool injected = false;
            f.Memory.BeforeWrite = (address, _) =>
            { if (address == f.Character1 + 4 && !injected) { injected = true; f.Memory.Put(f.CardContainer + 0x10, 0x21100000); } };
            var error = Expect<DataWriteException>(() => f.Engine.WriteValue(FeatureId.Star, 200));
            Check(!error.RollbackComplete && f.Memory.Writes.Count == 1, "Replaced card collection not isolated");
            Check(f.Memory.Int(f.Character2 + 4) == 20, "Later character written after layout loss");
        });
        test("data: short read stops readiness and never writes", () =>
        {
            var f = new DataFixture(); f.Memory.Remove(f.Party + 0x10);
            Check(!f.Engine.Refresh().GameplayReady, "Partial read accepted as readiness");
            Expect<InvalidOperationException>(() => f.Engine.WriteValue(FeatureId.Food, 3));
            Check(f.Memory.Writes.Count == 0, "Write after failed readiness");
        });
        test("runtime/data: initial stability and same-PID owner replacement recapture", () =>
        {
            var f = new DataFixture();
            var snapshots = new ConcurrentQueue<TrainerSnapshot>();
            var runtime = new TrainerRuntime(() => new GameConnection(f.Memory, "unused-for-data-tests.ain"));
            runtime.SnapshotChanged += snapshots.Enqueue;
            try
            {
                runtime.StartAsync().GetAwaiter().GetResult();
                Wait(() => runtime.Current.Connection == ConnectionState.Ready);
                var initial = snapshots.First(x => x.ProcessId == f.Memory.ProcessId);
                Check(initial.Connection == ConnectionState.Loading && !initial.Features[FeatureId.Turn].CanEdit, "Initial observation allowed immediate writes");
                Check(runtime.SetLockAsync(FeatureId.Army, true).GetAwaiter().GetResult().Success, "Army lock rejected");
                uint replacement = 0x21004000; f.Copy(f.Campaign, replacement, 0xD0);
                for (uint i = 0; i < 4; i++) f.Memory.Put(replacement + 0x64 + i * 4, checked((int)(700 + i)));
                bool injected = false;
                f.Memory.BeforeWrite = (address, _) =>
                { if (address == f.Turn + 8 && !injected) { injected = true; f.Bind(0x10C, replacement); } };
                Check(runtime.SetValueAsync(FeatureId.Turn, 2).GetAwaiter().GetResult().Success, "Trigger command failed");
                Wait(() => runtime.Current.Connection == ConnectionState.Loading);
                Check(runtime.Current.Features[FeatureId.Army].LockRequested && !runtime.Current.Features[FeatureId.Army].LockActive, "Rebind lost request or retained old active state");
                Check(f.Memory.Int(replacement + 0x64) == 700, "Stale lock wrote replacement before stability");
                Wait(() => runtime.Current.Connection == ConnectionState.Ready);
                Wait(() => runtime.Current.Features[FeatureId.Army].LockActive);
                Check(Enumerable.Range(0, 4).All(i => f.Memory.Int(replacement + 0x64 + (uint)i * 4) == 700 + i), "Recapture restored values from previous owner");
            }
            finally { Check(runtime.TryCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult().Success, "Runtime close failed"); }
        });
        test("runtime/data: unresolved rollback latches off all data writes", () =>
        {
            var f = new DataFixture(); bool injected = false;
            f.Memory.BeforeWrite = (address, _) =>
            {
                if (address != f.Campaign + 0x90 || injected) return;
                injected = true; f.Memory.Put(f.Campaign + 0x94, 777); throw new IOException("runtime rollback conflict");
            };
            var runtime = new TrainerRuntime(() => new GameConnection(f.Memory, "unused-for-data-tests.ain"));
            try
            {
                runtime.StartAsync().GetAwaiter().GetResult(); Wait(() => runtime.Current.Connection == ConnectionState.Ready);
                var result = runtime.SetValueAsync(FeatureId.DeathRate, 50).GetAwaiter().GetResult();
                Check(!result.Success && runtime.Current.Connection == ConnectionState.Error, "Partial rollback not latched");
                Check(runtime.Current.Error?.Contains("暂停数据写入", StringComparison.Ordinal) == true, "Recovery evidence not visible");
                int writes = f.Memory.Writes.Count;
                Check(!runtime.SetValueAsync(FeatureId.Food, 3).GetAwaiter().GetResult().Success, "Manual data write escaped latch");
                Check(!runtime.SetToggleAsync(FeatureId.Resources, true).GetAwaiter().GetResult().Success, "Continuous write escaped latch");
                Check(!runtime.SetLockAsync(FeatureId.Turn, true).GetAwaiter().GetResult().Success, "Lock escaped latch");
                Check(f.Memory.Writes.Count == writes && f.Memory.Int(f.Campaign + 0x94) == 777, "Conflict overwritten after fault latch");
            }
            finally { Check(runtime.TryCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult().Success, "Runtime close failed"); }
        });
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static T Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T ex) { return ex; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Wait(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate()) { if (timer.Elapsed > TimeSpan.FromSeconds(6)) throw new TimeoutException("Runtime state transition timed out"); Thread.Sleep(10); }
    }
}

internal sealed class DataFixture
{
    public DataFakeMemory Memory { get; } = new();
    public R10DataEngine Engine { get; }
    public uint Offsets = 0x10040000, Turn = 0x20000000, Resources = 0x20001000, Party = 0x20002000,
        Campaign = 0x20004000, Enemy = 0x20005000, CharacterArray = 0x20009000,
        Character1 = 0x20010000, Character2 = 0x20011000;
    public uint CardContainer => Descriptor(0x120);
    private const uint PointerBase = 0x10010000;
    public DataFixture()
    {
        Memory.Put(Memory.ModuleBase + 0x3FCBB0, 0x10000000);
        Memory.Put(0x10000008, PointerBase); Memory.Put(PointerBase, 0x10020000);
        Memory.Put(0x10020000, 0x10030000); Memory.Put(0x10030010, Offsets);
        SetTable(0x3F8, 0x100, Turn); SetTable(0x400, 0x104, Resources);
        SetTable(0x410, 0x479, Party); Bind(0x108, 0x20003000);
        SetTable(0x500, 0x10C, Campaign); SetTable(0x48C, 0x110, Enemy);
        SetTable(0x490, 0x114, 0x20006000); Bind(0x118, 0x20007000);
        Memory.Put(0x20006000 + 13 * 4, 0x118); Memory.Put(0x20007000, 1);
        Memory.Put(Turn + 8, 0); Memory.Put(Turn + 12, 1);
        Memory.Put(Party, 0x108); Memory.Put(Party + 0x10, 500); Memory.Put(Party + 0x34, 0);
        Memory.Put(0x20003000, 1000); Memory.Put(Enemy, 500); Memory.Put(Enemy + 0x28, 500);
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
    public Action<uint, byte[]>? BeforeWrite { get; set; }
    public ConcurrentQueue<(uint Address, byte[] Bytes)> Writes { get; } = [];
    public byte[] Read(uint address, int length)
    {
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
