using EmbyPlayer.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace EmbyPlayer.ViewModels;

/// <summary>详情页的一个评分胶囊（TMDB / IMDb / Trakt）。</summary>
public sealed record RatingChip(string Source, string Value, string Tooltip);

/// <summary>详情页的一个分类胶囊，点击跳到分类页。</summary>
public sealed record GenreChip(string Name);

/// <summary>「播放源」下拉里的一项：一台已添加的 Emby 服务器。</summary>
public sealed class PlaybackSourceOption
{
    public required string ServerId { get; init; }
    public required string Name { get; init; }
}

/// <summary>详情页的一条媒体信息（分辨率 / 码率 / 音轨 / 字幕…）。</summary>
public sealed record MediaChip(string Label, string Value);

/// <summary>剧集详情页里的一集：缩略图 + 集号 + 名称 + 时长 + 简介。</summary>
public sealed class EpisodeRow
{
    public required BaseItemDto Item { get; init; }

    /// <summary>「第 3 集」；没有集号时为空。</summary>
    public string NumberText => Item.IndexNumber is int n ? $"第 {n} 集" : "";

    public string Name => Item.Name;

    public string RuntimeText => Item.RuntimeText;

    public string Overview => Item.Overview ?? "";

    public ImageSource? Thumbnail { get; init; }

    public bool HasThumbnail => Thumbnail is not null;

    public Visibility ThumbnailVisibility =>
        HasThumbnail ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>没有截图时用集号占位，避免出现一块空白。</summary>
    public Visibility PlaceholderVisibility =>
        HasThumbnail ? Visibility.Collapsed : Visibility.Visible;

    public Visibility WatchedVisibility =>
        Item.UserData?.Played == true ? Visibility.Visible : Visibility.Collapsed;

    public Visibility OverviewVisibility =>
        string.IsNullOrWhiteSpace(Item.Overview) ? Visibility.Collapsed : Visibility.Visible;
}
