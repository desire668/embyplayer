using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbyPlayer.Services;
using EmbyPlayer.Views;

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

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public SearchViewModel(EmbyApiClient api, SettingsService settings, NavigationService navigation)
    {
        _api = api;
        _settings = settings;
        _navigation = navigation;
    }

    public async Task SearchAsync(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            Interlocked.Increment(ref _searchGeneration);
            Items = new ObservableCollection<ItemCard>();
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

            var cards = results.SelectMany(r => r.Cards).ToList();
            Items = new ObservableCollection<ItemCard>(cards);

            // 所有服务器都失败时才提示错误
            if (cards.Count == 0 && results.Any(r => r.Error is not null))
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
            var cards = result.Items
                .Select(i => ItemCardMapper.ToCard(client, i, server.Id))
                .ToList();
            return (cards, null);
        }
        catch (Exception ex)
        {
            // 单个服务器失败不影响其它服务器的搜索结果
            return ([], $"{(string.IsNullOrEmpty(server.ServerName) ? server.Url : server.ServerName)}：{ex.Message}");
        }
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
        _navigation.NavigateTo<DetailPage>(card.Id);
    }
}
