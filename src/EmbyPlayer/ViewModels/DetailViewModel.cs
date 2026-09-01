using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyPlayer.Models;
using EmbyPlayer.Services;

namespace EmbyPlayer.ViewModels;

public record SubtitleOption(int StreamIndex, string Label, MediaStreamInfo? Stream);

public partial class DetailViewModel : ObservableObject
{
    private readonly EmbyApiClient _api;
    private readonly NavigationService _navigation;
    private readonly PlaybackService _playback;
    private readonly SynchronizationContext? _uiContext;

    private PlaybackPlan? _plan;

    [ObservableProperty]
    private BaseItemDto? _item;

    [ObservableProperty]
    private ObservableCollection<BaseItemDto> _seasons = new();

    [ObservableProperty]
    private BaseItemDto? _selectedSeason;

    [ObservableProperty]
    private ObservableCollection<BaseItemDto> _episodes = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isPreparingPlayback;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private List<SubtitleOption> _subtitleOptions = new();

    [ObservableProperty]
    private SubtitleOption? _selectedSubtitle;

    [ObservableProperty]
    private bool _isFavorite;

    public Uri? PosterUri { get; private set; }

    /// <summary>由页面订阅，弹出字幕选择对话框；用户确认后调用 ConfirmPlayAsync。</summary>
    public event Func<Task>? RequestSubtitleChoice;

    public DetailViewModel(EmbyApiClient api, NavigationService navigation, PlaybackService playback)
    {
        _api = api;
        _navigation = navigation;
        _playback = playback;
        _uiContext = SynchronizationContext.Current;

        _playback.StatusChanged += s => PostToUi(() => StatusText = s);
        _playback.PlaybackEnded += () => PostToUi(() => _ = ReloadAsync());
    }

    public bool IsSeries => Item?.Type is "Series" or "Season";
    public bool IsMovie => Item?.Type is "Movie" or "Video";

    public string MetaText
    {
        get
        {
            var item = Item;
            if (item is null)
            {
                return "";
            }
            var parts = new List<string>();
            if (item.ProductionYear is int year)
            {
                parts.Add(year.ToString());
            }
            if (item.RunTimeTicks is long ticks && ticks > 0)
            {
                parts.Add(FormatDuration(ticks));
            }
            return string.Join(" · ", parts);
        }
    }

    public string PlayButtonText
    {
        get
        {
            var ticks = Item?.UserData?.PlaybackPositionTicks ?? 0;
            return ticks > 0 ? $"继续播放（{FormatDuration(ticks)}）" : "播放";
        }
    }

    public Microsoft.UI.Xaml.Visibility EpisodesVisibility =>
        IsSeries ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string FavoriteButtonText => IsFavorite ? "♥ 已收藏" : "♡ 收藏";

    partial void OnIsFavoriteChanged(bool value) => OnPropertyChanged(nameof(FavoriteButtonText));

    private static string FormatDuration(long ticks)
    {
        var ts = TimeSpan.FromTicks(ticks);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分钟"
            : $"{ts.Minutes} 分钟";
    }

    public async Task LoadAsync(string itemId)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var item = await _api.GetItemAsync(itemId);
            Item = item;
            IsFavorite = item.UserData?.IsFavorite == true;
            var primaryTag = item.ImageTags is not null && item.ImageTags.TryGetValue("Primary", out var tag)
                ? tag : null;
            PosterUri = primaryTag is null ? null : new Uri(_api.GetPrimaryImageUrl(item.Id, primaryTag, 400));
            OnPropertyChanged(nameof(PosterUri));
            OnPropertyChanged(nameof(IsSeries));
            OnPropertyChanged(nameof(IsMovie));
            OnPropertyChanged(nameof(MetaText));
            OnPropertyChanged(nameof(PlayButtonText));
            OnPropertyChanged(nameof(EpisodesVisibility));

            if (item.Type == "Series")
            {
                var seasons = await _api.GetSeasonsAsync(item.Id);
                Seasons = new ObservableCollection<BaseItemDto>(seasons.Items);
                SelectedSeason = Seasons.FirstOrDefault();
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

    private async Task ReloadAsync()
    {
        var id = Item?.Id;
        if (id is not null)
        {
            await LoadAsync(id);
        }
    }

    partial void OnSelectedSeasonChanged(BaseItemDto? value)
    {
        if (value is not null && Item is not null)
        {
            _ = LoadEpisodesAsync();
        }
    }

    private async Task LoadEpisodesAsync()
    {
        var item = Item;
        var season = SelectedSeason;
        if (item is null || season is null)
        {
            return;
        }
        try
        {
            var episodes = await _api.GetEpisodesAsync(item.Id, season.Id);
            Episodes = new ObservableCollection<BaseItemDto>(episodes.Items);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private Task PlayAsync()
    {
        if (Item is null)
        {
            return Task.CompletedTask;
        }
        var target = IsSeries ? Episodes.FirstOrDefault() ?? Item : Item;
        return PreparePlaybackAsync(target);
    }

    [RelayCommand]
    private Task PlayEpisodeAsync(BaseItemDto? episode) =>
        episode is null ? Task.CompletedTask : PreparePlaybackAsync(episode);

    public void GoBack() => _navigation.GoBack();

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        var item = Item;
        if (item is null)
        {
            return;
        }
        try
        {
            var newValue = !IsFavorite;
            await _api.SetFavoriteAsync(item.Id, newValue);
            IsFavorite = newValue;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task PreparePlaybackAsync(BaseItemDto target)
    {
        ErrorMessage = null;
        IsPreparingPlayback = true;
        try
        {
            _plan = await _playback.PrepareAsync(target);
            var options = new List<SubtitleOption> { new(-1, "无字幕", null) };
            options.AddRange(_plan.SubtitleStreams.Select(s =>
                new SubtitleOption(s.Index,
                    string.IsNullOrEmpty(s.DisplayTitle)
                        ? (string.IsNullOrEmpty(s.Language) ? $"字幕 #{s.Index}" : s.Language)
                        : s.DisplayTitle,
                    s)));
            SubtitleOptions = options;
            SelectedSubtitle = options.FirstOrDefault(o => o.Stream?.IsDefault == true) ?? options[0];

            IsPreparingPlayback = false;
            if (RequestSubtitleChoice is not null)
            {
                await RequestSubtitleChoice.Invoke();
            }
            else
            {
                await ConfirmPlayAsync();
            }
        }
        catch (Exception ex)
        {
            IsPreparingPlayback = false;
            _plan = null;
            ErrorMessage = ex.Message;
        }
    }

    public async Task ConfirmPlayAsync()
    {
        var plan = _plan;
        _plan = null;
        if (plan is null)
        {
            return;
        }
        try
        {
            await _playback.PlayAsync(plan, SelectedSubtitle?.Stream);
            StatusText = $"正在播放：{plan.Item.Name}";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void PostToUi(Action action)
    {
        if (_uiContext is not null)
        {
            _uiContext.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }
}
