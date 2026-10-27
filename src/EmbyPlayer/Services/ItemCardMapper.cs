using System.Collections.Generic;
using EmbyPlayer.Models;

namespace EmbyPlayer.Services;

/// <summary>最近播放列表里，某部剧集上次看到的是哪一集。</summary>
/// <param name="EpisodeId">续播的集 ID。</param>
/// <param name="Label">展示用文本，如 "S01E05"。</param>
public sealed record ResumeEpisode(string EpisodeId, string Label);

public static class ItemCardMapper
{
    /// <summary>
    /// itemId → 海报 URL。列表加载时顺手记下，详情页就能在发起 GetItemAsync 之前
    /// 先把海报显示出来（用的是缓存里那张已经解码好的位图），不必等元数据回来。
    /// </summary>
    private static readonly Dictionary<string, string> PosterUrlByItemId = new();

    /// <summary>
    /// 剧集 ID → 最近播放的那一集。仅由「最近播放」列表写入，
    /// 供剧集详情页把「播放」变成「继续播放 S01E05」。
    /// </summary>
    private static readonly Dictionary<string, ResumeEpisode> ResumeEpisodeBySeriesId = new();

    public static string? TryGetPosterUrl(string itemId) =>
        PosterUrlByItemId.TryGetValue(itemId, out var url) ? url : null;

    public static ResumeEpisode? TryGetResumeEpisode(string seriesId) =>
        ResumeEpisodeBySeriesId.TryGetValue(seriesId, out var ep) ? ep : null;

    public static ViewModels.ItemCard ToCard(EmbyApiClient api, BaseItemDto item, string? serverId = null)
    {
        var primaryTag = item.ImageTags is not null && item.ImageTags.TryGetValue("Primary", out var tag)
            ? tag : null;

        string? posterUrl = null;
        if (primaryTag is not null)
        {
            posterUrl = api.GetPrimaryImageUrl(item.Id, primaryTag);
            PosterUrlByItemId[item.Id] = posterUrl;
        }

        return new ViewModels.ItemCard
        {
            Id = item.Id,
            Name = item.Name,
            Type = item.Type,
            ServerId = serverId,
            Year = item.ProductionYear?.ToString(),
            ImageUri = posterUrl is null ? null : new System.Uri(posterUrl),
            Watched = item.UserData?.Played == true,
            ResumePercent = ComputeResumePercent(item)
        };
    }

    /// <summary>
    /// 把「最近播放」里的某一集折叠成所属剧集的卡片：海报、标题、点击目标全部换成剧集，
    /// 副标题保留续播的集号。这样一部剧追到一半时，列表里只出现剧集本身而不是散落的单集。
    /// </summary>
    /// <returns>没有 SeriesId（拿不到剧集归属）时返回 null，调用方应退回单集卡片。</returns>
    public static ViewModels.ItemCard? ToSeriesCard(EmbyApiClient api, BaseItemDto episode, string? serverId = null)
    {
        var seriesId = episode.SeriesId;
        if (string.IsNullOrEmpty(seriesId))
        {
            return null;
        }

        var label = BuildEpisodeLabel(episode);
        ResumeEpisodeBySeriesId[seriesId!] = new ResumeEpisode(episode.Id, label);

        // 剧集海报：优先用 episode 上带的剧集主图 tag；没有 tag 时也能拿剧集当前主图
        var posterUrl = api.GetPrimaryImageUrl(seriesId!, episode.SeriesPrimaryImageTag);
        PosterUrlByItemId[seriesId!] = posterUrl;

        return new ViewModels.ItemCard
        {
            Id = seriesId!,
            Name = string.IsNullOrWhiteSpace(episode.SeriesName) ? episode.Name : episode.SeriesName!,
            Type = "Series",
            ServerId = serverId,
            Year = episode.ProductionYear?.ToString(),
            ImageUri = new System.Uri(posterUrl),
            // 剧集整体没有「已看完」概念，标记只看单集状态会误导
            Watched = false,
            ResumePercent = ComputeResumePercent(episode),
            Subtitle = label
        };
    }

    private static double ComputeResumePercent(BaseItemDto item)
    {
        if (item.UserData?.PlaybackPositionTicks is long pos && pos > 0 &&
            item.RunTimeTicks is long rt && rt > 0)
        {
            return Math.Min(100, pos * 100.0 / rt);
        }
        return 0;
    }

    /// <summary>集号文本：有季号用 S01E05，否则「第 5 集」，都没有则返回空。</summary>
    public static string BuildEpisodeLabel(BaseItemDto episode)
    {
        var episodeText = episode.IndexNumber is int n ? n.ToString("00") : "";
        if (episode.ParentIndexNumber is int season)
        {
            return $"S{season:00}E{episodeText}";
        }
        return episode.IndexNumber is int index ? $"第 {index} 集" : "";
    }
}
