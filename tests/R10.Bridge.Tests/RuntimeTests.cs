using R10.Bridge.Client;
using R10.Bridge.Runtime;
using R10.Core;

static class RuntimeTests
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("treasure discovery requires its component capability and closes the owned toggle", async () =>
        {
            var client = new RuntimeFixture();
            var runtime = new BridgeTrainerRuntime(client);
            Check(!(await runtime.SetToggleAsync(FeatureId.Treasure, true)).Success && client.Writes.Count == 0);
            client.State = client.State with { Capabilities = 15 | BridgeState.TreasureFindCapability, TreasureFindAvailable = true };
            Check((await runtime.SetToggleAsync(FeatureId.Treasure, true)).Success);
            Check(runtime.Current.Features[FeatureId.Treasure] is { Requested: true, Active: true, CanEdit: true });
            Check((await runtime.SetToggleAsync(FeatureId.Treasure, false)).Success && !client.State.TreasureFind);
            Check((await runtime.SetToggleAsync(FeatureId.Treasure, true)).Success);
            Check((await runtime.TryCloseAsync()).Success && !client.State.TreasureFind);
            Check(client.FeatureWrites.SequenceEqual(new[] { "treasure_find:True", "treasure_find:False", "treasure_find:True", "treasure_find:False" }));
        });
        await test("component cancellation queued behind polling retains its original session", async () =>
        {
            foreach (bool unified in new[] { false, true })
            foreach (bool restart in new[] { false, true })
            {
                var client = new RuntimeFixture { State = Ready() with { HpGuard = true } };
                var bridge = new BridgeTrainerRuntime(client);
                ITrainerRuntime runtime = unified
                    ? new UnifiedTrainerRuntime(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "R10-cancellation-fixture")),
                        _ => (new FaultedMemoryFixture(), bridge)) : bridge;
                var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<BridgeState>(TaskCreationOptions.RunContinuationsAsynchronously);
                bridge.SnapshotChanged += snapshot =>
                {
                    if (snapshot.LoadEpoch is { Ready: true }) observed.TrySetResult();
                };
                try
                {
                    await runtime.StartAsync();
                    await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    int blocked = 0;
                    client.Read = () =>
                    {
                        if (Interlocked.Exchange(ref blocked, 1) != 0) return Task.FromResult(client.State);
                        entered.TrySetResult();
                        return release.Task;
                    };
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var pending = runtime.SetToggleAsync(FeatureId.Hp, false);
                    Check(!pending.IsCompleted);
                    client.State = client.State with { Session = restart ? "42:restarted" : client.State.Session, LoadGeneration = 4 };
                    release.SetResult(client.State);
                    Check((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Success == !restart);
                    Check(client.Writes.Count == (restart ? 0 : 1) && client.State.HpGuard == restart);
                    if (restart)
                    {
                        Check((await runtime.SetToggleAsync(FeatureId.Hp, false)).Success);
                        Check(client.Writes.SequenceEqual(new[] { false }) && !client.State.HpGuard);
                    }
                }
                finally
                {
                    release.TrySetResult(client.State);
                    Check((await runtime.TryCloseAsync()).Success);
                }
            }
        });
        await test("confirmed value checks original authority after asynchronous state read", async () =>
        {
            var expected=new GameLoadEpoch(42,"42:123",3,true);
            foreach (var changed in new[] { Ready() with { LoadGeneration=4 }, Ready() with { Session="42:456" }, Ready() with { Pid=43 }, Ready() })
            {
                var read=new TaskCompletionSource<BridgeState>(TaskCreationOptions.RunContinuationsAsynchronously);
                var client=new RuntimeFixture { Read=()=>read.Task };
                var runtime=new BridgeTrainerRuntime(client);
                var pending=runtime.SetValueForEpochAsync(FeatureId.Star,80,expected);
                client.State=changed with { Capabilities=495,DataReady=true };
                read.SetResult(client.State);
                var result=await pending;
                bool same=changed.Pid==42 && changed.Session==expected.Session && changed.LoadGeneration==3;
                Check(result.Success==same && client.ValueWrites.Count==(same ? 1 : 0));
                Check((await runtime.TryCloseAsync()).Success);
            }
        });
        await test("typed data uses game readiness, enforces full exclusion, and closes owned full flag", async () =>
        {
            var client=new RuntimeFixture { State=Ready() with { Capabilities=495,DataReady=true,Food=1,Medal=2,Friendship=0,Unit=50 } };
            var runtime=new BridgeTrainerRuntime(client);
            Check((await runtime.SetValueAsync(FeatureId.Food,3)).Success && client.ValueWrites.Count==1);
            Check(!(await runtime.SetValueAsync(FeatureId.Star,99)).Success && client.ValueWrites.Count==1);
            Check((await runtime.SetToggleAsync(FeatureId.Resources,true)).Success);
            Check(!runtime.Current.Features[FeatureId.Food].CanEdit && runtime.Current.Features[FeatureId.Unit].CanEdit);
            Check(!(await runtime.SetValueAsync(FeatureId.Medal,0)).Success && client.ValueWrites.Count==1);
            Check((await runtime.SetValueAsync(FeatureId.Star,200)).Success && client.ValueWrites.Last()=="star:200:3");
            client.State=client.State with { DataReady=false };
            Check(!(await runtime.SetValueAsync(FeatureId.Unit,200)).Success && client.ValueWrites.Count==2);
            Check((await runtime.TryCloseAsync()).Success && !client.State.ResourcesFull);
        });
        await test("typed unknown outcome is reported without retry or rollback", async () =>
        {
            var client=new RuntimeFixture { State=Ready() with { Capabilities=495,DataReady=true },ValueError=new BridgeException("lost","fixture",true) };
            var runtime=new BridgeTrainerRuntime(client);
            var result=await runtime.SetValueAsync(FeatureId.Star,40);
            Check(!result.Success && result.Error!.Contains("结果未知") && client.ValueWrites.Count==1);
            Check((await runtime.TryCloseAsync()).Success && client.ValueWrites.Count==1);
        });
        await test("script branch capabilities and independent close ownership", async () =>
        {
            var client=new RuntimeFixture(); var runtime=new BridgeTrainerRuntime(client);
            Check(!(await runtime.SetToggleAsync(FeatureId.Redraw,true)).Success && client.Writes.Count==0);
            client.State=client.State with { Capabilities=239 };
            Check((await runtime.SetToggleAsync(FeatureId.Redraw,true)).Success && runtime.Current.Features[FeatureId.Redraw].Active);
            Check((await runtime.SetToggleAsync(FeatureId.Redistribute,true)).Success && runtime.Current.Features[FeatureId.Redistribute].Active);
            Check((await runtime.SetToggleAsync(FeatureId.Redraw,false)).Success && runtime.Current.Features[FeatureId.Redistribute].Active);
            Check((await runtime.TryCloseAsync()).Success);
            Check(client.FeatureWrites.SequenceEqual(new[]{"food_redraw:True","auto_redistribute:True","food_redraw:False","auto_redistribute:False"}));
        });
        await test("existing game patch owns both reroll rules", async () =>
        {
            var client=new RuntimeFixture { State=Ready() with { Capabilities=5935 } };
            var runtime=new BridgeTrainerRuntime(client);
            Check((await runtime.SetToggleAsync(FeatureId.Hp,true)).Success);
            foreach (var feature in new[] { FeatureId.Redraw, FeatureId.Redistribute })
            {
                var observation=runtime.Current.Features[feature];
                Check(!observation.CanEdit && !observation.Requested && observation.StatusText is not null);
                Check(!(await runtime.SetToggleAsync(feature,true)).Success);
            }
            Check((await runtime.TryCloseAsync()).Success);
            Check(client.FeatureWrites.SequenceEqual(new[]{"hp_guard:True","hp_guard:False"}));
        });
        await test("extra treasure pick needs its hook and closes independently of other card rules", async () =>
        {
            var client = new RuntimeFixture { State = Ready() with { Capabilities = 47, NoDuplicateCards = true } };
            var runtime = new BridgeTrainerRuntime(client);
            Check(!(await runtime.SetToggleAsync(FeatureId.ExtraTreasurePick, true)).Success && client.Writes.Count == 0);
            client.State = client.State with { Capabilities = 47 | BridgeState.ExtraTreasurePickCapability };
            Check((await runtime.SetToggleAsync(FeatureId.ExtraTreasurePick, true)).Success);
            Check(runtime.Current.Features[FeatureId.ExtraTreasurePick].Active && runtime.Current.Features[FeatureId.ExtraTreasurePick].CanEdit);
            Check((await runtime.TryCloseAsync()).Success && !client.State.ExtraTreasurePick && client.State.NoDuplicateCards);
            Check(client.FeatureWrites.SequenceEqual(new[] { "extra_treasure_pick:True", "extra_treasure_pick:False" }));
        });
        await test("card capability, ownership and HP close independently", async () =>
        {
            var client=new RuntimeFixture(); var runtime=new BridgeTrainerRuntime(client);
            Check(!(await runtime.SetToggleAsync(FeatureId.NoDuplicateCards,true)).Success && client.Writes.Count==0);
            client.State=client.State with { Capabilities=33 };
            Check((await runtime.SetToggleAsync(FeatureId.NoDuplicateCards,true)).Success);
            Check(runtime.Current.Connection==ConnectionState.Ready && runtime.Current.Features[FeatureId.NoDuplicateCards].CanEdit && !runtime.Current.Features[FeatureId.Hp].CanEdit);
            Check((await runtime.SetToggleAsync(FeatureId.NoDuplicateCards,false)).Success);
            client.FeatureWrites.Clear(); client.Writes.Clear();
            client.State=client.State with { Capabilities=47 };
            Check((await runtime.SetToggleAsync(FeatureId.Hp,true)).Success);
            Check((await runtime.SetToggleAsync(FeatureId.NoDuplicateCards,true)).Success);
            Check((await runtime.SetToggleAsync(FeatureId.Hp,false)).Success);
            Check((await runtime.TryCloseAsync()).Success);
            Check(client.FeatureWrites.SequenceEqual(new[]{"hp_guard:True","no_duplicate_cards:True","hp_guard:False","no_duplicate_cards:False"}));
        });
        await test("card lost acknowledgement retains independent shutdown responsibility", async () =>
        {
            var client=new RuntimeFixture { State=Ready() with { Capabilities=47 } };
            client.Set=(_,_)=>Task.FromException<BridgeCommand>(new BridgeException("lost","lost",true));
            var runtime=new BridgeTrainerRuntime(client);
            Check(!(await runtime.SetToggleAsync(FeatureId.NoDuplicateCards,true)).Success);
            client.Set=(_,_)=>Task.FromResult(new BridgeCommand(2,"applied"));
            Check((await runtime.TryCloseAsync()).Success);
            Check(client.FeatureWrites.SequenceEqual(new[]{"no_duplicate_cards:True","no_duplicate_cards:False"}));
        });
        await test("runtime rejected enable does not take ownership of an external guard", async () =>
        {
            var client = new RuntimeFixture { State = Ready() with { HpGuard = true } };
            client.Set = (_, _) => Task.FromResult(new BridgeCommand(1, "rejected", "generation_or_phase_changed"));
            var runtime = new BridgeTrainerRuntime(client);
            Check(!(await runtime.SetToggleAsync(FeatureId.Hp, true)).Success);
            Check((await runtime.TryCloseAsync()).Success && client.Writes.SequenceEqual(new[] { true }));
        });
        await test("runtime lost enable acknowledgement keeps close responsibility", async () =>
        {
            var client = new RuntimeFixture();
            client.Set = (enabled, _) =>
            {
                client.State = client.State with { HpGuard = enabled };
                return enabled ? Task.FromException<BridgeCommand>(new BridgeException("timeout", "fixture lost acknowledgement", true))
                    : Task.FromResult(new BridgeCommand(2, "applied"));
            };
            var runtime = new BridgeTrainerRuntime(client);
            var enabled = await runtime.SetToggleAsync(FeatureId.Hp, true);
            Check(!enabled.Success && enabled.Error!.Contains("未知", StringComparison.Ordinal));
            Check((await runtime.TryCloseAsync()).Success && client.Writes.SequenceEqual(new[] { true, false }));
        });
        await test("runtime queued then failed result read is unknown and kept for close", async () =>
        {
            var client = new RuntimeFixture();
            client.Set = (enabled, _) => Task.FromResult(new BridgeCommand(enabled ? 1 : 2, enabled ? "queued" : "applied"));
            client.Result = (_, _) => Task.FromException<BridgeCommand>(new BridgeException("transport_error", "fixture lost result"));
            var runtime = new BridgeTrainerRuntime(client);
            var enabled = await runtime.SetToggleAsync(FeatureId.Hp, true);
            Check(!enabled.Success && enabled.Error!.Contains("未知", StringComparison.Ordinal));
            Check((await runtime.TryCloseAsync()).Success && client.Writes.SequenceEqual(new[] { true, false }));
        });
        await test("runtime applied command stays successful when following snapshot fails", async () =>
        {
            var client = new RuntimeFixture();
            client.Set = (_, _) => { client.ReadError = new BridgeException("transport_error", "fixture snapshot failed"); return Task.FromResult(new BridgeCommand(1, "applied")); };
            var runtime = new BridgeTrainerRuntime(client);
            Check((await runtime.SetToggleAsync(FeatureId.Hp, true)).Success);
            client.ReadError = new BridgeException("game_not_running", "fixture exited");
            Check((await runtime.TryCloseAsync()).Success);
        });
        await test("runtime close prevents later writes while waiting for active command", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new RuntimeFixture();
            client.Set = async (enabled, _) => { if (enabled) { entered.SetResult(); await release.Task; } return new BridgeCommand(enabled ? 1 : 2, "applied"); };
            var runtime = new BridgeTrainerRuntime(client);
            var enabling = runtime.SetToggleAsync(FeatureId.Hp, true); await entered.Task;
            var closing = runtime.TryCloseAsync();
            var later = runtime.SetToggleAsync(FeatureId.Hp, true);
            Check(later.IsCompleted && !(await later).Success);
            release.SetResult();
            Check((await enabling).Success && (await closing).Success && client.Writes.SequenceEqual(new[] { true, false }));
            Check(runtime.Current.Connection == ConnectionState.Closed);
        });
        await test("runtime close never disables a restarted process", async () =>
        {
            var client = new RuntimeFixture(); var runtime = new BridgeTrainerRuntime(client);
            Check((await runtime.SetToggleAsync(FeatureId.Hp, true)).Success);
            client.State = client.State with { Session = "42:456", HpGuard = true };
            Check((await runtime.TryCloseAsync()).Success && client.Writes.SequenceEqual(new[] { true }));
        });
        await test("runtime failed close stays open and can be retried", async () =>
        {
            var client = new RuntimeFixture(); var runtime = new BridgeTrainerRuntime(client);
            Check((await runtime.SetToggleAsync(FeatureId.Hp, true)).Success);
            client.Set = (_, _) => Task.FromException<BridgeCommand>(new BridgeException("bridge_rejected", "game_not_ready"));
            Check(!(await runtime.TryCloseAsync()).Success && runtime.Current.Connection != ConnectionState.Closed);
            client.Set = (_, _) => Task.FromResult(new BridgeCommand(3, "applied"));
            Check((await runtime.TryCloseAsync()).Success);
        });
        await test("runtime hello-only and unknown phase never publish ready", async () =>
        {
            foreach (var state in new[] { Ready() with { Capabilities = 1, BlockedHits = null }, Ready() with { Phase = 0 } })
            {
                var client = new RuntimeFixture { State = state }; var runtime = new BridgeTrainerRuntime(client);
                var published = new TaskCompletionSource<TrainerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                runtime.SnapshotChanged += snapshot => published.TrySetResult(snapshot);
                await runtime.StartAsync();
                var snapshot = await published.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(snapshot.Connection != ConnectionState.Ready && !snapshot.Features[FeatureId.Hp].CanEdit);
                if (state.BlockedHits is null) Check(snapshot.Features[FeatureId.Hp].Detail!.Contains("?", StringComparison.Ordinal));
                Check((await runtime.TryCloseAsync()).Success && client.Writes.Count == 0);
            }
        });
        await test("runtime stale heartbeat rejects writes", async () =>
        {
            var client = new RuntimeFixture { State = Ready() with { HeartbeatAgeMs = 2001 } };
            var runtime = new BridgeTrainerRuntime(client);
            Check(!(await runtime.SetToggleAsync(FeatureId.Hp, true)).Success && client.Writes.Count == 0);
            Check((await runtime.TryCloseAsync()).Success);
        });
        await test("runtime unexpected poll exception publishes error and resumes observations", async () =>
        {
            var client = new RuntimeFixture { ReadError = new InvalidOperationException("fixture unexpected discovery error") };
            var runtime = new BridgeTrainerRuntime(client);
            var errorSeen = new TaskCompletionSource<TrainerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var recovered = new TaskCompletionSource<TrainerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.SnapshotChanged += snapshot =>
            {
                if (snapshot.Connection == ConnectionState.Error) errorSeen.TrySetResult(snapshot);
                if (snapshot.Connection == ConnectionState.Ready) recovered.TrySetResult(snapshot);
            };
            await runtime.StartAsync();
            Check((await errorSeen.Task.WaitAsync(TimeSpan.FromSeconds(3))).Error!.Contains("fixture unexpected", StringComparison.Ordinal));
            client.ReadError = null;
            Check((await recovered.Task.WaitAsync(TimeSpan.FromSeconds(3))).Features[FeatureId.Hp].CanEdit);
            Check((await runtime.TryCloseAsync()).Success && client.Writes.Count == 0);
        });
        await test("runtime bad snapshot subscriber cannot stop other observers or close", async () =>
        {
            var client = new RuntimeFixture(); var runtime = new BridgeTrainerRuntime(client);
            var twice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observations = 0;
            runtime.SnapshotChanged += _ => throw new InvalidOperationException("fixture observer failure");
            runtime.SnapshotChanged += _ => { if (Interlocked.Increment(ref observations) >= 2) twice.TrySetResult(); };
            await runtime.StartAsync();
            await twice.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(runtime.Current.Connection == ConnectionState.Ready);
            Check((await runtime.TryCloseAsync()).Success && runtime.Current.Connection == ConnectionState.Closed && client.Writes.Count == 0);
        });
    }

    private static BridgeState Ready() => new(42, 9, 10, 10, 3, 1, false, 0, "none", null, null, null,
        HeartbeatAgeMs: 0, BlockedHits: 0, ScriptVersion: 1, Capabilities: 15) { Session = "42:123" };
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Runtime assertion failed"); }

    private sealed class FaultedMemoryFixture : ITrainerRuntime
    {
        public TrainerSnapshot Current { get; } = TrainerSnapshot.Empty with { ProcessId = 42, Connection = ConnectionState.Error };
        public event Action<TrainerSnapshot>? SnapshotChanged { add { } remove { } }
        public Task StartAsync() => Task.CompletedTask;
        public Task<OperationResult> TryCloseAsync() => Task.FromResult(OperationResult.Ok);
        public Task<OperationResult> SetValueAsync(FeatureId id, decimal value) => throw new InvalidOperationException("Memory writes must remain blocked.");
        public Task<OperationResult> SetToggleAsync(FeatureId id, bool enabled) => throw new InvalidOperationException("Component cancellation must use the bridge.");
        public Task<OperationResult> SetLockAsync(FeatureId id, bool enabled) => throw new InvalidOperationException("Memory writes must remain blocked.");
    }

    private sealed class RuntimeFixture : IBridgeClient
    {
        public BridgeState State { get; set; } = Ready();
        public Exception? ReadError { get; set; }
        public Func<Task<BridgeState>>? Read { get; set; }
        public Func<bool, CancellationToken, Task<BridgeCommand>>? Set { get; set; }
        public Func<long, CancellationToken, Task<BridgeCommand>>? Result { get; set; }
        public List<string> ValueWrites { get; } = [];
        public BridgeException? ValueError;
        public Task<BridgeCommand> SetValueForSessionAsync(string session,string feature,int value,long generation,CancellationToken cancellationToken=default)
        {
            if(session!=Session) throw new BridgeException("session_changed","fixture");
            ValueWrites.Add($"{feature}:{value}:{generation}");
            if(ValueError is not null) throw ValueError;
            return Task.FromResult(new BridgeCommand(ValueWrites.Count,"applied"));
        }
        public List<bool> Writes { get; } = [];
        public List<string> FeatureWrites { get; } = [];
        public int ProcessId => State.Pid;
        public string? Session => State.Session;
        public bool IsSimulation => true;
        public Task<BridgeState> GetStateAsync(CancellationToken cancellationToken = default) =>
            Read?.Invoke() ?? (ReadError is null ? Task.FromResult(State) : Task.FromException<BridgeState>(ReadError));
        public Task<BridgeCommand> SetFeatureForSessionAsync(string session, string feature, bool enabled, long generation, CancellationToken cancellationToken = default)
        {
            if (session != Session) throw new BridgeException("session_changed", "fixture restarted");
            FeatureWrites.Add(feature+":"+enabled); Writes.Add(enabled);
            if (Set is not null) return Set(enabled, cancellationToken);
            State = feature switch {
                "hp_guard"=>State with { HpGuard=enabled }, "no_duplicate_cards"=>State with { NoDuplicateCards=enabled },
                "resources_full"=>State with { ResourcesFull=enabled },
                "food_redraw"=>State with { FoodRedraw=enabled }, "auto_redistribute"=>State with { AutoRedistribute=enabled },
                "extra_treasure_pick"=>State with { ExtraTreasurePick=enabled },
                "treasure_find"=>State with { TreasureFind=enabled },
                _=>throw new InvalidOperationException("Unknown fixture feature") };
            return Task.FromResult(new BridgeCommand(Writes.Count, "applied"));
        }
        public Task<BridgeCommand> GetCommandResultForSessionAsync(string session, long id, CancellationToken cancellationToken = default) =>
            Result?.Invoke(id, cancellationToken) ?? Task.FromResult(new BridgeCommand(id, "applied"));
        public Task<BridgeCommand> SetFeatureAsync(string feature, bool enabled, long generation, CancellationToken cancellationToken = default) =>
            SetFeatureForSessionAsync(Session!, feature, enabled, generation, cancellationToken);
        public Task<BridgeCommand> GetCommandResultAsync(long id, CancellationToken cancellationToken = default) =>
            GetCommandResultForSessionAsync(Session!, id, cancellationToken);
        public Task PingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
