using R10.Bridge.Runtime;
using R10.Core;
using R10.Core.Memory;
using R10.Tests;

static class UnifiedRuntimeTests
{
    private static readonly string DirectoryA = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "R10-unified-fixture-a"));
    private static readonly string DirectoryB = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "R10-unified-fixture-b"));
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("unified battle edits use the bridge and respect enemy HP overrides", async () =>
        {
            var battle = new BattleEditContext(new(42, "fixture:original", 3, true), 7);
            var memory = new Fixture();
            var bridge = new Fixture { Current = Snapshot() with { Battle = battle } };
            var runtime = new UnifiedTrainerRuntime(DirectoryA, _ => (memory, bridge));
            foreach (var id in new[] { FeatureId.ApFull, FeatureId.UnlimitedSkills, FeatureId.StatusImmunity,
                         FeatureId.RepeatActions, FeatureId.UnlimitedActions })
                Check((await runtime.SetToggleAsync(id, true)).Success);
            Check(runtime.Current.Battle == battle && (await runtime.SetBattleHpAsync(FeatureId.EnemyHp, 80, battle)).Success);
            Check(bridge.BattleWrite == (battle, 80m) && memory.Writes.Count == 0 && bridge.Writes.Count == 5);
            foreach (var overrideId in new[] { FeatureId.EnemyKill, FeatureId.EnemyNear })
            {
                var state = Snapshot();
                var features = state.Features.ToDictionary(item => item.Key, item => item.Value);
                features[overrideId] = features[overrideId] with { Requested = true };
                memory.Publish(state with { Features = features });
                Check(!runtime.Current.Features[FeatureId.EnemyHp].CanEdit &&
                    !(await runtime.SetBattleHpAsync(FeatureId.EnemyHp, 70, battle)).Success &&
                    bridge.BattleWrite == (battle, 80m));
            }
            Check((await runtime.TryCloseAsync()).Success);
        });
        await test("unified exposes bridge epoch and preserves confirmation token to selected runtime", async () =>
        {
            var epoch=new GameLoadEpoch(42,"fixture:original",3,true);
            var memory=new Fixture(); var bridge=new Fixture { Current=Snapshot() with { LoadEpoch=epoch } };
            var runtime=new UnifiedTrainerRuntime(DirectoryA,_=>(memory,bridge));
            Check(runtime.Current.LoadEpoch==epoch);
            Check((await runtime.SetValueForEpochAsync(FeatureId.Star,80,epoch)).Success);
            Check(ReferenceEquals(bridge.Expected,epoch) && memory.Writes.Count==0);
            bridge.Publish(Snapshot() with { LoadEpoch=epoch with { Generation=4 } });
            Check(!(await runtime.SetValueForEpochAsync(FeatureId.Star,80,epoch)).Success && bridge.Writes.Count==1);
            Check((await runtime.TryCloseAsync()).Success);
        });
        await test("unified forwards load epochs only from selected bridge observations", async () =>
        {
            var memory=new Fixture(); var bridge=new Fixture();
            var runtime=new UnifiedTrainerRuntime(DirectoryA,_=>(memory,bridge));
            var epoch=new GameLoadEpoch(42,"fixture:1",7,true);
            bridge.Publish(Snapshot() with { LoadEpoch=epoch });
            Check(memory.LastEpoch==epoch);
            int observed=memory.EpochObservations;
            memory.Publish(Snapshot());
            Check(memory.EpochObservations==observed);
            bridge.Publish(Snapshot(42,ConnectionState.Error,false));
            Check(memory.LastEpoch is null);
            Check((await runtime.TryCloseAsync()).Success);
            observed=memory.EpochObservations;
            bridge.Publish(Snapshot() with { LoadEpoch=epoch });
            Check(memory.EpochObservations==observed);
        });
        await test("unified hides old data edit readiness as soon as the bridge reports loading", async () =>
        {
            var epoch = new GameLoadEpoch(42, "fixture:1", 7, true);
            var snapshot = Snapshot();
            var features = snapshot.Features.ToDictionary(item => item.Key, item => item.Value);
            features[FeatureId.War] = features[FeatureId.War] with { Requested = true, Active = true };
            features[FeatureId.Turn] = features[FeatureId.Turn] with { LockRequested = true, LockActive = true };
            var memory = new Fixture { Current = snapshot with { Features = features } };
            var bridge = new Fixture { Current = snapshot with { LoadEpoch = epoch } };
            var runtime = new UnifiedTrainerRuntime(DirectoryA, _ => (memory, bridge));
            Check(runtime.Current.Features[FeatureId.Turn].CanEdit && runtime.Current.Features[FeatureId.War].Active);
            bridge.Publish(Snapshot(42, ConnectionState.Loading, false) with { LoadEpoch = epoch with { Generation = 8, Ready = false } });
            var loading = runtime.Current;
            Check(loading.Connection == ConnectionState.Loading && !loading.Features[FeatureId.Turn].CanEdit &&
                !loading.Features[FeatureId.Turn].LockActive && loading.Features[FeatureId.War].Requested &&
                loading.Features[FeatureId.War].CanEdit && !loading.Features[FeatureId.War].Active);
            bridge.Publish(snapshot with { LoadEpoch = epoch with { Generation = 8 } });
            Check(runtime.Current.Connection == ConnectionState.Ready && runtime.Current.Features[FeatureId.Turn].CanEdit);
            Check((await runtime.TryCloseAsync()).Success);
        });
        await test("unified unselected never discovers or routes any writes", async () =>
        {
            int created = 0;
            var runtime = new UnifiedTrainerRuntime(null, _ => { created++; return (new Fixture(), new Fixture()); });
            await runtime.StartAsync();
            Check(!(await runtime.SetToggleAsync(FeatureId.Hp, true)).Success && created == 0);
            Check(runtime.Current.Features.Values.All(value => !value.CanEdit));
            Check(runtime.Current.Features[FeatureId.Hp].Help == UnifiedTrainerRuntime.HpHelp);
            Check((await runtime.TryCloseAsync()).Success);
        });
        await test("unified HP and cards only use bridge while other controls use memory", async () =>
        {
            var memory = new Fixture(); var bridge = new Fixture();
            var runtime = new UnifiedTrainerRuntime(DirectoryA, _ => (memory, bridge));
            await runtime.StartAsync();
            Check((await runtime.SetToggleAsync(FeatureId.Hp, true)).Success);
            Check((await runtime.SetToggleAsync(FeatureId.Hp, false)).Success);
            Check((await runtime.SetToggleAsync(FeatureId.NoDuplicateCards, true)).Success);
            Check((await runtime.SetToggleAsync(FeatureId.ExtraTreasurePick, true)).Success);
            Check((await runtime.SetToggleAsync(FeatureId.Redraw, true)).Success);
            Check((await runtime.SetToggleAsync(FeatureId.Redistribute, true)).Success);
            Check(!(await runtime.SetValueAsync(FeatureId.NoDuplicateCards, 10)).Success);
            Check(!(await runtime.SetLockAsync(FeatureId.NoDuplicateCards, true)).Success);
            Check(!(await runtime.SetValueAsync(FeatureId.Hp, 10)).Success);
            Check(!(await runtime.SetLockAsync(FeatureId.Hp, true)).Success);
            Check((await runtime.SetToggleAsync(FeatureId.EnemyNear, true)).Success);
            Check((await runtime.SetValueAsync(FeatureId.Food, 3)).Success);
            Check((await runtime.SetLockAsync(FeatureId.Turn, true)).Success);
            Check(memory.Writes.SequenceEqual(new[] { "toggle:EnemyNear:True", "lock:Turn:True" }));
            Check(bridge.Writes.SequenceEqual(new[] { "toggle:Hp:True", "toggle:Hp:False", "toggle:NoDuplicateCards:True",
                "toggle:ExtraTreasurePick:True", "toggle:Redraw:True", "toggle:Redistribute:True", "value:Food:3" }));
            Check((await runtime.TryCloseAsync()).Success && memory.Closes == 1 && bridge.Closes == 1);
        });
        await test("unified original game keeps other features and never falls back to HP healing", async () =>
        {
            var memory = new Fixture(); var bridge = new Fixture { Current = Snapshot(null, ConnectionState.Error, false) };
            var runtime = new UnifiedTrainerRuntime(DirectoryA, _ => (memory, bridge));
            await runtime.StartAsync();
            Check(!runtime.Current.Features[FeatureId.Hp].CanEdit && !runtime.Current.Features[FeatureId.NoDuplicateCards].CanEdit && runtime.Current.Features[FeatureId.Turn].CanEdit);
            Check(!(await runtime.SetToggleAsync(FeatureId.Hp, true)).Success && memory.Writes.Count == 0 && bridge.Writes.Count == 0);
            Check(!(await runtime.SetToggleAsync(FeatureId.NoDuplicateCards, true)).Success && memory.Writes.Count == 0 && bridge.Writes.Count == 0);
            Check((await runtime.SetValueAsync(FeatureId.Turn, 2)).Success && memory.Writes.Count == 1);
            Check((await runtime.TryCloseAsync()).Success);
        });
        await test("unified component capabilities are independent", async () =>
        {
            foreach (var missing in new[] { FeatureId.Hp, FeatureId.NoDuplicateCards })
            {
                var available = missing == FeatureId.Hp ? FeatureId.NoDuplicateCards : FeatureId.Hp;
                var memory = new Fixture();
                var snapshot = Snapshot();
                var features = snapshot.Features.ToDictionary(x => x.Key, x => x.Value);
                features[missing] = features[missing] with { CanEdit = false };
                var bridge = new Fixture { Current = snapshot with { Features = features } };
                var runtime = new UnifiedTrainerRuntime(DirectoryA, _ => (memory, bridge));
                Check(!runtime.Current.Features[missing].CanEdit && runtime.Current.Features[available].CanEdit);
                Check(!(await runtime.SetToggleAsync(missing, true)).Success);
                Check((await runtime.SetToggleAsync(available, true)).Success && bridge.Writes.Count == 1 && memory.Writes.Count == 0);
                Check((await runtime.TryCloseAsync()).Success);
            }
        });
        await test("unified different process snapshots cannot enable features or merge HP", async () =>
        {
            var memory = new Fixture(); var bridge = new Fixture { Current = Snapshot(43) };
            var runtime = new UnifiedTrainerRuntime(DirectoryA, _ => (memory, bridge));
            Check(!runtime.Current.Features[FeatureId.Hp].CanEdit && !runtime.Current.Features[FeatureId.Turn].CanEdit);
            Check(!(await runtime.SetToggleAsync(FeatureId.Hp, true)).Success && !(await runtime.SetValueAsync(FeatureId.Food, 3)).Success);
            bridge.Publish(Snapshot(42));
            Check(runtime.Current.Features[FeatureId.Hp].CanEdit && (await runtime.SetToggleAsync(FeatureId.Hp, true)).Success);
            Check(memory.Writes.Count == 0 && bridge.Writes.Count == 1);
            Check((await runtime.TryCloseAsync()).Success);
        });
        await test("unified disconnect releases both runtimes and reselect builds a fresh pair", async () =>
        {
            var pairs = new List<(Fixture Memory, Fixture Bridge)>();
            var runtime = new UnifiedTrainerRuntime(DirectoryA, _ => { var pair = (new Fixture(), new Fixture()); pairs.Add(pair); return pair; });
            await runtime.StartAsync();
            Check((await runtime.SelectGameAsync(null)).Success && runtime.GameDirectory is null);
            Check(pairs[0].Memory.Closes == 1 && pairs[0].Bridge.Closes == 1);
            Check((await runtime.SelectGameAsync(DirectoryA)).Success && pairs.Count == 2);
            Check(pairs[1].Memory.Starts == 1 && pairs[1].Bridge.Starts == 1);
            pairs[0].Bridge.Publish(Snapshot(99));
            Check(runtime.Current.ProcessId == 42 && runtime.GameDirectory == DirectoryA);
            Check((await runtime.TryCloseAsync()).Success);
        });
        await test("unified unresolved data rollback survives reselect and only blocks the faulted process", async () =>
        {
            const int oldPid = 1234, otherPid = 4321;
            var original = new DataFixture();
            var other = new DataFixture();
            var restarted = new DataFixture();
            original.InjectDeathRateRollbackConflict(777);
            bool originalExited = false;
            bool livenessUnavailable = false;
            GameConnection Discover(string path)
            {
                if (path == DirectoryB) return new(new FixtureProcessMemory(other.Memory, otherPid, 100, () => true), "fixture.ain");
                if (Volatile.Read(ref originalExited)) return new(new FixtureProcessMemory(restarted.Memory, oldPid, 200, () => true), "fixture.ain");
                return new(new FixtureProcessMemory(original.Memory, oldPid, 100, () =>
                    Volatile.Read(ref livenessUnavailable) ? throw new IOException("liveness probe failed") : !Volatile.Read(ref originalExited)), "fixture.ain");
            }
            var bridges = new List<Fixture>();
            var runtime = new UnifiedTrainerRuntime(DirectoryA, Discover, path =>
            {
                var bridge = new Fixture { Current = Snapshot(path == DirectoryA ? oldPid : otherPid) };
                bridges.Add(bridge);
                return bridge;
            });
            async Task WaitForConnection(int pid)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                while (runtime.Current.ProcessId != pid || runtime.Current.Connection is not (ConnectionState.Ready or ConnectionState.Error))
                    await Task.Delay(10, timeout.Token);
            }
            try
            {
                await runtime.StartAsync();
                await WaitForConnection(oldPid);
                Check(runtime.Current.Connection == ConnectionState.Ready);
                Check(!(await runtime.SetValueAsync(FeatureId.DeathRate, 50)).Success);
                string? fault = runtime.Current.Error;
                Check(runtime.Current.Connection == ConnectionState.Error && fault?.Contains("暂停数据写入") == true);
                int writes = original.Memory.Writes.Count;

                async Task AssertStillFaulted()
                {
                    await WaitForConnection(oldPid);
                    Check(runtime.Current.Connection == ConnectionState.Error && runtime.Current.Error == fault);
                    Check(!runtime.Current.Features[FeatureId.Turn].CanEdit && !runtime.Current.Features[FeatureId.Hp].CanEdit);
                    Check(!(await runtime.SetValueAsync(FeatureId.Turn, 2)).Success);
                    Check(!(await runtime.SetLockAsync(FeatureId.Turn, true)).Success);
                    Check(!(await runtime.SetToggleAsync(FeatureId.EnemyNear, true)).Success);
                    Check(!(await runtime.SetToggleAsync(FeatureId.Hp, true)).Success);
                    Check(original.Memory.Writes.Count == writes && original.Memory.Int(original.Campaign + 0x94) == 777);
                    Check(bridges.All(bridge => bridge.Writes.Count == 0));
                }

                Check((await runtime.SelectGameAsync(DirectoryA)).Success);
                await AssertStillFaulted();
                Volatile.Write(ref livenessUnavailable, true);
                Check(!(await runtime.SelectGameAsync(null)).Success);
                Volatile.Write(ref livenessUnavailable, false);
                Check((await runtime.SelectGameAsync(null)).Success);
                Check((await runtime.SelectGameAsync(DirectoryB)).Success);
                await WaitForConnection(otherPid);
                Check((await runtime.SetValueAsync(FeatureId.Turn, 2)).Success && other.Memory.Int(other.Turn + 8) == 2);
                Check((await runtime.SelectGameAsync(DirectoryA)).Success);
                await AssertStillFaulted();

                Check((await runtime.SelectGameAsync(null)).Success);
                Volatile.Write(ref originalExited, true);
                Check((await runtime.SelectGameAsync(DirectoryA)).Success);
                await WaitForConnection(oldPid);
                Check(runtime.Current.Connection == ConnectionState.Ready);
                Check((await runtime.SetValueAsync(FeatureId.Turn, 3)).Success && restarted.Memory.Int(restarted.Turn + 8) == 3);
                Check(original.Memory.Writes.Count == writes && original.Memory.Int(original.Campaign + 0x94) == 777);
            }
            finally
            {
                Volatile.Write(ref livenessUnavailable, false);
                Check((await runtime.TryCloseAsync()).Success);
            }
        });
        await test("unified failed close attempts both owners and blocks writes until retry", async () =>
        {
            var oldMemory = new Fixture { CloseResult = OperationResult.Fail("fixture restore failed") };
            var oldBridge = new Fixture(); int count = 0;
            var runtime = new UnifiedTrainerRuntime(DirectoryA, _ => ++count == 1 ? (oldMemory, oldBridge) : (new Fixture(), new Fixture()));
            Check(!(await runtime.SelectGameAsync(DirectoryB)).Success);
            Check(runtime.GameDirectory == DirectoryA && oldMemory.Closes == 1 && oldBridge.Closes == 1 && count == 1);
            Check(runtime.Current.Connection == ConnectionState.RestorePending && !(await runtime.SetToggleAsync(FeatureId.Hp, true)).Success);
            oldMemory.CloseResult = OperationResult.Ok;
            Check((await runtime.SelectGameAsync(DirectoryB)).Success && runtime.GameDirectory == DirectoryB && count == 2);
            Check(oldMemory.Closes == 2 && oldBridge.Closes == 1);
            Check((await runtime.TryCloseAsync()).Success);
        });
        await test("unified unexpected close exception still restores the other owner", async () =>
        {
            var memory = new Fixture { CloseError = new IOException("fixture close exception") }; var bridge = new Fixture();
            var runtime = new UnifiedTrainerRuntime(DirectoryA, _ => (memory, bridge));
            Check(!(await runtime.TryCloseAsync()).Success && memory.Closes == 1 && bridge.Closes == 1);
            memory.CloseError = null;
            Check((await runtime.TryCloseAsync()).Success && runtime.Current.Connection == ConnectionState.Closed);
        });
        await test("unified a pending selection rejects later mutation requests", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var memory = new Fixture { Close = async () => { entered.SetResult(); await release.Task; return OperationResult.Ok; } }; var bridge = new Fixture();
            var runtime = new UnifiedTrainerRuntime(DirectoryA, _ => (memory, bridge));
            var selecting = runtime.SelectGameAsync(null); await entered.Task;
            var later = runtime.SetToggleAsync(FeatureId.Hp, true);
            Check(later.IsCompleted && !(await later).Success && bridge.Writes.Count == 0);
            release.SetResult(); Check((await selecting).Success && runtime.GameDirectory is null);
            Check((await runtime.TryCloseAsync()).Success);
        });
        await test("unified lost bridge state disables HP without hiding ordinary controls", async () =>
        {
            var memory = new Fixture(); var bridge = new Fixture();
            var runtime = new UnifiedTrainerRuntime(DirectoryA, _ => (memory, bridge));
            Check(runtime.Current.Features[FeatureId.Hp].CanEdit);
            bridge.Publish(Snapshot(42, ConnectionState.Error, false));
            Check(!runtime.Current.Features[FeatureId.Hp].CanEdit && runtime.Current.Features[FeatureId.Turn].CanEdit);
            Check(!(await runtime.SetToggleAsync(FeatureId.Hp, true)).Success && memory.Writes.Count == 0);
            Check((await runtime.TryCloseAsync()).Success);
        });
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Unified runtime assertion failed"); }
    private static TrainerSnapshot Snapshot(int? pid = 42, ConnectionState connection = ConnectionState.Ready, bool ready = true) =>
        new(connection, "fixture", pid, true, FeatureCatalog.All.ToDictionary(feature => feature.Id, _ => new FeatureObservation(CanEdit: ready)));
    private sealed class FixtureProcessMemory(DataFakeMemory memory, int pid, long creationFileTime, Func<bool> isAlive) : IProcessMemory
    {
        public int ProcessId => pid;
        public long? ProcessCreationFileTime => creationFileTime;
        public uint ModuleBase => memory.ModuleBase;
        public bool IsAlive => isAlive();
        public byte[] Read(uint address, int length) => memory.Read(address, length);
        public void Write(uint address, ReadOnlySpan<byte> bytes) => memory.Write(address, bytes);
        public IReadOnlyList<MemoryRegion> QueryRegions() => memory.QueryRegions();
        public void Dispose() { }
    }
    private sealed class Fixture : ITrainerRuntime, IGameLoadObserver
    {
        public GameLoadEpoch? LastEpoch { get; private set; }
        public int EpochObservations { get; private set; }
        public void ObserveGameLoad(GameLoadEpoch? epoch) { LastEpoch=epoch; EpochObservations++; }
        public TrainerSnapshot Current { get; set; } = Snapshot();
        public event Action<TrainerSnapshot>? SnapshotChanged;
        public List<string> Writes { get; } = [];
        public GameLoadEpoch? Expected;
        public (BattleEditContext, decimal)? BattleWrite;
        public Task<OperationResult> SetBattleHpAsync(FeatureId id, decimal value, BattleEditContext expected)
        {
            if (Current.Battle != expected) return Task.FromResult(OperationResult.Fail("battle changed"));
            BattleWrite = (expected, value);
            return Task.FromResult(OperationResult.Ok);
        }
        public Task<OperationResult> SetValueForEpochAsync(FeatureId id, decimal value, GameLoadEpoch expected)
        {
            Expected=expected;
            return Current.LoadEpoch==expected ? SetValueAsync(id,value) : Task.FromResult(OperationResult.Fail("epoch changed"));
        }
        public int Starts, Closes;
        public OperationResult CloseResult = OperationResult.Ok;
        public Exception? CloseError;
        public Func<Task<OperationResult>>? Close;
        public void Publish(TrainerSnapshot snapshot) { Current = snapshot; SnapshotChanged?.Invoke(snapshot); }
        public Task StartAsync() { Starts++; return Task.CompletedTask; }
        public Task<OperationResult> SetToggleAsync(FeatureId id, bool enabled) { Writes.Add($"toggle:{id}:{enabled}"); return Task.FromResult(OperationResult.Ok); }
        public Task<OperationResult> SetValueAsync(FeatureId id, decimal value) { Writes.Add($"value:{id}:{value}"); return Task.FromResult(OperationResult.Ok); }
        public Task<OperationResult> SetLockAsync(FeatureId id, bool enabled) { Writes.Add($"lock:{id}:{enabled}"); return Task.FromResult(OperationResult.Ok); }
        public Task<OperationResult> TryCloseAsync()
        {
            Closes++;
            if (CloseError is not null) throw CloseError;
            return Close?.Invoke() ?? Task.FromResult(CloseResult);
        }
    }
}
