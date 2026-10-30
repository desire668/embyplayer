using System.Text.Json.Serialization;

namespace EmbyPlayer.Models;

/// <summary>TMDB 分页响应外壳（/trending 等接口）。</summary>
public class TmdbPagedResponse<T>
{
    public List<T> Results { get; set; } = new();
}

/// <summary>
/// TMDB 电影条目（列表接口返回的字段）。
/// 注意：TMDB 返回的是 snake_case（poster_path / vote_average），
/// 而 System.Text.Json 的 PropertyNameCaseInsensitive 只忽略大小写、不做下划线映射，
/// 所以必须逐个标注 JsonPropertyName，否则这些字段会被反序列化成 null。
/// </summary>
public class TmdbMovie
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; set; }

    /// <summary>评分人数（详情页评分胶囊的悬停提示）。</summary>
    [JsonPropertyName("vote_count")]
    public int? VoteCount { get; set; }

    /// <summary>上映日期 "2026-01-30"，取前 4 位即年份。</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }
}

/// <summary>TMDB 电影详情：补 runtime 与类型中文名。</summary>
public class TmdbMovieDetail : TmdbMovie
{
    /// <summary>片长（分钟）。</summary>
    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    [JsonPropertyName("genres")]
    public List<TmdbGenre> Genres { get; set; } = new();
}

public class TmdbGenre
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

/// <summary>/find/{external_id} 的响应：按 IMDb id 反查 TMDB 条目。</summary>
public class TmdbFindResponse
{
    [JsonPropertyName("movie_results")]
    public List<TmdbMovie> MovieResults { get; set; } = new();

    [JsonPropertyName("tv_results")]
    public List<TmdbMovie> TvResults { get; set; } = new();
}

/// <summary>TMDB 评分查询结果（id 同时可作为后续请求的依据）。</summary>
public sealed record TmdbRating(int Id, double Vote, int Votes);

/// <summary>/{kind}/{id}/images：拿条目在别处的 id（目前只用 IMDb id）。</summary>
public sealed class TmdbExternalIds
{
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }
}

/// <summary>images 接口返回的一张图（海报 / 背景 / 剧名 logo 结构相同）。</summary>
public sealed class TmdbImage
{
    /// <summary>形如 "/xxxx.png" 或 "/xxxx.svg"；svg 不能直接解码成位图，需单独处理。</summary>
    [JsonPropertyName("file_path")]
    public string? FilePath { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("aspect_ratio")]
    public double? AspectRatio { get; set; }

    /// <summary>语言标记："zh" / "en" / null（无字，纯图形）。</summary>
    [JsonPropertyName("iso_639_1")]
    public string? Language { get; set; }

    /// <summary>这张图的社区评分，挑 logo 时按它排优选。</summary>
    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; set; }

    [JsonPropertyName("vote_count")]
    public int? VoteCount { get; set; }
}

/// <summary>/{kind}/{id}/images 的响应；目前只用到 logos（剧名 logo）。</summary>
public sealed class TmdbImages
{
    [JsonPropertyName("logos")]
    public List<TmdbImage> Logos { get; set; } = new();
}
