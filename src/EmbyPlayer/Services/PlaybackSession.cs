using EmbyPlayer.Models;

namespace EmbyPlayer.Services;

/// <summary>一个正在播放的会话，负责向 Emby 上报 Playing / Progress / Stopped。</summary>
public sealed class PlaybackSession
{
    private const double ReportIntervalSeconds = 10;

    private readonly EmbyApiClient _api;
    private readonly string _userId;

    private DateTime _lastReportAtUtc = DateTime.MinValue;
    private double _lastReportedPosition = -1;

    public BaseItemDto Item { get; }
    public MediaSourceInfo Source { get; }
    public string? PlaySessionId { get; }

    public PlaybackSession(EmbyApiClient api, string userId, BaseItemDto item,
        MediaSourceInfo source, string? playSessionId)
    {
        _api = api;
        _userId = userId;
        Item = item;
        Source = source;
        PlaySessionId = playSessionId;
    }

    public PlaybackReport BuildReport(double positionSeconds, bool isPaused = false) => new()
    {
        ItemId = Item.Id,
        MediaSourceId = Source.Id,
        PlaySessionId = PlaySessionId,
        PositionTicks = (long)(positionSeconds * 10_000_000),
        IsPaused = isPaused,
        PlayMethod = "DirectStream"
    };

    public async Task ReportPlayingAsync(double positionSeconds)
    {
        await _api.ReportPlayingAsync(BuildReport(positionSeconds));
        _lastReportAtUtc = DateTime.UtcNow;
        _lastReportedPosition = positionSeconds;
    }

    public async Task UpdatePositionAsync(double positionSeconds)
    {
        var elapsed = (DateTime.UtcNow - _lastReportAtUtc).TotalSeconds;
        if (elapsed < ReportIntervalSeconds || Math.Abs(positionSeconds - _lastReportedPosition) < 5)
        {
            return;
        }
        await _api.ReportProgressAsync(BuildReport(positionSeconds));
        _lastReportAtUtc = DateTime.UtcNow;
        _lastReportedPosition = positionSeconds;
    }

    public async Task StopAsync(double positionSeconds, bool completed, double durationSeconds)
    {
        await _api.ReportStoppedAsync(BuildReport(positionSeconds));
        var watched = completed ||
            (durationSeconds > 0 && positionSeconds / durationSeconds >= 0.9);
        if (watched)
        {
            await _api.MarkPlayedAsync(Item.Id);
        }
    }
}

public sealed class PlaybackSessionManager
{
    private readonly EmbyApiClient _api;
    private readonly SettingsService _settings;

    public PlaybackSession? Current { get; private set; }

    public PlaybackSessionManager(EmbyApiClient api, SettingsService settings)
    {
        _api = api;
        _settings = settings;
    }

    public async Task<PlaybackSession> StartAsync(BaseItemDto item, MediaSourceInfo source,
        string? playSessionId, double resumeSeconds)
    {
        var session = new PlaybackSession(_api, _settings.Current.UserId ?? "", item, source, playSessionId);
        await session.ReportPlayingAsync(resumeSeconds);
        Current = session;
        return session;
    }

    public async Task EndAsync(double positionSeconds, bool completed)
    {
        var session = Current;
        Current = null;
        if (session is null)
        {
            return;
        }
        await session.StopAsync(positionSeconds, completed, session.Item.RunTimeTicks is long t ? t / 10_000_000.0 : 0);
    }
}
