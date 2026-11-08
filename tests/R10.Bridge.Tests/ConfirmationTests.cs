using R10.App.ViewModels;
using R10.Core;

static class ConfirmationTests
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("star confirmation rejects load, restart, unavailable context and changed process", async () =>
        {
            var epoch = new GameLoadEpoch(42, "42:original", 3, true);
            foreach (var changed in new GameLoadEpoch?[] { epoch with { Generation = 4 },
                epoch with { Session = "42:restarted" }, epoch with { ProcessId = 43 },
                epoch with { Ready = false }, null })
            {
                var runtime = new Fixture(epoch);
                var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var vm = new FeatureViewModel(FeatureCatalog.Get(FeatureId.Star), runtime, _ => { }, _ => answer.Task);
                vm.Update(new(CanEdit: true), true);
                var done = Completion(vm);
                vm.ApplyCommand.Execute(null);
                runtime.Current = runtime.Current with { LoadEpoch = changed };
                answer.SetResult(true);
                await done.WaitAsync(TimeSpan.FromSeconds(3));
                Check(runtime.ConditionalWrites == 0 && runtime.UnconditionalWrites == 0 && vm.HasError);
            }
        });
        await test("star confirmation forwards original epoch and cancel never writes", async () =>
        {
            foreach (bool accept in new[] { false, true })
            {
                var epoch = new GameLoadEpoch(42, "42:original", 3, true);
                var runtime = new Fixture(epoch);
                var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var vm = new FeatureViewModel(FeatureCatalog.Get(FeatureId.Star), runtime, _ => { }, _ => answer.Task);
                vm.Update(new(CanEdit: true), true);
                var done = Completion(vm); vm.ApplyCommand.Execute(null); answer.SetResult(accept);
                await done.WaitAsync(TimeSpan.FromSeconds(3));
                Check(runtime.UnconditionalWrites == 0 && runtime.ConditionalWrites == (accept ? 1 : 0));
                if (accept) Check(ReferenceEquals(runtime.Expected, epoch) && !vm.HasError);
            }
        });
    }
    private static Task Completion(FeatureViewModel vm)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ApplyCommand.CanExecuteChanged += (_, _) => { if (vm.ApplyCommand.CanExecute(null)) done.TrySetResult(); };
        return done.Task;
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Confirmation assertion failed"); }
    private sealed class Fixture(GameLoadEpoch epoch) : ITrainerRuntime
    {
        public TrainerSnapshot Current { get; set; } = TrainerSnapshot.Empty with { ProcessId = epoch.ProcessId, LoadEpoch = epoch };
        public event Action<TrainerSnapshot>? SnapshotChanged { add { } remove { } }
        public int ConditionalWrites, UnconditionalWrites;
        public GameLoadEpoch? Expected;
        public Task<OperationResult> SetValueForEpochAsync(FeatureId id, decimal value, GameLoadEpoch expected)
        { ConditionalWrites++; Expected = expected; return Task.FromResult(OperationResult.Ok); }
        public Task<OperationResult> SetValueAsync(FeatureId id, decimal value)
        { UnconditionalWrites++; return Task.FromResult(OperationResult.Ok); }
        public Task StartAsync() => Task.CompletedTask;
        public Task<OperationResult> SetToggleAsync(FeatureId id, bool enabled) => Task.FromResult(OperationResult.Ok);
        public Task<OperationResult> SetLockAsync(FeatureId id, bool enabled) => Task.FromResult(OperationResult.Ok);
        public Task<OperationResult> TryCloseAsync() => Task.FromResult(OperationResult.Ok);
    }
}
