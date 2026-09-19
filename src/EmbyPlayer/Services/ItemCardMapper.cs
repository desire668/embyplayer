using System.Collections.Generic;
using EmbyPlayer.Models;

namespace EmbyPlayer.Services;

public static class ItemCardMapper
{
    /// <summary>
    /// itemId → 海报 URL。列表加载时顺手记下，详情页就能在发起 GetItemAsync 之前
    /// 先把海报显示出来（用的是缓存里那张已经解码好的位图），不必等元数据回来。
    /// </summary>
    private static readonly Dictionary<string, string> PosterUrlByItemId = new();

    public static string? TryGetPosterUrl(string itemId) =>
        PosterUrlByItemId.TryGetValue(itemId, out var url) ? url : null;

    public static ViewModels.ItemCard ToCard(EmbyApiClient api, BaseItemDto item, string? serverId = null)
    {
        double resumePercent = 0;
        if (item.UserData?.PlaybackPositionTicks is long pos && pos > 0 &&
            item.RunTimeTicks is long rt && rt > 0)
        {
            resumePercent = Math.Min(100, pos * 100.0 / rt);
        }
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
            ResumePercent = resumePercent
        };
    }
}
