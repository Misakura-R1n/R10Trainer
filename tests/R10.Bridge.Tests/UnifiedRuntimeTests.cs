using R10.Bridge.Runtime;
using R10.Core;

static class UnifiedRuntimeTests
{
    private static readonly string DirectoryA = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "R10-unified-fixture-a"));
    private static readonly string DirectoryB = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "R10-unified-fixture-b"));
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
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
            Check((await runtime.SetToggleAsync(FeatureId.Redraw, true)).Success);
            Check((await runtime.SetToggleAsync(FeatureId.Redistribute, true)).Success);
            Check(!(await runtime.SetValueAsync(FeatureId.NoDuplicateCards, 10)).Success);
            Check(!(await runtime.SetLockAsync(FeatureId.NoDuplicateCards, true)).Success);
            Check(!(await runtime.SetValueAsync(FeatureId.Hp, 10)).Success);
            Check(!(await runtime.SetLockAsync(FeatureId.Hp, true)).Success);
            Check((await runtime.SetToggleAsync(FeatureId.EnemyNear, true)).Success);
            Check((await runtime.SetValueAsync(FeatureId.Food, 3)).Success);
            Check((await runtime.SetLockAsync(FeatureId.Turn, true)).Success);
            Check(memory.Writes.SequenceEqual(new[] { "toggle:EnemyNear:True", "value:Food:3", "lock:Turn:True" }));
            Check(bridge.Writes.SequenceEqual(new[] { "toggle:Hp:True", "toggle:Hp:False", "toggle:NoDuplicateCards:True", "toggle:Redraw:True", "toggle:Redistribute:True" }));
            Check((await runtime.TryCloseAsync()).Success && memory.Closes == 1 && bridge.Closes == 1);
        });
        await test("unified original game keeps other features and never falls back to HP healing", async () =>
        {
            var memory = new Fixture(); var bridge = new Fixture { Current = Snapshot(null, ConnectionState.Error, false) };
            var runtime = new UnifiedTrainerRuntime(DirectoryA, _ => (memory, bridge));
            await runtime.StartAsync();
            Check(!runtime.Current.Features[FeatureId.Hp].CanEdit && !runtime.Current.Features[FeatureId.NoDuplicateCards].CanEdit && runtime.Current.Features[FeatureId.Food].CanEdit);
            Check(!(await runtime.SetToggleAsync(FeatureId.Hp, true)).Success && memory.Writes.Count == 0 && bridge.Writes.Count == 0);
            Check(!(await runtime.SetToggleAsync(FeatureId.NoDuplicateCards, true)).Success && memory.Writes.Count == 0 && bridge.Writes.Count == 0);
            Check((await runtime.SetValueAsync(FeatureId.Food, 2)).Success && memory.Writes.Count == 1);
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
            Check(!runtime.Current.Features[FeatureId.Hp].CanEdit && !runtime.Current.Features[FeatureId.Food].CanEdit);
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
            Check(!runtime.Current.Features[FeatureId.Hp].CanEdit && runtime.Current.Features[FeatureId.Food].CanEdit);
            Check(!(await runtime.SetToggleAsync(FeatureId.Hp, true)).Success && memory.Writes.Count == 0);
            Check((await runtime.TryCloseAsync()).Success);
        });
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Unified runtime assertion failed"); }
    private static TrainerSnapshot Snapshot(int? pid = 42, ConnectionState connection = ConnectionState.Ready, bool ready = true) =>
        new(connection, "fixture", pid, true, FeatureCatalog.All.ToDictionary(feature => feature.Id, _ => new FeatureObservation(CanEdit: ready)));
    private sealed class Fixture : ITrainerRuntime
    {
        public TrainerSnapshot Current { get; set; } = Snapshot();
        public event Action<TrainerSnapshot>? SnapshotChanged;
        public List<string> Writes { get; } = [];
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
