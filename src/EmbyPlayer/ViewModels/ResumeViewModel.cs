using System.Collections.ObjectModel;
using EmbyPlayer.Animations;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbyPlayer.Models;
using EmbyPlayer.Services;
using EmbyPlayer.Views;

namespace EmbyPlayer.ViewModels;

public partial class ResumeViewModel : ObservableObject
{
    private readonly EmbyApiClient _api;
    private readonly NavigationService _navigation;

    [ObservableProperty]
    private ObservableCollection<ItemCard> _items = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public ResumeViewModel(EmbyApiClient api, NavigationService navigation)
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
            var result = await _api.GetResumeAsync();
            Items = new ObservableCollection<ItemCard>(BuildCards(result.Items ?? new List<BaseItemDto>()));
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

    /// <summary>
    /// 剧集的某一集折叠成剧集卡片：一部剧追到一半时，列表里只显示剧集本身，
    /// 副标题标出续播的集号；同一部剧的多集会合并成一张卡片（保留最近播放的那一集）。
    /// 电影等仍是单条目，保持原样。
    /// </summary>
    private List<ItemCard> BuildCards(List<BaseItemDto> items)
    {
        var cards = new List<ItemCard>(items.Count);
        var seenSeries = new HashSet<string>();

        foreach (var item in items)
        {
            var seriesId = item.Type == "Episode" ? item.SeriesId : null;
            if (string.IsNullOrEmpty(seriesId))
            {
                cards.Add(ItemCardMapper.ToCard(_api, item));
                continue;
            }

            if (!seenSeries.Add(seriesId!))
            {
                continue; // 同一部剧的其它集已合并，跳过
            }

            // 拿不到剧集归属时才退回单集卡片
            cards.Add(ItemCardMapper.ToSeriesCard(_api, item) ?? ItemCardMapper.ToCard(_api, item));
        }

        return cards;
    }

    public void OpenItem(ItemCard card) => _navigation.NavigateTo<DetailPage>(card.Id, FluidNavigation.PosterPush);
}
