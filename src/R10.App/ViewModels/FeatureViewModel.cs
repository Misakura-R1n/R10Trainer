using System.Globalization;
using R10.Core;

namespace R10.App.ViewModels;

public sealed class FeatureViewModel : ObservableObject
{
    private readonly ITrainerRuntime _runtime;
    private readonly Action<string> _status;
    private readonly Func<decimal, Task<bool>> _confirmStar;
    private FeatureObservation _observation = new();
    private BattleEditContext? _battle;
    private string _draft = "";
    private string? _operationError;
    private bool _dirty, _editing, _busy, _helpOpen, _canCancel;
    public FeatureDefinition Definition { get; }
    public FeatureId Id => Definition.Id;
    public string Label => Id == FeatureId.Hp ? "HP 保护" : Definition.Label;
    public string Unit => Definition.Unit;
    public bool IsToggle => Definition.Kind == FeatureKind.Toggle;
    public bool IsNumber => Definition.Kind == FeatureKind.Number;
    public bool IsChoice => Definition.Kind == FeatureKind.Choice;
    public bool HasLock => Definition.HasLock;
    public bool HasHelp => !string.IsNullOrWhiteSpace(Help);
    public string Help => _observation.Help ?? Definition.Help;
    public bool HelpOpen { get => _helpOpen; set => Set(ref _helpOpen, value); }
    public string Draft { get => _draft; set { if (Set(ref _draft, value)) { _dirty = true; _operationError = null; RefreshState(); } } }
    public IReadOnlyList<int> StarChoices { get; } = new[] { 10, 40, 80, 200 };
    private int _starTarget = 80;
    public int StarTarget { get => _starTarget; set => Set(ref _starTarget, value); }
    public bool Requested => _observation.Requested;
    public bool LockRequested => _observation.LockRequested;
    public string LockGlyph => LockRequested ? "\uE72E" : "\uE785";
    public bool CanEdit => !_busy && _observation.CanEdit;
    public bool CanToggle => !_busy && (_observation.CanEdit || (_canCancel && Requested));
    public bool CanLock => !_busy && (_observation.CanEdit || (_canCancel && LockRequested));
    public string? Error => _operationError ?? _observation.Error;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool HasState => _observation.StatusText is not null || (IsToggle ? Requested : LockRequested);
    public string StateText => _observation.StatusText ?? (IsToggle
        ? Requested ? (_observation.Active ? "已应用" : PendingText("等待应用")) : ""
        : LockRequested ? (_observation.LockActive ? "锁定中" : PendingText("等待锁定")) : "");
    public string StateDescription
    {
        get
        {
            var detail = _observation.Detail;
            var verification = IsToggle && Requested && _observation.Active
                ? _observation.ActiveDescription ?? "当前内存与目标状态一致，不代表游戏内效果已验证。"
                : LockRequested && _observation.LockActive
                    ? "正在按已捕获的数值维护内存，不代表游戏内效果已验证。"
                    : null;
            return string.IsNullOrWhiteSpace(verification) ? detail ?? StateText
                : string.IsNullOrWhiteSpace(detail) ? verification : detail + "\n" + verification;
        }
    }
    public string ValueDescription => _observation.Value is null ? _observation.Detail ?? "当前值未知" : "当前值：" + Format(_observation.Value.Value) + Unit;
    public string? CurrentReading
    {
        get
        {
            var reading = _observation.Value is { } value && _observation.Maximum is { } max
                ? $"{Format(value)} / {Format(max)}" : null;
            if (!FeatureCatalog.IsBattleHp(Id) || _observation.CanEdit || _observation.Detail is not { } detail) return reading;
            return reading is null ? detail : $"{reading} · {detail}";
        }
    }
    public AsyncCommand ToggleCommand { get; }
    public AsyncCommand LockCommand { get; }
    public AsyncCommand ApplyCommand { get; }
    public RelayCommand HelpCommand { get; }
    public FeatureViewModel(FeatureDefinition definition, ITrainerRuntime runtime, Action<string> status, Func<decimal, Task<bool>> confirmStar)
    {
        Definition = definition; _runtime = runtime; _status = status; _confirmStar = confirmStar;
        ToggleCommand = new AsyncCommand(() => RunAsync(() => _runtime.SetToggleAsync(Id, !Requested), "请求已更新"), () => CanToggle);
        LockCommand = new AsyncCommand(() => RunAsync(() => _runtime.SetLockAsync(Id, !LockRequested), "锁定请求已更新"), () => CanLock);
        ApplyCommand = new AsyncCommand(ApplyAsync, () => CanEdit);
        HelpCommand = new RelayCommand(() => HelpOpen = !HelpOpen);
    }
    public void Update(FeatureObservation observation, bool canCancel, BattleEditContext? battle = null)
    {
        if (FeatureCatalog.IsBattleHp(Id) && _battle != battle)
        {
            if (_dirty) _operationError = "战斗目标已变化，请重新输入。";
            _dirty = false;
            _editing = false;
            _battle = battle;
        }
        if (IsToggle && _observation.Error is not null && observation.Error is null && observation.Requested == observation.Active)
            _operationError = null;
        _observation = observation; _canCancel = canCancel;
        if (!_dirty && !_editing && IsNumber) { _draft = observation.Value is { } value ? Format(value) : ""; Changed(nameof(Draft)); }
        RefreshState();
    }
    public void ResetForSession()
    {
        _dirty = false; _editing = false; _draft = ""; _operationError = null; _battle = null;
        Changed(nameof(Draft)); RefreshState();
    }
    public void SetEditing(bool editing) => _editing = editing;
    public void CancelDraft()
    {
        _dirty = false; _operationError = null; _draft = _observation.Value is { } value ? Format(value) : "";
        Changed(nameof(Draft)); RefreshState();
    }
    private string Format(decimal value) => value.ToString(Definition.Step < 1 ? "0.#" : "0", CultureInfo.InvariantCulture);
    private string PendingText(string fallback) => string.IsNullOrWhiteSpace(_observation.Detail) ? fallback : _observation.Detail;
    private async Task ApplyAsync()
    {
        decimal value;
        if (IsChoice) value = StarTarget;
        else if (!decimal.TryParse(Draft, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
        { _operationError = "请输入有效数值。"; RefreshState(); return; }
        if (!FeatureCatalog.ValidValue(Id, value) || value > _observation.Maximum)
        {
            _operationError = IsChoice ? "请选择 10、40、80 或 200。" : $"请输入 {Definition.Minimum}～{_observation.Maximum ?? Definition.Maximum}，步长 {Definition.Step}。";
            RefreshState(); return;
        }
        if (FeatureCatalog.IsBattleHp(Id))
        {
            var battle = _battle;
            if (battle is null || battle != _runtime.Current.Battle)
            { _operationError = "战斗目标已变化，请重新输入。"; RefreshState(); return; }
            if (await RunAsync(() => _runtime.SetBattleHpAsync(Id, value, battle)) && battle == _battle)
            { _dirty = false; _draft = Format(value); Changed(nameof(Draft)); }
            return;
        }
        GameLoadEpoch? expected = null;
        if (IsChoice)
        {
            var before = _runtime.Current;
            expected = before.LoadEpoch;
            if (!before.IsSimulation && expected is not { Ready: true })
            { _operationError = "游戏读档状态未确认，请等待连接稳定后重试。"; RefreshState(); return; }
            if (!await _confirmStar(value)) return;
            var after = _runtime.Current;
            if (before.IsSimulation != after.IsSimulation || expected != after.LoadEpoch || before.ProcessId != after.ProcessId)
            { _operationError = "游戏会话或读档状态已变化，请重新确认后应用。"; RefreshState(); return; }
        }
        var succeeded = await RunAsync(() => expected is not null && IsChoice
            ? _runtime.SetValueForEpochAsync(Id, value, expected) : _runtime.SetValueAsync(Id, value));
        if (succeeded) { _dirty = false; _draft = Format(value); Changed(nameof(Draft)); }
    }
    private async Task<bool> RunAsync(Func<Task<OperationResult>> operation, string successMessage = "数值已应用")
    {
        _busy = true; _operationError = null; RefreshState();
        try
        {
            var result = await operation();
            _operationError = result.Success ? null : result.Error ?? "操作未完成。";
            _status(result.Success ? $"{Label}：{successMessage}" : $"{Label}：{_operationError}");
            return result.Success;
        }
        catch (Exception ex) { _operationError = ex.Message; _status($"{Label}：{ex.Message}"); return false; }
        finally { _busy = false; RefreshState(); }
    }
    private void RefreshState()
    {
        foreach (var property in new[] { nameof(Requested), nameof(LockRequested), nameof(LockGlyph), nameof(CanEdit), nameof(CanToggle), nameof(CanLock), nameof(Error), nameof(HasError), nameof(HasState), nameof(StateText), nameof(StateDescription), nameof(ValueDescription), nameof(CurrentReading), nameof(Help), nameof(HasHelp) }) Changed(property);
        ToggleCommand?.Refresh(); LockCommand?.Refresh(); ApplyCommand?.Refresh();
    }
}
