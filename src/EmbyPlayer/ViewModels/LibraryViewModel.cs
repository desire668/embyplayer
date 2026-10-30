using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyPlayer.Animations;
using EmbyPlayer.Models;
using EmbyPlayer.Services;
using EmbyPlayer.Views;

namespace EmbyPlayer.ViewModels;

/// <summary>表示 Emby API 的一个排序字段选项。</summary>
public record SortOption(string Key, string Display);

/// <summary>从详情页点分类跳过来时带的条件：按该分类过滤，并优先切到对应类型的媒体库（CollectionType 为 "movies" / "tvshows"）。</summary>
public sealed record LibraryFilter(string? Genre, string? CollectionType = null);

public partial class LibraryViewModel : ObservableObject
{
    /// <summary>「概览」虚拟分类：媒体库默认页，Netflix 式横向分类列表；不是服务器上的真实视图。</summary>
    public static readonly ViewInfo OverviewView = new("__overview__", "概览", null);

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

    /// <summary>概览模式的横向分类区集合；每个区独立加载、独立滚动。</summary>
    [ObservableProperty]
    private ObservableCollection<MediaSection> _sections = new();

    /// <summary>当前是否在「概览」模式（横向分类列表）；false 时是某个分类的网格视图。</summary>
    public bool IsOverview => SelectedView?.Id == OverviewView.Id;

    public Microsoft.UI.Xaml.Visibility OverviewVisibility =>
        IsOverview ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility GridVisibility =>
        IsOverview ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>排序工具栏只在网格视图有意义，概览页隐藏。</summary>
    public Microsoft.UI.Xaml.Visibility SortBarVisibility =>
        IsOverview ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

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

    /// <summary>当前生效的分类过滤（来自详情页的分类胶囊）；null 表示不过滤。</summary>
    [ObservableProperty]
    private string? _genreFilter;

    public Microsoft.UI.Xaml.Visibility FilterVisibility =>
        string.IsNullOrEmpty(GenreFilter) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    partial void OnGenreFilterChanged(string? value)
    {
        OnPropertyChanged(nameof(FilterVisibility));
        if (!string.IsNullOrEmpty(value) && SelectedView is not null)
        {
            RequestReload();
        }
    }

    [RelayCommand]
    private void ClearGenreFilter()
    {
        if (string.IsNullOrEmpty(GenreFilter))
        {
            return;
        }
        GenreFilter = null;
        RequestReload();
    }

    public LibraryViewModel(EmbyApiClient api, NavigationService navigation)
    {
        _api = api;
        _navigation = navigation;
    }

    public async Task InitializeAsync(LibraryFilter? filter = null)
    {
        ErrorMessage = null;
        IsLoading = true;
        try
        {
            var views = await _api.GetViewsAsync();
            // 「概览」始终排在第一个：默认进横向分类列表，点具体分类再进网格
            Views = new ObservableCollection<ViewInfo>(
                new[] { OverviewView }.Concat(
                    views.Select(v => new ViewInfo(v.Id, v.Name, v.CollectionType))));

            // 分类过滤：先定过滤条件，再切到对应类型的媒体库（切换本身会触发一次加载）
            GenreFilter = string.IsNullOrWhiteSpace(filter?.Genre) ? null : filter!.Genre;

            var preferred = string.IsNullOrEmpty(filter?.CollectionType)
                ? null
                : Views.FirstOrDefault(v => v.CollectionType == filter!.CollectionType);

            // 带着分类过滤进来时不能停在概览（概览不响应过滤），退而求其次选第一个真实分类
            if (preferred is null && !string.IsNullOrEmpty(GenreFilter))
            {
                preferred = Views.FirstOrDefault(v => v != OverviewView);
            }

            if (preferred is not null)
            {
                SelectedView = preferred;
            }
            else if (SelectedView is null)
            {
                SelectedView = Views.Count > 0 ? Views[0] : null;
            }
            else if (SelectedView != OverviewView && Views.All(v => v.Id != SelectedView.Id))
            {
                // 之前选中的分类在这台服务器上不存在（如刚换过服务器）：回概览
                SelectedView = Views.Count > 0 ? Views[0] : null;
            }
            else if (SelectedView == OverviewView)
            {
                // 停在本来就是概览的位置：SelectedView 没变，属性变更不触发，
                // 手动补一次（刷新按钮 / 换服务器时走的正是这条路径）
                _ = LoadSectionsAsync();
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
        // 概览与网格两种模式的显隐由这一处统一广播
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(OverviewVisibility));
        OnPropertyChanged(nameof(GridVisibility));
        OnPropertyChanged(nameof(SortBarVisibility));

        if (value is null) return;

        if (IsOverview)
        {
            // 概览不走排序 / 网格加载，直接拉各分类的数据
            _ = LoadSectionsAsync();
            return;
        }

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
        if (view is null || IsOverview) return;

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
                sortBy: sortBy, sortOrder: sortOrder, genres: GenreFilter, ct: linked.Token);
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

    // ==================== 概览：横向分类列表 ====================

    /// <summary>概览各分类并行的取消源（刷新 / 切换时作废在途请求）。</summary>
    private CancellationTokenSource? _sectionsCts;

    /// <summary>概览页单分类最多取多少张卡：横排够看，又不至于一次拉全库。</summary>
    private const int SectionItemLimit = 30;

    /// <summary>概览里参与建区的 CollectionType（音乐等无海报墙意义的跳过）。</summary>
    private static readonly string[] SectionViewTypes = ["movies", "tvshows", "boxsets", "homevideos"];

    /// <summary>
    /// 搭概览的骨架：继续观看 + 每个媒体分类的「最新添加」。
    /// 各分类的卡片由独立任务并行填充，先出骨架后出内容，单个分类失败/为空就整行移除。
    /// </summary>
    private async Task LoadSectionsAsync()
    {
        _sectionsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _sectionsCts = cts;

        var sections = new ObservableCollection<MediaSection>();
        Sections = sections;

        var resume = new MediaSection("继续观看", MediaSectionViewAll.Resume,
            description: "上次看到的地方，接着往下看");
        sections.Add(resume);
        _ = FillResumeSectionAsync(resume, sections, cts.Token);

        foreach (var view in Views.Where(v => v != OverviewView &&
                                              SectionViewTypes.Contains(v.CollectionType ?? "")))
        {
            var section = new MediaSection($"最新{view.Name}", MediaSectionViewAll.View, view);
            sections.Add(section);
            _ = FillViewSectionAsync(section, sections, cts.Token);
        }
    }

    /// <summary>填充「继续观看」：断点卡片（含续播进度条）。</summary>
    private async Task FillResumeSectionAsync(MediaSection section,
        ObservableCollection<MediaSection> owner, CancellationToken ct)
    {
        try
        {
            var result = await _api.GetResumeAsync(limit: SectionItemLimit, ct: ct);
            if (ct.IsCancellationRequested) return;

            foreach (var item in result.Items)
            {
                section.Items.Add(ItemCardMapper.ToCard(_api, item));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // 单个分类失败不影响其它分类
        }
        finally
        {
            section.IsLoading = false;
        }

        RemoveIfEmpty(section, owner, ct);
    }

    /// <summary>填充某个媒体分类的「最新添加」。</summary>
    private async Task FillViewSectionAsync(MediaSection section,
        ObservableCollection<MediaSection> owner, CancellationToken ct)
    {
        var view = section.TargetView;
        if (view is null) return;

        try
        {
            var types = view.CollectionType switch
            {
                "movies" => "Movie",
                "tvshows" => "Series",
                "homevideos" => "Video",
                "boxsets" => "BoxSet",
                _ => "Movie,Series"
            };
            var result = await _api.GetItemsAsync(view.Id, types, limit: SectionItemLimit,
                sortBy: "DateCreated", sortOrder: "Descending", ct: ct);
            if (ct.IsCancellationRequested) return;

            foreach (var item in result.Items)
            {
                section.Items.Add(ItemCardMapper.ToCard(_api, item));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // 服务器上这个分类取不到：下面会因空集合整行移除
        }
        finally
        {
            section.IsLoading = false;
        }

        RemoveIfEmpty(section, owner, ct);
    }

    /// <summary>一张卡都没有的分类不留空行（取消中的除外，那是要被整体替换掉的旧数据）。</summary>
    private static void RemoveIfEmpty(MediaSection section,
        ObservableCollection<MediaSection> owner, CancellationToken ct)
    {
        if (!ct.IsCancellationRequested && section.Items.Count == 0)
        {
            owner.Remove(section);
        }
    }
}
