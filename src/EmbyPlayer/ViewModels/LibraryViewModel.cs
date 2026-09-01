using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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

        _ = LoadItemsAsync();
    }

    partial void OnSelectedSortByChanged(SortOption? value)
    {
        if (SelectedView is not null && value is not null)
        {
            _ = LoadItemsAsync();
        }
    }

    partial void OnSortAscendingChanged(bool value)
    {
        OnPropertyChanged(nameof(SortOrderDisplay));
        if (SelectedView is not null && SelectedSortBy is not null)
        {
            _ = LoadItemsAsync();
        }
    }

    private async Task LoadItemsAsync()
    {
        var view = SelectedView;
        if (view is null) return;

        var sortBy = SelectedSortBy?.Key ?? "SortName";
        var sortOrder = SortAscending ? "Ascending" : "Descending";

        ErrorMessage = null;
        IsLoading = true;
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
                sortBy: sortBy, sortOrder: sortOrder);
            Items = new ObservableCollection<ItemCard>(result.Items.Select(i => ItemCardMapper.ToCard(_api, i)));
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

    public void OpenItem(ItemCard card) => _navigation.NavigateTo<DetailPage>(card.Id);
}
