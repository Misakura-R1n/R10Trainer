using System.Globalization;
using R10.Bridge.Client;
using R10.Bridge.Runtime;
using R10.Core;

namespace R10.App.ViewModels;

public sealed class CardManagementViewModel : ObservableObject
{
    private readonly ICardManagementRuntime? _runtime;
    private readonly Func<string, string, Task<bool>> _confirm;
    private CardAccess? _access;
    private bool _active, _busy, _batchReads, _itemStarAvailable;
    private IReadOnlyList<CardEntry> _catalog = [], _rows = [], _cachedCatalog = [];
    private string? _catalogSession;
    private CardEntry? _selected;
    private CardReply? _detail;
    private ItemStarLevel? _itemStar;
    private string _search = "", _starText = "", _itemStarText = "", _status = "";
    private int _filter;

    public CardManagementViewModel(ITrainerRuntime runtime, Func<string, string, Task<bool>> confirm)
    {
        _runtime = runtime as ICardManagementRuntime;
        _confirm = confirm;
        RefreshCommand = new(RefreshAsync, () => CanBrowse);
        GrantCommand = new(GrantAsync, () => CanBrowse && _detail?.Rows[0].CanGrant == true);
        ApplyStarCommand = new(ApplyStarAsync, () => CanEditStar && ValidStar(out _));
        ApplyItemStarCommand = new(ApplyItemStarAsync, () => CanEditItemStar && ValidItemStar(out _));
    }

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand GrantCommand { get; }
    public AsyncCommand ApplyStarCommand { get; }
    public AsyncCommand ApplyItemStarCommand { get; }
    public bool IsConnected => _access is not null;
    public bool CanBrowse => IsConnected && !_busy;
    public bool HasSelection => Selected is not null;
    public bool CanEditStar => CanBrowse && _detail?.Star is not null;
    public bool CanEditItemStar => CanBrowse && _itemStarAvailable && _itemStar is not null;
    public string ItemStarLabel => _itemStar is { } level
        ? $"基础 ★{level.Base}　·　生效 ★{level.Effective}"
        : IsConnected && !_itemStarAvailable ? "需要新版游戏组件" : "等待读取物品★";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) FilterRows(); }
    }
    public int Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) FilterRows(); }
    }
    public string StarText
    {
        get => _starText;
        set { if (Set(ref _starText, value)) ApplyStarCommand.Refresh(); }
    }
    public string ItemStarText
    {
        get => _itemStarText;
        set { if (Set(ref _itemStarText, value)) ApplyItemStarCommand.Refresh(); }
    }
    public IReadOnlyList<CardEntry> Rows { get => _rows; private set => Set(ref _rows, value); }
    public string CountLabel => $"显示 {Rows.Count} / {_catalog.Count} 张";
    public CardEntry? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            ShowDetails(null);
            Changed(nameof(HasSelection));
            if (value is not null && CanBrowse) _ = InspectAsync(value);
        }
    }
    public string DetailLabel => _detail is { Star: { } star } info
        ? $"角色 {info.Rows[0].CharacterId}　·　★{star}　·　经验 {info.Experience} / {info.NextExperience}　·　上限 {info.MaxStar}"
        : _detail?.Rows[0] switch
        {
            { Owned: true } => "已拥有 · 此卡没有可编辑的角色★",
            { CanGrant: true, HasRoleGrowth: true } => "未拥有 · 获取后可查看角色成长数据",
            { CanGrant: true } => "未拥有 · 获取后加入卡组",
            not null => "此条目无法加入持久卡组",
            _ => ""
        };

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (!active || !CanBrowse) return;
        if (_catalog.Count == 0) _ = RefreshAsync();
        else if (_itemStarAvailable) _ = RunAsync(ReadItemStarAsync);
    }

    public void Update(TrainerSnapshot snapshot)
    {
        var next = _runtime is not null && snapshot.LoadEpoch is { Ready: true } epoch && snapshot.CardContext > 0
            ? new CardAccess(epoch, snapshot.CardContext) : null;
        _batchReads = snapshot.CardBatchReadAvailable;
        bool itemStarChanged = _itemStarAvailable != snapshot.ItemStarAvailable;
        _itemStarAvailable = snapshot.ItemStarAvailable;
        if (next is not null && _catalogSession != next.Epoch.Session)
        {
            _catalogSession = next.Epoch.Session;
            _cachedCatalog = [];
        }
        if (_access == next && next is not null)
        {
            if (itemStarChanged)
            {
                ShowItemStar(null);
                if (_active && CanBrowse && _itemStarAvailable) _ = RunAsync(ReadItemStarAsync);
            }
            return;
        }
        if (_access != next)
        {
            _access = next;
            Selected = null;
            _catalog = [];
            FilterRows();
            Changed(nameof(IsConnected));
            ShowItemStar(null);
        }
        if (next is null)
            Status = snapshot.IsSimulation ? "演示模式下显示页面布局。连接游戏后可读取卡片。"
                : snapshot.CardManagementAvailable ? "请进入存档并离开战斗、奖励选择或读档界面。"
                : "请安装支持卡片管理的组件，并启动所选游戏。";
        else if (_active && CanBrowse) _ = RefreshAsync();
    }

    private Task RefreshAsync() => RunAsync(async access =>
    {
        Selected = null;
        Status = "正在读取游戏卡牌表…";
        IReadOnlyList<CardEntry>? rows = null;
        if (_batchReads && _cachedCatalog.Count > 0)
            rows = await RefreshOwnershipAsync(access, _cachedCatalog);
        if (_access != access || !_active) return;
        rows ??= await ReadCatalogAsync(access, _batchReads ? CardOperation.CatalogPage : CardOperation.Page);
        if (rows is null || _access != access || !_active) return;
        _catalog = _cachedCatalog = rows;
        FilterRows();
        Status = "请选择要获取或调整的卡片版本。";
        if (_itemStarAvailable) await ReadItemStarAsync(access);
    });

    private async Task ReadItemStarAsync(CardAccess access)
    {
        ShowItemStar(null);
        var result = await _runtime!.ExecuteCardAsync(access, new(CardOperation.ReadItemStar, 0));
        if (_access == access && _itemStarAvailable) ShowItemStar(result.ItemStar);
    }

    private async Task<IReadOnlyList<CardEntry>?> ReadCatalogAsync(CardAccess access, CardOperation operation)
    {
        var rows = new List<CardEntry>();
        int total = 0;
        do
        {
            var page = await _runtime!.ExecuteCardAsync(access, new(operation, rows.Count));
            if (_access != access || !_active) return null;
            if (rows.Count != 0 && total != page.Total)
                throw new BridgeException("card_table_changed", "卡牌表已变化，请重新读取。");
            total = page.Total;
            rows.AddRange(page.Rows);
        } while (rows.Count < total);
        return rows;
    }

    private async Task<IReadOnlyList<CardEntry>?> RefreshOwnershipAsync(CardAccess access, IReadOnlyList<CardEntry> catalog)
    {
        var rows = new CardEntry[catalog.Count];
        int start = 0;
        while (start < rows.Length)
        {
            var page = await _runtime!.ExecuteCardAsync(access, new(CardOperation.OwnershipPage, start));
            if (_access != access || !_active) return null;
            if (page.Total != rows.Length)
            {
                _cachedCatalog = [];
                return null;
            }
            var flags = page.Ownership!.Flags;
            for (int i = 0; i < flags.Length; i++)
            {
                var card = catalog[start + i];
                bool owned = flags[i] == '1';
                rows[start + i] = card.Owned == owned ? card : card with { Flags = (card.Flags & ~1) | (owned ? 1 : 0) };
            }
            start += flags.Length;
        }
        return rows;
    }

    private Task InspectAsync(CardEntry card) => RunAsync(async access =>
    {
        var details = await _runtime!.ExecuteCardAsync(access, new(CardOperation.Inspect, card.Index, card.Key));
        if (_access == access && Selected?.Key == card.Key) ShowDetails(details);
    });

    private Task GrantAsync() => RunAsync(async access =>
    {
        if (Selected is not { } card || _detail?.Rows[0].CanGrant != true) return;
        var result = await _runtime!.ExecuteCardAsync(access, new(CardOperation.Grant, card.Index, card.Key));
        if (_access != access) return;
        var updated = result.Rows[0];
        _catalog = _catalog.Select(item => item.Index == updated.Index ? updated : item).ToArray();
        FilterRows();
        Selected = Rows.FirstOrDefault(item => item.Index == updated.Index);
        if (Selected is not null) ShowDetails(result);
        Status = $"已获取 {updated.Id}。请在游戏中保存进度。";
    }, () => ShowDetails(null));

    private Task ApplyStarAsync() => RunAsync(async access =>
    {
        if (Selected is not { } card || _detail?.Star is not { } current || !ValidStar(out int star)) return;
        var related = _catalog.Where(item => item.Owned && item.HasRoleGrowth && item.CharacterId == card.CharacterId).ToArray();
        string versions = string.Join("、", related.Take(5).Select(item => item.Id));
        if (related.Length > 5) versions += $" 等 {related.Length} 张";
        if (!await _confirm("修改角色★等级", $"将 {card.CharacterId} 的 ★ 从 {current} 设为 {star}。同角色版本共享 ★。当前持有 {versions}。修改会清空当前经验，可能影响继承。"))
            return;
        if (_access != access || Selected?.Key != card.Key)
        {
            Status = "游戏或读档状态已变化，请重新选择卡片。";
            return;
        }
        var result = await _runtime!.ExecuteCardAsync(access, new(CardOperation.SetStar, card.Index, card.Key, star));
        if (_access != access) return;
        ShowDetails(result);
        Status = $"已将 {card.CharacterId} 设为 ★{result.Star}。请在游戏中保存进度。";
    }, () => ShowDetails(null));

    private Task ApplyItemStarAsync() => RunAsync(async access =>
    {
        if (_itemStar is not { } level || !ValidItemStar(out int star)) return;
        if (!await _confirm("修改物品★等级", $"将共享物品★从 {level.Base} 设为 {star}。当前存档的装备与道具将按新星级重算属性。")) return;
        if (_access != access || !_itemStarAvailable)
        {
            Status = "游戏或读档状态已变化，请重新读取物品★。";
            return;
        }
        var result = await _runtime!.ExecuteCardAsync(access, new(CardOperation.SetItemStar, 0, Star: star));
        if (_access != access || !_itemStarAvailable) return;
        ShowItemStar(result.ItemStar);
        Status = $"已将共享物品★设为 {star}。请在游戏中保存进度。";
    }, () => ShowItemStar(null));

    private bool ValidStar(out int star) =>
        int.TryParse(StarText, NumberStyles.None, CultureInfo.InvariantCulture, out star) &&
        _detail is { Star: { } current } && star >= 0 && star <= _detail.MaxStar && star != current;

    private bool ValidItemStar(out int star) =>
        int.TryParse(ItemStarText, NumberStyles.None, CultureInfo.InvariantCulture, out star) &&
        _itemStar is { } level && star is >= 0 and <= CardRequest.MaximumItemStar && star != level.Base;

    private async Task RunAsync(Func<CardAccess, Task> action, Action? clearOnWriteFailure = null)
    {
        if (_busy || _access is not { } access) return;
        _busy = true;
        UpdateActions();
        try { await action(access); }
        catch (BridgeException error)
        {
            if (_access == access)
            {
                clearOnWriteFailure?.Invoke();
                Status = clearOnWriteFailure is not null && error.OutcomeUnknown ? "操作结果尚未确认，请刷新后核对。" : error.Message;
            }
        }
        finally
        {
            _busy = false;
            UpdateActions();
            if (_access != access && _active && CanBrowse) _ = RefreshAsync();
        }
    }

    private void FilterRows()
    {
        string term = Search.Trim();
        Rows = _catalog.Where(card => (Filter == 0 || card.Owned == (Filter == 1)) &&
            (card.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
             card.CharacterId.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (Selected is { } selected && !Rows.Any(card => card.Key == selected.Key)) Selected = null;
        Changed(nameof(CountLabel));
    }

    private void ShowDetails(CardReply? detail)
    {
        _detail = detail;
        StarText = detail?.Star?.ToString(CultureInfo.InvariantCulture) ?? "";
        Changed(nameof(DetailLabel));
        UpdateActions();
    }

    private void ShowItemStar(ItemStarLevel? level)
    {
        _itemStar = level;
        ItemStarText = level?.Base.ToString(CultureInfo.InvariantCulture) ?? "";
        Changed(nameof(ItemStarLabel));
        UpdateActions();
    }

    private void UpdateActions()
    {
        Changed(nameof(CanBrowse));
        Changed(nameof(CanEditStar));
        Changed(nameof(CanEditItemStar));
        RefreshCommand.Refresh();
        GrantCommand.Refresh();
        ApplyStarCommand.Refresh();
        ApplyItemStarCommand.Refresh();
    }
}
