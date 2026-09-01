using EmbyPlayer.Models;

namespace EmbyPlayer.Services;

public static class ItemCardMapper
{
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
        return new ViewModels.ItemCard
        {
            Id = item.Id,
            Name = item.Name,
            Type = item.Type,
            ServerId = serverId,
            Year = item.ProductionYear?.ToString(),
            ImageUri = primaryTag is null ? null : new Uri(api.GetPrimaryImageUrl(item.Id, primaryTag)),
            Watched = item.UserData?.Played == true,
            ResumePercent = resumePercent
        };
    }
}
