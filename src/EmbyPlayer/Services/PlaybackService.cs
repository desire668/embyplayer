using EmbyPlayer.Models;

namespace EmbyPlayer.Services;

public sealed class PlaybackPlan
{
    public required BaseItemDto Item { get; init; }
    public required PlaybackInfoResponse Info { get; init; }
    public required MediaSourceInfo Source { get; init; }
    public required string StreamUrl { get; init; }
    public List<MediaStreamInfo> SubtitleStreams { get; init; } = new();

    /// <summary>
    /// 取这条播放计划用的 API 客户端。为空表示用当前服务器；
    /// 用户在详情页选了别的「播放源」时，这里是被选中服务器的独立客户端，
    /// 流地址与播放进度上报都会走那台服务器。
    /// </summary>
    public EmbyApiClient? Client { get; init; }
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

    /// <summary>最近一次播放用的客户端（播放源不是当前服务器时非空）。</summary>
    private EmbyApiClient? _lastClient;

    /// <summary>当前会话实际使用的客户端：跟随「播放源」，缺省回落到当前服务器。</summary>
    private EmbyApiClient SessionClient => _lastClient ?? _api;

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

    /// <summary>
    /// 取播放计划。<paramref name="client"/> 传其它服务器的客户端时，
    /// 整条链路（流地址、字幕、进度上报）都走那台服务器。
    /// </summary>
    public async Task<PlaybackPlan> PrepareAsync(BaseItemDto item, EmbyApiClient? client = null)
    {
        var api = client ?? _api;
        var info = await api.GetPlaybackInfoAsync(item.Id);
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
            StreamUrl = api.BuildStreamUrl(item.Id, source.Id, info.PlaySessionId),
            SubtitleStreams = source.MediaStreams.Where(s => s.Type == "Subtitle").ToList(),
            Client = client
        };
    }

    public async Task PlayAsync(PlaybackPlan plan, MediaStreamInfo? subtitle, string? danmakuFile = null)
    {
        var mpvPath = MpvLocator.Find(_settings)
            ?? throw new FileNotFoundException("未找到 mpv.exe，请在设置页配置路径。");

        WireHandlers();

        var resumeSeconds = GetResumeSeconds(plan.Item);
        var subtitlePath = await PrepareSubtitleFileAsync(plan, subtitle);

        // 播放源可能不是当前服务器：流地址、字幕与进度上报统一用它自己的客户端
        _lastClient = plan.Client;
        await _sessions.StartAsync(plan.Item, plan.Source, plan.Info.PlaySessionId, resumeSeconds, plan.Client);

        if (_controller.IsRunning)
        {
            await _controller.LoadFileAsync(plan.StreamUrl);
            await _controller.WaitForFileStartedAsync(TimeSpan.FromSeconds(20));
            if (subtitlePath is not null)
            {
                await _controller.AddSubtitleAsync(subtitlePath);
            }
            if (danmakuFile is not null)
            {
                await _controller.AddSubtitleAsync(danmakuFile);
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
                SubtitleFile = subtitlePath,
                DanmakuFile = danmakuFile
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
        var dir = CacheService.DirectoryOf(CacheKind.Playback);
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
            var next = await SessionClient.GetNextUpAsync(item.SeriesId!);
            if (next is not null && next.Id != item.Id)
            {
                StatusChanged?.Invoke($"自动播放下一集：{next.Name}");
                var plan = await PrepareAsync(next, _lastClient);
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
