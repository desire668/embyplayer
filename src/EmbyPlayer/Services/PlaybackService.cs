using EmbyPlayer.Models;

namespace EmbyPlayer.Services;

public sealed class PlaybackPlan
{
    public required BaseItemDto Item { get; init; }
    public required PlaybackInfoResponse Info { get; init; }
    public required MediaSourceInfo Source { get; init; }
    public required string StreamUrl { get; init; }
    public List<MediaStreamInfo> SubtitleStreams { get; init; } = new();
}

/// <summary>
/// 播放流水线编排：PlaybackInfo → 直连流地址 → 字幕准备 → 启动/复用 mpv → 会话上报，
/// 以及自然播完后的自动连播。
/// </summary>
public sealed class PlaybackService
{
    private readonly EmbyApiClient _api;
    private readonly SettingsService _settings;
    private readonly MpvController _controller;
    private readonly PlaybackSessionManager _sessions;
    private bool _handlersWired;

    public MpvController Controller => _controller;

    public event Action<string>? StatusChanged;
    public event Action? PlaybackEnded;

    public PlaybackService(EmbyApiClient api, SettingsService settings,
        MpvController controller, PlaybackSessionManager sessions)
    {
        _api = api;
        _settings = settings;
        _controller = controller;
        _sessions = sessions;
    }

    public async Task<PlaybackPlan> PrepareAsync(BaseItemDto item)
    {
        var info = await _api.GetPlaybackInfoAsync(item.Id);
        if (!string.IsNullOrEmpty(info.ErrorCode))
        {
            throw new InvalidOperationException($"无法播放：{info.ErrorCode}");
        }
        var source = info.MediaSources.FirstOrDefault()
            ?? throw new InvalidOperationException("服务器未返回可播放的媒体源。");
        return new PlaybackPlan
        {
            Item = item,
            Info = info,
            Source = source,
            StreamUrl = _api.BuildStreamUrl(item.Id, source.Id, info.PlaySessionId),
            SubtitleStreams = source.MediaStreams.Where(s => s.Type == "Subtitle").ToList()
        };
    }

    public async Task PlayAsync(PlaybackPlan plan, MediaStreamInfo? subtitle)
    {
        var mpvPath = MpvLocator.Find(_settings)
            ?? throw new FileNotFoundException("未找到 mpv.exe，请在设置页配置路径。");

        WireHandlers();

        var resumeSeconds = GetResumeSeconds(plan.Item);
        var subtitlePath = await PrepareSubtitleFileAsync(plan, subtitle);

        await _sessions.StartAsync(plan.Item, plan.Source, plan.Info.PlaySessionId, resumeSeconds);

        if (_controller.IsRunning)
        {
            await _controller.LoadFileAsync(plan.StreamUrl);
            if (subtitlePath is not null)
            {
                await _controller.AddSubtitleAsync(subtitlePath);
            }
        }
        else
        {
            await _controller.StartAsync(new MpvLaunchOptions
            {
                MpvPath = mpvPath,
                Url = plan.StreamUrl,
                Title = plan.Item.Name,
                ResumeSeconds = resumeSeconds,
                SubtitleFile = subtitlePath
            });
        }
        StatusChanged?.Invoke($"正在播放：{plan.Item.Name}");
    }

    private static double GetResumeSeconds(BaseItemDto item)
    {
        if (item.UserData?.PlaybackPositionTicks is not long ticks || ticks <= 0)
        {
            return 0;
        }
        // 距结尾不足 30 秒视为看完，从头播放
        if (item.RunTimeTicks is long rt && rt > 0 && rt - ticks < 30L * 10_000_000L)
        {
            return 0;
        }
        return ticks / 10_000_000.0;
    }

    private async Task<string?> PrepareSubtitleFileAsync(PlaybackPlan plan, MediaStreamInfo? stream)
    {
        if (stream is null)
        {
            return null;
        }
        var url = _api.BuildSubtitleUrl(plan.Item.Id, plan.Source.Id, stream);
        var dir = Path.Combine(Path.GetTempPath(), "EmbyPlayer");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{plan.Item.Id}_{stream.Index}.srt");
        await _api.DownloadSubtitleAsync(url, path);
        return path;
    }

    private void WireHandlers()
    {
        if (_handlersWired)
        {
            return;
        }
        _controller.FileEnded += OnFileEnded;
        _controller.PlayerClosed += OnPlayerClosed;
        _handlersWired = true;
    }

    private async void OnFileEnded()
    {
        try
        {
            await HandleFileEndedAsync();
        }
        catch
        {
            // 上报失败不应影响播放体验
        }
    }

    private async void OnPlayerClosed()
    {
        try
        {
            await HandlePlayerClosedAsync();
        }
        catch
        {
        }
    }

    private async Task HandleFileEndedAsync()
    {
        var session = _sessions.Current;
        if (session is null)
        {
            return;
        }
        var duration = _controller.LastKnownDuration;
        var position = duration > 0 ? duration : _controller.LastKnownPosition;
        var item = session.Item;
        await _sessions.EndAsync(position, completed: true);
        PlaybackEnded?.Invoke();

        if (_settings.Current.AutoPlayNext && item.Type == "Episode" && !string.IsNullOrEmpty(item.SeriesId))
        {
            var next = await _api.GetNextUpAsync(item.SeriesId!);
            if (next is not null && next.Id != item.Id)
            {
                StatusChanged?.Invoke($"自动播放下一集：{next.Name}");
                var plan = await PrepareAsync(next);
                await PlayAsync(plan, subtitle: null);
                return;
            }
        }
        await _controller.QuitAsync();
    }

    private async Task HandlePlayerClosedAsync()
    {
        var session = _sessions.Current;
        if (session is null)
        {
            return;
        }
        await _sessions.EndAsync(_controller.LastKnownPosition, completed: false);
        PlaybackEnded?.Invoke();
        StatusChanged?.Invoke("播放已结束");
    }
}
