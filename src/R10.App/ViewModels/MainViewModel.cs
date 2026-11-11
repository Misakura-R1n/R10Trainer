using R10.Core;

namespace R10.App.ViewModels;

public sealed class NavigationItem(string key, string label, string glyph, Action<string> select) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Glyph { get; } = glyph;
    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public RelayCommand SelectCommand { get; } = new(() => select(key));
}
public sealed record FeatureGroupViewModel(string Title, string Glyph, IReadOnlyList<FeatureViewModel> Features);
public sealed class MainViewModel : ObservableObject
{
    private readonly ITrainerRuntime _runtime;
    private int? _pid;
    private bool _simulation;
    private string _page = "all", _status = "等待游戏连接", _notice = "正在查找 Rance10…", _footer = "准备就绪", _error = "";
    private bool _showNotice = true, _showError, _compactNavigation;
    public IReadOnlyDictionary<FeatureId, FeatureViewModel> Features { get; }
    public IReadOnlyList<NavigationItem> Navigation { get; }
    public FeatureGroupViewModel Battle { get; }
    public FeatureGroupViewModel Strategy { get; }
    public FeatureGroupViewModel Resources { get; }
    public FeatureGroupViewModel Growth { get; }
    public GameSetupViewModel GameSetup { get; }
    public CardManagementViewModel Cards { get; }
    public bool ShowCards => Page == "cards";
    private bool CardPageReady => ShowCards && Cards.IsConnected;
    public string PageStatus => CardPageReady ? "卡片管理已连接" : Status;
    public bool ShowPageNotice => ShowNotice && !CardPageReady;
    public bool ShowPageError => ShowError && !CardPageReady;
    public string Page { get => _page; private set => Set(ref _page, value); }
    public string Title => Navigation.First(x => x.Key == Page).Label;
    public string Subtitle => Page switch { "battle" => "战斗中的常用操作。", "strategy" => "管理回合、兵力与战局。", "resources" => "调整当前资源与发现加成。", "growth" => "调整成长数值与辅助功能。", "cards" => "按卡名或角色查找，获取指定版本或调整角色★。", _ => "把操作留在手边，继续你的冒险。" };
    public bool IsSimulation { get => _simulation; private set => Set(ref _simulation, value); }
    public string BuildLabel => IsSimulation ? "演示模式" : "v" + typeof(MainViewModel).Assembly.GetName().Version?.ToString(3);
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public bool ShowNotice { get => _showNotice; private set => Set(ref _showNotice, value); }
    public string Footer { get => _footer; private set => Set(ref _footer, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public bool ShowError { get => _showError; private set => Set(ref _showError, value); }
    public bool CompactNavigation { get => _compactNavigation; set => Set(ref _compactNavigation, value); }
    public event Action? NavigationChanged;
    public MainViewModel(ITrainerRuntime runtime, Func<decimal, Task<bool>> confirmStar, GameSetupViewModel gameSetup, Func<string, Task<bool>> confirmCardStar)
    {
        _runtime = runtime; GameSetup = gameSetup;
        Cards = new CardManagementViewModel(runtime, confirmCardStar);
        Features = FeatureCatalog.All.ToDictionary(x => x.Id, x => new FeatureViewModel(x, runtime, SetFooter, confirmStar));
        FeatureGroupViewModel Group(FeatureGroup group, string name, string glyph) => new(name, glyph, Features.Values.Where(x => x.Definition.Group == group).ToArray());
        Battle = Group(FeatureGroup.Battle, "战斗", "\uE945"); Strategy = Group(FeatureGroup.Strategy, "战略", "\uE7C1");
        Resources = Group(FeatureGroup.Resources, "资源", "\uE7B8"); Growth = Group(FeatureGroup.Growth, "成长与辅助", "\uE734");
        Navigation = new[] {
            new NavigationItem("all", "全部功能", "\uE80A", SelectPage), new NavigationItem("battle", "战斗", "\uE945", SelectPage),
            new NavigationItem("strategy", "战略", "\uE7C1", SelectPage), new NavigationItem("resources", "资源", "\uE7B8", SelectPage),
            new NavigationItem("growth", "成长与辅助", "\uE734", SelectPage),
            new NavigationItem("cards", "卡片管理", "\uE8A5", SelectPage)
        };
        Navigation[0].Selected = true;
        Update(runtime.Current);
    }
    public void SelectPage(string page)
    {
        if (!Navigation.Any(x => x.Key == page)) return;
        Page = page;
        foreach (var item in Navigation) item.Selected = item.Key == page;
        Cards.SetActive(ShowCards);
        Changed(nameof(Title)); Changed(nameof(Subtitle)); Changed(nameof(ShowCards));
        UpdatePageStatus();
        NavigationChanged?.Invoke();
    }
    public void Update(TrainerSnapshot snapshot)
    {
        if (_pid != snapshot.ProcessId)
        {
            foreach (var feature in Features.Values) feature.ResetForSession();
            _pid = snapshot.ProcessId;
        }
        IsSimulation = snapshot.IsSimulation; Changed(nameof(BuildLabel));
        Status = snapshot.IsSimulation && !snapshot.Status.Contains("演示", StringComparison.Ordinal) ? "演示 · " + snapshot.Status : snapshot.Status;
        ShowNotice = snapshot.Notice is not null || snapshot.Connection != ConnectionState.Ready;
        Notice = snapshot.Notice ?? (snapshot.Connection switch
        {
            ConnectionState.Searching => "等待游戏连接。启动受支持的中文 BugFix 版后，将自动识别。",
            ConnectionState.Loading => "游戏数据尚未就绪。已有请求会在适用场景恢复，也可以随时取消。",
            ConnectionState.Unsupported => "当前游戏版本不受支持，已停用修改功能。",
            ConnectionState.RestorePending => "正在恢复本实例的脚本补丁。恢复完成前将保留此窗口和连接。",
            ConnectionState.Error => "连接暂不可用，请查看下方错误信息。",
            ConnectionState.Closed => "连接已关闭。",
            _ => ""
        });
        Error = snapshot.Error ?? ""; ShowError = Error.Length > 0;
        var canCancel = snapshot.ProcessId.HasValue && snapshot.Connection != ConnectionState.Closed;
        foreach (var (id, feature) in Features) feature.Update(snapshot.Features.GetValueOrDefault(id) ?? new(), canCancel, snapshot.Battle);
        GameSetup.Observe(snapshot);
        Cards.Update(snapshot);
        UpdatePageStatus();
    }
    public void SetFooter(string value) => Footer = (IsSimulation ? "演示 · " : "") + value;
    public void SetError(string value) { Error = value; ShowError = value.Length > 0; UpdatePageStatus(); }
    private void UpdatePageStatus()
    {
        Changed(nameof(PageStatus));
        Changed(nameof(ShowPageNotice));
        Changed(nameof(ShowPageError));
    }
}
