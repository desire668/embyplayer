using System.Collections.ObjectModel;
using EmbyPlayer.Animations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyPlayer.Services;
using EmbyPlayer.Views;
using Microsoft.UI.Xaml;

namespace EmbyPlayer.ViewModels;

public partial class SearchViewModel : ObservableObject
{
    private readonly EmbyApiClient _api;
    private readonly SettingsService _settings;
    private readonly NavigationService _navigation;

    /// <summary>搜索代数，用于丢弃防抖期间过期的旧结果。</summary>
    private int _searchGeneration;

    [ObservableProperty]
    private ObservableCollection<ItemCard> _items = new();

    /// <summary>结果按媒体服务器分组，界面上一个服务器一段。</summary>
    [ObservableProperty]
    private ObservableCollection<SearchGroup> _groups = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>历史搜索词，最新的排最前。</summary>
    public ObservableCollection<string> SearchHistory { get; } = new();

    [ObservableProperty]
    private bool _hasSearchHistory;

    public Visibility HistoryVisibility =>
        HasSearchHistory ? Visibility.Visible : Visibility.Collapsed;

    partial void OnHasSearchHistoryChanged(bool value) => OnPropertyChanged(nameof(HistoryVisibility));

    /// <summary>点历史词后，由页面把它填回搜索框（ViewModel 不直接碰控件）。</summary>
    public event Action<string>? ApplySearchTerm;

    public SearchViewModel(EmbyApiClient api, SettingsService settings, NavigationService navigation)
    {
        _api = api;
        _settings = settings;
        _navigation = navigation;
        RefreshHistory();
    }

    /// <summary>重新读取历史（设置里改动后同步到界面）。</summary>
    public void RefreshHistory()
    {
        SearchHistory.Clear();
        foreach (var term in _settings.Current.SearchHistory)
        {
            if (!string.IsNullOrWhiteSpace(term))
            {
                SearchHistory.Add(term);
            }
        }
        HasSearchHistory = SearchHistory.Count > 0;
    }

    /// <summary>用户主动提交搜索：记进历史，再执行搜索。</summary>
    public async Task CommitSearchAsync(string term)
    {
        _settings.RememberSearchTerm(term);
        RefreshHistory();
        await SearchAsync(term);
    }

    [RelayCommand]
    private async Task UseHistoryItemAsync(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return;
        }
        ApplySearchTerm?.Invoke(term);
        await CommitSearchAsync(term);
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _settings.ClearSearchHistory();
        RefreshHistory();
    }

    /// <summary>只删历史里这一条。</summary>
    [RelayCommand]
    private void RemoveHistoryItem(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return;
        }
        _settings.RemoveSearchTerm(term);
        RefreshHistory();
    }

    public async Task SearchAsync(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            Interlocked.Increment(ref _searchGeneration);
            Items = new ObservableCollection<ItemCard>();
            Groups = new ObservableCollection<SearchGroup>();
            return;
        }

        var generation = Interlocked.Increment(ref _searchGeneration);
        ErrorMessage = null;
        IsLoading = true;
        try
        {
            // 聚合搜索：当前服务器排在最前，其余服务器并行查询
            var currentId = _settings.CurrentServer?.Id;
            var servers = _settings.Current.Servers
                .OrderByDescending(s => s.Id == currentId)
                .ToList();

            var tasks = servers.Select(s => SearchServerAsync(s, term));
            var results = await Task.WhenAll(tasks);

            // 防抖期间有更新的搜索请求，丢弃本次结果
            if (generation != Interlocked.CompareExchange(ref _searchGeneration, 0, 0))
            {
                return;
            }

            // 按服务器分组展示：一台服务器一段，没有任何命中的服务器不占位置
            var groups = new List<SearchGroup>();
            for (var i = 0; i < servers.Count; i++)
            {
                var (cards, error) = results[i];
                if (cards.Count == 0 && error is null)
                {
                    continue;
                }
                groups.Add(new SearchGroup(DisplayName(servers[i]), cards, error));
            }

            Groups = new ObservableCollection<SearchGroup>(groups);
            Items = new ObservableCollection<ItemCard>(results.SelectMany(r => r.Cards));

            // 所有服务器都失败时才提示错误
            if (groups.All(g => g.Items.Count == 0) && results.Any(r => r.Error is not null))
            {
                ErrorMessage = results.First(r => r.Error is not null).Error;
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<(List<ItemCard> Cards, string? Error)> SearchServerAsync(
        ServerEntry server, string term)
    {
        try
        {
            // 每台服务器使用独立的临时客户端，互不影响当前登录上下文
            using var client = new EmbyApiClient(_settings);
            client.Configure(server.Url, server.AccessToken, server.UserId);

            var result = await client.SearchAsync(term, limit: 30);
            var cards = new List<ItemCard>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            // 同一部片子可能同时挂在两个媒体库下（返回两张一模一样的结果），按「类型+名字」去重
            var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(EmbyPlayer.Models.BaseItemDto item)
            {
                if (!seenIds.Add(item.Id))
                {
                    return;
                }
                if (item.Type is "Movie" or "Series" && !seenTitles.Add($"{item.Type}|{item.Name}"))
                {
                    return;
                }
                cards.Add(ItemCardMapper.ToCard(client, item, server.Id));
            }

            foreach (var item in result.Items)
            {
                // 单集本身命中搜索词（比如直接搜「第 4 集」）才单独显示这一集
                if (item.Type == "Episode" && !EpisodeMatches(item, term))
                {
                    var seriesId = item.SeriesId;
                    if (string.IsNullOrEmpty(seriesId))
                    {
                        // 没有归属剧集可折（数据不全），只能保留这一集
                        Add(item);
                        continue;
                    }

                    // 这部剧已经出现过了（不管来自结果本身还是前面折出来的），就不用重复加
                    if (seenIds.Contains(seriesId!))
                    {
                        continue;
                    }

                    var series = await client.GetItemAsync(seriesId!);
                    if (series is null)
                    {
                        Add(item);
                        continue;
                    }

                    // 一季几十集只会变成一张剧集海报
                    Add(series);
                    continue;
                }

                Add(item);
            }

            return (cards, null);
        }
        catch (Exception ex)
        {
            // 单个服务器失败不影响其它服务器的搜索结果
            return ([], $"{(string.IsNullOrEmpty(server.ServerName) ? server.Url : server.ServerName)}：{ex.Message}");
        }
    }

    /// <summary>分组标题用的服务器名：没取到服务器名字时退回域名。</summary>
    private static string DisplayName(ServerEntry server)
    {
        if (!string.IsNullOrWhiteSpace(server.ServerName))
        {
            return server.ServerName!;
        }
        return Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) ? uri.Host : server.Url;
    }

    /// <summary>集名里的「S01E04」这类季集编号。</summary>
    private static readonly System.Text.RegularExpressions.Regex SeasonEpisodePattern =
        new(@"\bS\d{1,2}\s*E\d{1,3}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 这一集自己是否命中搜索词：命中才单独显示这一集，否则折成它所属的剧集。
    /// 不能直接拿整条集名去比——Emby 的集名通常写成「剧名 - S01E04 - 第 4 集」，
    /// 搜剧名时每一集都会命中；要把剧名和季集编号剥掉，剩下的才算这一集自己的信息。
    /// </summary>
    private static bool EpisodeMatches(EmbyPlayer.Models.BaseItemDto episode, string term)
    {
        if (string.IsNullOrWhiteSpace(episode.Name) || string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        var name = episode.Name!;
        var t = term.Trim();

        var own = name;
        if (!string.IsNullOrEmpty(episode.SeriesName))
        {
            own = own.Replace(episode.SeriesName!, "", StringComparison.OrdinalIgnoreCase);
        }
        own = SeasonEpisodePattern.Replace(own, " ").Trim(' ', '-', '–', '_', '·', '|', ':', '：');

        if (own.Length > 0 && own.Contains(t, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 用户就是要找「S01E04」这一集：季集编号本身对上也算命中
        var code = SeasonEpisodePattern.Match(name).Value;
        return code.Length > 0 && t.Contains(code, StringComparison.OrdinalIgnoreCase);
    }

    public void OpenItem(ItemCard card)
    {
        // 聚合搜索的结果可能来自其它服务器，先切换上下文再打开详情
        if (!string.IsNullOrEmpty(card.ServerId) && _settings.CurrentServer?.Id != card.ServerId)
        {
            var server = _settings.Current.Servers.FirstOrDefault(s => s.Id == card.ServerId);
            if (server is not null)
            {
                _settings.SwitchServer(server.Id);
                _api.Configure(server.Url, server.AccessToken, server.UserId);
            }
        }
        _navigation.NavigateTo<DetailPage>(card.Id, FluidNavigation.PosterPush);
    }
}

/// <summary>
/// 搜索结果里的一段：一台服务器的命中项。
/// 分组标题显示服务器名与命中数量；该服务器出错时顺带把原因写在标题下面。
/// </summary>
public sealed class SearchGroup
{
    public SearchGroup(string serverName, IEnumerable<ItemCard> items, string? error)
    {
        ServerName = serverName;
        Items = new ObservableCollection<ItemCard>(items);
        Error = error;
    }

    public string ServerName { get; }

    public ObservableCollection<ItemCard> Items { get; }

    public string? Error { get; }

    public string CountText => $"{Items.Count} 个结果";

    public Visibility ErrorVisibility =>
        string.IsNullOrEmpty(Error) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility GridVisibility =>
        Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
}
