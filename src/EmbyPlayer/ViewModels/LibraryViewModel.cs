using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbyPlayer.Animations;
using EmbyPlayer.Models;
using EmbyPlayer.Services;
using EmbyPlayer.Views;

namespace EmbyPlayer.ViewModels;

/// <summary>表示 Emby API 的一个排序字段选项。</summary>
public record SortOption(string Key, string Display);

public partial class LibraryViewModel : ObservableObject
{
    private readonly EmbyApiClient _api;
    private readonly NavigationService _navigation;

    /// <summary>不同 CollectionType 下可用的排序字段。</summary>
    private static readonly Dictionary<string, SortOption[]> SortOptionsByType = new()
    {
        ["movies"] =
        [
            new("SortName", "名称"),
            new("ProductionYear", "年份"),
            new("CommunityRating", "评分"),
            new("DateCreated", "添加日期"),
            new("DatePlayed", "观看日期"),
            new("Runtime", "时长"),
        ],
        ["tvshows"] =
        [
            new("SortName", "名称"),
            new("ProductionYear", "年份"),
            new("CommunityRating", "评分"),
            new("DateCreated", "添加日期"),
            new("PremiereDate", "首播日期"),
        ],
        ["music"] =
        [
            new("SortName", "名称"),
            new("DateCreated", "添加日期"),
        ],
        ["boxsets"] =
        [
            new("SortName", "名称"),
            new("DateCreated", "添加日期"),
        ],
        ["homevideos"] =
        [
            new("SortName", "名称"),
            new("DateCreated", "添加日期"),
        ],
    };

    private static readonly SortOption[] DefaultSortOptions =
    [
        new("SortName", "名称"),
        new("DateCreated", "添加日期"),
    ];

    [ObservableProperty]
    private ObservableCollection<ViewInfo> _views = new();

    [ObservableProperty]
    private ViewInfo? _selectedView;

    [ObservableProperty]
    private ObservableCollection<ItemCard> _items = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private ObservableCollection<SortOption> _availableSortOptions = new();

    [ObservableProperty]
    private SortOption? _selectedSortBy;

    [ObservableProperty]
    private bool _sortAscending = true;

    /// <summary>升降序按钮的显示文本。</summary>
    public string SortOrderDisplay => SortAscending ? "↑ 升序" : "↓ 降序";

    public LibraryViewModel(EmbyApiClient api, NavigationService navigation)
    {
        _api = api;
        _navigation = navigation;
    }

    public async Task InitializeAsync()
    {
        ErrorMessage = null;
        IsLoading = true;
        try
        {
            var views = await _api.GetViewsAsync();
            Views = new ObservableCollection<ViewInfo>(
                views.Select(v => new ViewInfo(v.Id, v.Name, v.CollectionType)));
            if (SelectedView is null && Views.Count > 0)
            {
                SelectedView = Views[0];
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // 防抖 + 请求取消：切分类会连带改排序字段和升降序，三个属性变更各触发一次加载的话，
    // 列表会被整体替换三遍、GridView 重建三遍 —— 那正是「整页闪几下」的来源。
    private CancellationTokenSource? _reloadDelayCts;
    private CancellationTokenSource? _loadCts;

    /// <summary>合并连发的重载请求，120ms 内的多次变更只发一次网络请求。</summary>
    private void RequestReload()
    {
        _reloadDelayCts?.Cancel();
        var cts = new CancellationTokenSource();
        _reloadDelayCts = cts;
        IsLoading = true;
        _ = ReloadDebouncedAsync(cts);
    }

    private async Task ReloadDebouncedAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(120, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // 已被后续请求合并
        }

        if (!ReferenceEquals(_reloadDelayCts, cts))
        {
            return;
        }

        await LoadItemsAsync(cts.Token);
    }

    partial void OnSelectedViewChanged(ViewInfo? value)
    {
        if (value is null) return;

        // 根据 CollectionType 更新可用排序选项
        var options = SortOptionsByType.TryGetValue(value.CollectionType ?? "", out var opts)
            ? opts
            : DefaultSortOptions;
        AvailableSortOptions = new ObservableCollection<SortOption>(options);
        SelectedSortBy = options[0];
        SortAscending = true;

        RequestReload();
    }

    partial void OnSelectedSortByChanged(SortOption? value)
    {
        if (SelectedView is not null && value is not null)
        {
            RequestReload();
        }
    }

    partial void OnSortAscendingChanged(bool value)
    {
        OnPropertyChanged(nameof(SortOrderDisplay));
        if (SelectedView is not null && SelectedSortBy is not null)
        {
            RequestReload();
        }
    }

    private async Task LoadItemsAsync(CancellationToken ct)
    {
        var view = SelectedView;
        if (view is null) return;

        var sortBy = SelectedSortBy?.Key ?? "SortName";
        var sortOrder = SortAscending ? "Ascending" : "Descending";

        ErrorMessage = null;
        IsLoading = true;

        // 取消仍在飞行的上一次请求，避免两个响应先后覆盖 Items
        _loadCts?.Cancel();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loadCts = linked;

        try
        {
            var types = view.CollectionType switch
            {
                "movies" => "Movie",
                "tvshows" => "Series",
                "homevideos" => "Video",
                "music" => "Audio",
                "boxsets" => "BoxSet",
                _ => "Movie,Series"
            };
            var result = await _api.GetItemsAsync(view.Id, types,
                sortBy: sortBy, sortOrder: sortOrder, ct: linked.Token);
            if (linked.IsCancellationRequested)
            {
                return;
            }

            Items = new ObservableCollection<ItemCard>(result.Items.Select(i => ItemCardMapper.ToCard(_api, i)));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!linked.IsCancellationRequested)
            {
                ErrorMessage = ex.Message;
            }
        }
        finally
        {
            if (!linked.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    public void OpenItem(ItemCard card) => _navigation.NavigateTo<DetailPage>(card.Id, FluidNavigation.PosterPush);
}
