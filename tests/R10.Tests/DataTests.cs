using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using R10.Core;
using R10.Core.Data;
using R10.Core.Memory;

namespace R10.Tests;

public static class DataTests
{
    public static void Run(Action<string, Action> test)
    {
        test("data: load epoch defeats same-address lock reuse and pauses lost heartbeat", () =>
        {
            var f=new DataFixture(); f.Engine.Refresh(); long now=0;
            var fence=new GameLoadFence(()=>now);
            var epoch=new GameLoadEpoch(f.Memory.ProcessId,"fixture:1",0,true);
            fence.Update(epoch);
            var held=f.Engine.CaptureLock(FeatureId.Army,fence.CaptureGuard(f.Memory.ProcessId));
            now=1001;
            Check(!f.Engine.MaintainLock(held) && f.Memory.Writes.Count==0,"Expired observation wrote a lock");
            fence.Update(epoch); Check(f.Engine.MaintainLock(held),"Same-epoch heartbeat did not recover");
            fence.Update(epoch with { Generation=1,Ready=false });
            Check(!f.Engine.MaintainLock(held) && f.Memory.Writes.Count==0,"Same-address reload retained old target");
            fence.Update(epoch with { Generation=1 });
            Check(!f.Engine.MaintainLock(held),"Old target resumed after loading");
            var next=f.Engine.CaptureLock(FeatureId.Army,fence.CaptureGuard(f.Memory.ProcessId));
            fence.Update(null); Check(!f.Engine.MaintainLock(next),"Lost component fell back to legacy writes");
            fence.Update(epoch with { Generation=1,Session="fixture:2" });
            Check(!f.Engine.MaintainLock(next),"New session reused old lock");
        });
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
        test("data: composed sampling skips component fields and reads the four armies together", () =>
        {
            var f = new DataFixture();
            f.Engine.Refresh();
            List<(uint Address, int Length)> reads = [];
            f.Memory.BeforeRead = (address, length) => reads.Add((address, length));
            var skipped = new HashSet<FeatureId> { FeatureId.Food, FeatureId.Medal, FeatureId.Friendship,
                FeatureId.Unit, FeatureId.Star, FeatureId.PlayerHp, FeatureId.EnemyHp };
            var values = f.Engine.ReadValues(skipped);
            Check(skipped.All(id => values[id] is null) && values[FeatureId.Turn] == 0 && values[FeatureId.DeathRate] == 12.5m,
                "Skipping component fields changed the external values");
            Check(!reads.Any(read => read.Address == f.Offsets + 0x400), "Resource pointer chain was still sampled");
            Check(reads.Where(read => read.Address >= f.Campaign + 0x64 && read.Address <= f.Campaign + 0x70)
                .SequenceEqual(new[] { (f.Campaign + 0x64, 16) }), "Army fields were not read as one block");
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
            f.Engine.MaintainToggle(FeatureId.Treasure);
            Check(f.Memory.Int(f.CommonStatus + 0x34) == 100 && f.Memory.Int(f.Party + 0x34) == 2, "Treasure bonus wrote the wrong object");
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
        test("data: death-rate batch stops when phase changes without rollback into forbidden phase", () =>
        {
            foreach (bool locked in new[] { false, true })
            foreach (uint stopOffset in new[] { 0x94u, 0x88u })
            {
                var f = new DataFixture(); f.Engine.Refresh();
                var target = f.Engine.CaptureLock(FeatureId.DeathRate);
                if (locked) // Capture 12.5%, then change all four values so lock maintenance writes them.
                    foreach (var (offset, value) in new[] { (0x94u, 500), (0x90u, 50), (0x88u, 150000), (0x84u, 150000) })
                        f.Memory.Put(f.Campaign + offset, value);
                int originalLast = f.Memory.Int(f.Campaign + 0x84);
                f.Memory.BeforeWrite = (address, _) => { if (address == f.Campaign + stopOffset) f.Memory.Put(f.Turn + 12, 16); };
                var error = Expect<DataWriteException>(() => { if (locked) f.Engine.MaintainLock(target); else f.Engine.WriteValue(FeatureId.DeathRate, 50); });
                Check(!error.RollbackComplete, "Forbidden phase falsely reported as safely restored");
                Check(f.Memory.Writes.Count == (stopOffset == 0x94 ? 1 : 3), "Continued writes or rolled back during forbidden phase");
                Check(f.Memory.Int(f.Campaign + 0x84) == originalLast && f.Memory.Int(f.Campaign + 0x8C) == 123456, "Remaining field or stage cache changed");
            }
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
        test("data: partial native write preserves uncertain field and restores completed fields", () =>
        {
            var f = new DataFixture(); f.Engine.Refresh(); bool injected = false;
            f.Memory.BeforeWrite = (address, bytes) =>
            {
                if (address != f.Campaign + 0x90 || injected) return;
                injected = true; f.Memory.PutBytes(address, bytes.AsSpan(0, 2)); throw new IOException("injected short write");
            };
            var error = Expect<DataWriteException>(() => f.Engine.WriteValue(FeatureId.DeathRate, 50));
            Check(!error.RollbackComplete && error.UnrestoredAddresses.Contains(f.Campaign + 0x90), "Partial write incorrectly claimed as owned");
            Check(f.Memory.Int(f.Campaign + 0x94) == 125 && f.Memory.Int(f.Campaign + 0x90) == 50, "Completed/uncertain fields handled incorrectly");
            Check(f.Memory.Int(f.Campaign + 0x88) == 37500, "Unattempted field changed");
        });
        test("data: failed write never adopts a competing readback, even equal to target", () =>
        {
            foreach (int competing in new[] { 777, 500 })
            {
                var f = new DataFixture(); f.Engine.Refresh(); bool injected = false;
                f.Memory.BeforeWrite = (address, _) =>
                {
                    if (address != f.Campaign + 0x94 || injected) return;
                    injected = true; f.Memory.Put(address, competing); throw new IOException("failed write with concurrent update");
                };
                var error = Expect<DataWriteException>(() => f.Engine.WriteValue(FeatureId.DeathRate, 50));
                Check(!error.RollbackComplete && error.UnrestoredAddresses.Contains(f.Campaign + 0x94), "Unknown result reported restored");
                Check(f.Memory.Int(f.Campaign + 0x94) == competing && f.Memory.Writes.Count == 1, "Competing result overwritten");
            }
        });
        test("data: failed write leaving original bytes needs no rollback", () =>
        {
            var f = new DataFixture(); f.Engine.Refresh();
            f.Memory.BeforeWrite = (_, _) => throw new IOException("no write occurred");
            var error = Expect<DataWriteException>(() => f.Engine.WriteValue(FeatureId.DeathRate, 50));
            Check(error.RollbackComplete && f.Memory.Writes.Count == 1 && f.Memory.Int(f.Campaign + 0x94) == 125, "Unchanged original incorrectly treated as conflict");
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
        test("data: missing battle slots on the map do not follow invalid tokens", () =>
        {
            var f = new DataFixture();
            uint roundTable = f.Offsets + 0x490, enemyTable = f.Offsets + 0x48C;
            int roundToken = f.Memory.Int(roundTable), enemyToken = f.Memory.Int(enemyTable);
            f.Memory.Put(roundTable, -1);
            f.Memory.Put(enemyTable, -1);
            f.Memory.BeforeRead = (address, _) =>
            {
                if (address == DataFixture.PointerBase - 4) throw new Exception("Negative token reached pointer table");
            };
            var map = f.Engine.Refresh();
            Check(map.GameplayReady && !map.BattleReady && map.Error is null, "Missing battle objects disabled map data");
            Check(f.Engine.ReadValues()[FeatureId.Turn] == 0, "Map values became unreadable");
            Expect<InvalidOperationException>(() => f.Engine.MaintainToggle(FeatureId.EnemyNear));
            Check(f.Memory.Writes.Count == 0, "Absent battle object was written");
            f.Memory.Put(roundTable, roundToken);
            f.Memory.Put(enemyTable, enemyToken);
            Check(f.Engine.Refresh().BattleReady, "Returning to battle did not restore battle readiness");
        });
        test("data: Win32 partial reads only disable readiness for the affected scene", () =>
        {
            var f = new DataFixture();
            uint missing = f.Enemy;
            f.Memory.BeforeRead = (address, _) =>
            {
                if (address == missing) throw new Win32Exception(299, "Fixture read failed");
            };
            var optional = f.Engine.Refresh();
            Check(optional.GameplayReady && !optional.BattleReady && optional.Error is null, "Optional battle read invalidated map data");
            missing = f.Party + 0x10;
            var required = f.Engine.Refresh();
            Check(!required.GameplayReady && required.Error == "Fixture read failed", "Required read failure was lost");
        });
        test("data: short read stops readiness and never writes", () =>
        {
            var f = new DataFixture(); f.Memory.Remove(f.Party + 0x10);
            Check(!f.Engine.Refresh().GameplayReady, "Partial read accepted as readiness");
            Expect<InvalidOperationException>(() => f.Engine.WriteValue(FeatureId.Food, 3));
            Check(f.Memory.Writes.Count == 0, "Write after failed readiness");
        });
        test("runtime/data: load generation cancels requested lock even when all pointers are reused", () =>
        {
            var f=new DataFixture();
            var runtime=new TrainerRuntime(()=>new GameConnection(f.Memory,"unused-for-data-tests.ain"));
            var epoch=new GameLoadEpoch(f.Memory.ProcessId,"fixture:1",0,true);
            try
            {
                runtime.ObserveGameLoad(epoch); runtime.StartAsync().GetAwaiter().GetResult();
                Wait(()=>runtime.Current.Connection==ConnectionState.Ready);
                runtime.ObserveGameLoad(epoch);
                Check(runtime.SetLockAsync(FeatureId.Army,true).GetAwaiter().GetResult().Success,"Initial lock rejected");
                runtime.ObserveGameLoad(epoch with { Generation=1,Ready=false });
                f.Memory.Put(f.Campaign+0x64,777);
                Wait(()=>!runtime.Current.Features[FeatureId.Army].LockRequested);
                runtime.ObserveGameLoad(epoch with { Generation=1 });
                Check(f.Memory.Int(f.Campaign+0x64)==777 && f.Memory.Writes.Count==0,"Old lock restored pre-load value");
                Check(runtime.SetLockAsync(FeatureId.Army,true).GetAwaiter().GetResult().Success,"Explicit new lock rejected");
                Check(runtime.SetLockAsync(FeatureId.Army,false).GetAwaiter().GetResult().Success,"Unlock rejected");
            }
            finally { Check(runtime.TryCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult().Success,"Runtime close failed"); }
        });
        test("runtime/data: loading blocks manual and continuous data writes until the new epoch is ready", () =>
        {
            var f = new DataFixture();
            var runtime = new TrainerRuntime(() => new GameConnection(f.Memory, "unused-for-data-tests.ain"));
            var epoch = new GameLoadEpoch(f.Memory.ProcessId, "fixture:1", 0, true);
            try
            {
                runtime.ObserveGameLoad(epoch);
                runtime.StartAsync().GetAwaiter().GetResult();
                Wait(() => runtime.Current.Connection == ConnectionState.Ready);
                runtime.ObserveGameLoad(epoch);
                Check(runtime.SetToggleAsync(FeatureId.War, true).GetAwaiter().GetResult().Success, "War request rejected");
                Check(runtime.SetToggleAsync(FeatureId.EnemyNear, true).GetAwaiter().GetResult().Success, "Battle request rejected");
                Wait(() => runtime.Current.Features[FeatureId.War].Active && runtime.Current.Features[FeatureId.EnemyNear].Active);

                var oldRequest = runtime.CaptureDataRequest();
                var loading = epoch with { Generation = 1, Ready = false };
                runtime.ObserveGameLoad(loading);
                f.Memory.Put(f.Campaign + 0xBC, 0);
                f.Memory.Put(f.Enemy, 500);
                f.Memory.Writes.Clear();
                foreach (var (id, value) in new[] { (FeatureId.Turn, 42), (FeatureId.Difficulty, -3), (FeatureId.DeathRate, 50), (FeatureId.Army, 900) })
                    Check(!runtime.SetValueAsync(id, value).GetAwaiter().GetResult().Success, $"Loading accepted {id}");
                Check(!runtime.SetLockAsync(FeatureId.Turn, true).GetAwaiter().GetResult().Success, "Loading accepted a new lock");
                Wait(() => runtime.Current.Features[FeatureId.Turn].Value is null);
                Check(!runtime.Current.Features[FeatureId.Turn].CanEdit && !runtime.Current.Features[FeatureId.War].Active &&
                    !runtime.Current.Features[FeatureId.EnemyNear].Active && f.Memory.Writes.IsEmpty &&
                    f.Memory.Int(f.Campaign + 0xBC) == 0 && f.Memory.Int(f.Enemy) == 500,
                    "Loading preserved old readiness or let a maintenance write through");

                runtime.ObserveGameLoad(loading with { Ready = true });
                Wait(() => runtime.Current.Features[FeatureId.War].Active && runtime.Current.Features[FeatureId.EnemyNear].Active);
                Check(!runtime.SetValueAsync(FeatureId.Turn, 42, oldRequest).GetAwaiter().GetResult().Success &&
                    !runtime.SetLockAsync(FeatureId.Turn, true, oldRequest).GetAwaiter().GetResult().Success &&
                    f.Memory.Int(f.Turn + 8) == 0, "Old prepared request escaped the actual data worker");
                Check(runtime.SetValueAsync(FeatureId.Turn, 42).GetAwaiter().GetResult().Success && f.Memory.Int(f.Turn + 8) == 42,
                    "New epoch did not restore normal writes");
            }
            finally { Check(runtime.TryCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult().Success, "Runtime close failed"); }
        });
        test("data: load epoch is checked at the forward and rollback write boundaries", () =>
        {
            foreach (bool duringRollback in new[] { false, true })
            {
                var f = new DataFixture();
                f.Engine.Refresh();
                var fence = new GameLoadFence(() => 0);
                var epoch = new GameLoadEpoch(f.Memory.ProcessId, "fixture:1", 0, true);
                fence.Update(epoch);
                var contextValid = fence.CaptureGuard(f.Memory.ProcessId);
                uint first = f.Campaign + 0x64, second = f.Campaign + 0x68;
                int reads = 0;
                void LoadAtFirstFieldRead(uint address, int length)
                {
                    if (address != first || length != 4 || --reads != 0) return;
                    fence.Update(epoch with { Generation = 1 });
                }

                if (duringRollback)
                    f.Memory.BeforeWrite = (address, _) =>
                    {
                        if (address != second) return;
                        reads = 1;
                        f.Memory.BeforeRead = LoadAtFirstFieldRead;
                        throw new IOException("second field write failed");
                    };
                else
                {
                    reads = 2;
                    f.Memory.BeforeRead = LoadAtFirstFieldRead;
                }

                var error = Expect<DataWriteException>(() => f.Engine.WriteValue(FeatureId.Army, 900, contextValid));
                Check(f.Memory.Writes.Count == (duringRollback ? 2 : 0), "A write crossed the observed load boundary");
                Check(error.UnrestoredAddresses.SequenceEqual(duringRollback ? [first] : Array.Empty<uint>()) &&
                    f.Memory.Int(first) == (duringRollback ? 900 : 100), "Rollback ownership result did not match the load transition");
            }
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
        test("runtime/data: treasure recalculation between readbacks reproduces the write suspension", () =>
        {
            var f = new DataFixture();
            uint treasureAddress = f.CommonStatus + 0x34;
            f.Memory.Put(treasureAddress, 22);
            int postWriteReads = 0;
            f.Memory.BeforeRead = (address, length) =>
            {
                if (address != treasureAddress || length != sizeof(int) || f.Memory.Writes.IsEmpty) return;
                if (++postWriteReads == 2)
                    f.Memory.Put(address, 0); // PartyStatus recalculation clears the bonus before summing it again.
            };

            var runtime = new TrainerRuntime(() => new GameConnection(f.Memory, "unused-for-data-tests.ain"));
            try
            {
                runtime.StartAsync().GetAwaiter().GetResult();
                Wait(() => runtime.Current.Connection == ConnectionState.Ready);
                Check(runtime.SetToggleAsync(FeatureId.Treasure, true).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult().Success,
                    "Treasure request rejected");
                Wait(() => runtime.Current.Connection == ConnectionState.Error);

                var snapshot = runtime.Current;
                var treasure = snapshot.Features[FeatureId.Treasure];
                string failure = $"批量写入最终校验失败。；未能确认恢复：0x{treasureAddress:X8}";
                Check(treasure is { Requested: false, Active: false, CanEdit: false } && treasure.Error == failure,
                    $"Expected the treasure final verification failure, got {treasure}");
                Check(snapshot.Error?.Contains("已暂停数据写入", StringComparison.Ordinal) == true &&
                      snapshot.Error.EndsWith(failure, StringComparison.Ordinal), "Global suspension did not report the treasure conflict");

                var write = f.Memory.Writes.Single();
                Check(write.Address == treasureAddress && BinaryPrimitives.ReadInt32LittleEndian(write.Bytes) == 100 &&
                      f.Memory.Int(treasureAddress) == 0, "Rollback overwrote the game's recalculation");
                f.Memory.Put(treasureAddress, 22);
                Check(!runtime.SetValueAsync(FeatureId.Turn, 1).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult().Success &&
                      f.Memory.Writes.Count == 1, "Writes resumed after the game finished recalculating");
            }
            finally { Check(runtime.TryCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult().Success, "Runtime close failed"); }
        });
        test("runtime/data: unresolved rollback latches off all data writes", () =>
        {
            var f = new DataFixture();
            f.InjectDeathRateRollbackConflict(777);
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
