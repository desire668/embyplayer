namespace EmbyPlayer.Models;

public class AuthenticationResult
{
    public string AccessToken { get; set; } = "";
    public UserDto? User { get; set; }
    public string? ServerId { get; set; }
}

public class UserDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class QueryResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalRecordCount { get; set; }
}

public class BaseItemDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public string Type { get; set; } = "";
    public string? CollectionType { get; set; }
    public string? Overview { get; set; }
    public int? ProductionYear { get; set; }
    public int? IndexNumber { get; set; }

    /// <summary>季号（Episode 的 ParentIndexNumber，即第几季）。</summary>
    public int? ParentIndexNumber { get; set; }
    public string? ParentId { get; set; }
    public string? SeriesId { get; set; }
    public string? SeriesName { get; set; }

    /// <summary>所属剧集主图的 image tag，用于在不额外请求的情况下拼出剧集海报 URL。</summary>
    public string? SeriesPrimaryImageTag { get; set; }
    public string? SeasonId { get; set; }
    public string? SeasonName { get; set; }
    public long? RunTimeTicks { get; set; }
    public Dictionary<string, string>? ImageTags { get; set; }
    public UserItemDataDto? UserData { get; set; }

    /// <summary>背景图 tags（首页海报墙大图轮播用，取第一张即可）。</summary>
    public List<string>? BackdropImageTags { get; set; }

    /// <summary>上级条目（剧集）背景图的 item id 与 tags：单集本身通常没有背景图。</summary>
    public string? ParentBackdropItemId { get; set; }
    public List<string>? ParentBackdropImageTags { get; set; }

    /// <summary>社区评分（如 7.4）。</summary>
    public double? CommunityRating { get; set; }

    /// <summary>类型标签（需要 Fields=Genres 才返回）。</summary>
    public List<string>? Genres { get; set; }

    public string IndexNumberText => IndexNumber is int n ? n.ToString() : "";

    public string RuntimeText
    {
        get
        {
            if (RunTimeTicks is not long ticks || ticks <= 0)
            {
                return "";
            }
            var ts = TimeSpan.FromTicks(ticks);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
                : $"{ts.Minutes}:{ts.Seconds:00}";
        }
    }

    public Microsoft.UI.Xaml.Visibility WatchedVisibility =>
        UserData?.Played == true
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
}

public class UserItemDataDto
{
    public long PlaybackPositionTicks { get; set; }
    public bool Played { get; set; }
    public bool IsFavorite { get; set; }

    /// <summary>最近一次播放时间（首页「继续播放 · X分钟前」副标题用）。</summary>
    public DateTime? LastPlayedDate { get; set; }
}

public class MediaSourceInfo
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Container { get; set; }
    public long? RunTimeTicks { get; set; }
    public List<MediaStreamInfo> MediaStreams { get; set; } = new();
}

public class MediaStreamInfo
{
    public int Index { get; set; }
    public string Type { get; set; } = "";
    public string? Codec { get; set; }
    public string? Language { get; set; }
    public string? Title { get; set; }
    public string? DisplayTitle { get; set; }
    public bool IsExternal { get; set; }
    public bool IsDefault { get; set; }
    public bool SupportsExternalStream { get; set; }
    public string? DeliveryUrl { get; set; }
}

public class PlaybackInfoResponse
{
    public List<MediaSourceInfo> MediaSources { get; set; } = new();
    public string? PlaySessionId { get; set; }
    public string? ErrorCode { get; set; }
}

public class PlaybackReport
{
    public string? ItemId { get; set; }
    public string? MediaSourceId { get; set; }
    public string? PlaySessionId { get; set; }
    public long? PositionTicks { get; set; }
    public bool IsPaused { get; set; }
    public bool IsMuted { get; set; }
    public bool CanSeek { get; set; } = true;
    public string? PlayMethod { get; set; }
}
