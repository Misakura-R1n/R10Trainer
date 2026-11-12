using R10.App.ViewModels;
using R10.Bridge.Client;
using R10.Bridge.Runtime;
using R10.Core;

static class ConfirmationTests
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("unchanged feature snapshots do not notify the UI, but a new battle invalidates the draft", () =>
        {
            var epoch = new GameLoadEpoch(42, "42:original", 3, true);
            var battle = new BattleEditContext(epoch, 7);
            var vm = new FeatureViewModel(FeatureCatalog.Get(FeatureId.EnemyHp), new Fixture(epoch),
                _ => { }, _ => Task.FromResult(false));
            var observation = new FeatureObservation(Value: 80, Maximum: 100, CanEdit: true);
            vm.Update(observation, true, battle);
            int notifications = 0;
            vm.PropertyChanged += (_, _) => notifications++;
            vm.ApplyCommand.CanExecuteChanged += (_, _) => notifications++;
            vm.Update(observation with { }, true, battle with { });
            Check(notifications == 0 && vm.Draft == "80");

            vm.Draft = "75";
            notifications = 0;
            vm.Update(observation, true, battle with { Id = 8 });
            Check(notifications > 0 && vm.Draft == "80" && vm.HasError);
            return Task.CompletedTask;
        });
        await test("battle HP draft is bound to the displayed battle and current limit", async () =>
        {
            var epoch = new GameLoadEpoch(42, "42:original", 3, true);
            var first = new BattleEditContext(epoch, 7);
            var next = first with { Id = 8 };
            var runtime = new Fixture(epoch);
            runtime.Current = runtime.Current with { Battle = first };
            var vm = new FeatureViewModel(FeatureCatalog.Get(FeatureId.EnemyHp), runtime, _ => { }, _ => Task.FromResult(false));
            vm.Update(new(Value: 80, Maximum: 100, CanEdit: true), true, first);
            vm.Draft = "101";
            vm.ApplyCommand.Execute(null);
            Check(vm.HasError && runtime.BattleWrite is null);
            vm.Draft = "75";
            runtime.Current = runtime.Current with { Battle = next };
            vm.ApplyCommand.Execute(null);
            Check(vm.HasError && runtime.BattleWrite is null);
            vm.Update(new(Value: 60, Maximum: 100, CanEdit: true), true, next);
            Check(vm.Draft == "60");
            vm.Draft = "70";
            var done = Completion(vm);
            vm.ApplyCommand.Execute(null);
            await done.WaitAsync(TimeSpan.FromSeconds(3));
            Check(runtime.BattleWrite == (next, 70m) && !vm.HasError);
        });
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
        await test("a collectible item can be granted without exposing role growth", () =>
        {
            var runtime = new Fixture(new GameLoadEpoch(42, "42:original", 3, true));
            var vm = new CardManagementViewModel(runtime, _ => Task.FromResult(false));
            vm.Update(runtime.Current);
            vm.SetActive(true);
            vm.Selected = vm.Rows.Single(card => card == Fixture.Item);
            Check(vm.GrantCommand.CanExecute(null) && vm.DetailLabel.Contains("加入卡组") && !vm.CanEditStar);
            vm.GrantCommand.Execute(null);
            Check(vm.Selected?.Owned == true && !vm.GrantCommand.CanExecute(null) && !vm.CanEditStar);
            return Task.CompletedTask;
        });
        await test("card catalog is reused across scene changes only within the same process session", () =>
        {
            var epoch = new GameLoadEpoch(42, "42:original", 3, true);
            var runtime = new Fixture(epoch);
            var snapshot = runtime.Current with { CardBatchReadAvailable = true };
            var vm = new CardManagementViewModel(runtime, _ => Task.FromResult(false));
            vm.Update(snapshot); vm.SetActive(true);
            Check(vm.Rows.Single(card => card.Index == Fixture.Item.Index) == Fixture.Item);
            vm.Update(snapshot with { CardContext = 6 });
            Check(vm.Rows.Single(card => card.Index == Fixture.Item.Index) == Fixture.Item with { Flags = 3 });
            vm.Update(snapshot with { CardContext = 7, LoadEpoch = epoch with { Session = "42:new" } });
            Check(vm.Rows.Single(card => card.Index == Fixture.Item.Index) == Fixture.Item &&
                runtime.CardReads.SequenceEqual(new[] { CardOperation.CatalogPage, CardOperation.OwnershipPage, CardOperation.CatalogPage }));
            return Task.CompletedTask;
        });
        await test("card star confirmation keeps the selected card key and rejects changed read generations", async () =>
        {
            var epoch = new GameLoadEpoch(42, "42:original", 3, true);
            foreach (bool loadDuringConfirmation in new[] { false, true })
            {
                var runtime = new Fixture(epoch);
                var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var vm = new CardManagementViewModel(runtime, text => { shown.SetResult(text); return answer.Task; });
                vm.Update(runtime.Current);
                vm.SetActive(true);
                vm.Selected = vm.Rows[0];
                vm.StarText = "80";
                Check(vm.ApplyStarCommand.CanExecute(null));
                var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(vm.Status) && vm.Status.Contains(loadDuringConfirmation ? "重新选择" : "已将"))
                        finished.TrySetResult();
                };
                vm.ApplyStarCommand.Execute(null);
                Check((await shown.Task.WaitAsync(TimeSpan.FromSeconds(3))).Contains(Fixture.OtherVersion.Id));
                if (loadDuringConfirmation) vm.Update(runtime.Current with { LoadEpoch = epoch with { Generation = 4 }, CardContext = 6 });
                answer.SetResult(true);
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(loadDuringConfirmation ? runtime.CardWrite is null :
                    runtime.CardWrite == (new CardAccess(epoch, 5), new CardRequest(CardOperation.SetStar, Fixture.Card.Index, Fixture.Card.Key, 80)));
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
    private sealed class Fixture(GameLoadEpoch epoch) : ITrainerRuntime, ICardManagementRuntime
    {
        internal static readonly CardEntry Card = new(0, "Lv42 兰斯", "兰斯", 7, "4C76343220C0BCCBB9.C0BCCBB9");
        internal static readonly CardEntry OtherVersion = new(1, "Lv45 兰斯", "兰斯", 7, "4C76343520C0BCCBB9.C0BCCBB9");
        internal static readonly CardEntry Item = new(2, "装备", "装备", 2, "D7B0B1B8.D7B0B1B8");
        public TrainerSnapshot Current { get; set; } = TrainerSnapshot.Empty with
        { ProcessId = epoch.ProcessId, LoadEpoch = epoch, CardManagementAvailable = true, CardContext = 5 };
        internal (CardAccess, CardRequest)? CardWrite;
        internal (BattleEditContext, decimal)? BattleWrite;
        internal List<CardOperation> CardReads { get; } = [];
        public Task<CardReply> ExecuteCardAsync(CardAccess expected, CardRequest request)
        {
            if (request.Writes) CardWrite = (expected, request);
            if (request.IsPage)
            {
                CardReads.Add(request.Operation);
                if (request.Operation == CardOperation.OwnershipPage)
                    return Task.FromResult(new CardReply(3, []) { Ownership = new(0, "111") });
                return Task.FromResult(new CardReply(3, [Card, OtherVersion, Item]));
            }
            if (request.Index == Item.Index)
                return Task.FromResult(new CardReply(3, [request.Operation == CardOperation.Grant ? Item with { Flags = 3 } : Item]));
            return Task.FromResult(new CardReply(3, [Card], request.Writes ? request.Star : 10, 0, 259, 999));
        }
        public event Action<TrainerSnapshot>? SnapshotChanged { add { } remove { } }
        public int ConditionalWrites, UnconditionalWrites;
        public GameLoadEpoch? Expected;
        public Task<OperationResult> SetValueForEpochAsync(FeatureId id, decimal value, GameLoadEpoch expected)
        { ConditionalWrites++; Expected = expected; return Task.FromResult(OperationResult.Ok); }
        public Task<OperationResult> SetValueAsync(FeatureId id, decimal value)
        { UnconditionalWrites++; return Task.FromResult(OperationResult.Ok); }
        public Task<OperationResult> SetBattleHpAsync(FeatureId id, decimal value, BattleEditContext expected)
        { BattleWrite = (expected, value); return Task.FromResult(OperationResult.Ok); }
        public Task StartAsync() => Task.CompletedTask;
        public Task<OperationResult> SetToggleAsync(FeatureId id, bool enabled) => Task.FromResult(OperationResult.Ok);
        public Task<OperationResult> SetLockAsync(FeatureId id, bool enabled) => Task.FromResult(OperationResult.Ok);
        public Task<OperationResult> TryCloseAsync() => Task.FromResult(OperationResult.Ok);
    }
}
