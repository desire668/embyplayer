using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbyPlayer.Models;

namespace EmbyPlayer.Services;

/// <summary>详情页的第三方评分集合；拿不到的项是 null，界面直接不显示对应胶囊。</summary>
public sealed class RatingsInfo
{
    public double? Tmdb { get; init; }
    public int? TmdbVotes { get; init; }

    public double? Imdb { get; init; }
    public int? ImdbVotes { get; init; }

    /// <summary>历史字段：旧版本显示过 Trakt 评分，现已不再请求，恒为 null。</summary>
    public double? Trakt { get; init; }
    public int? TrakVotes { get; init; }

    public static readonly RatingsInfo Empty = new();

    public bool IsEmpty => Tmdb is null && Imdb is null && Trakt is null;
}

/// <summary>
/// 聚合 TMDB / IMDb 两家的评分，供详情页展示。
///
/// 三个源的门槛不同，所以都做成「能拿到就显示、拿不到就跳过」：
/// - TMDB：复用设置里已有的 API Key，无需额外配置；
/// - IMDb：走 OMDb（omdbapi.com）的免费 Key，需在设置页填写；
///
/// 所有请求都短超时 + 静默失败：评分属于锦上添花，绝不能因此让详情页转圈或报错。
/// </summary>
public sealed class RatingsService
{
    private readonly SettingsService _settings;
    private readonly TmdbClient _tmdb;
    private readonly HttpClient _http = CreateHttp();

    /// <summary>带上 User-Agent：空 UA 的请求可能被公共接口前面的 CDN 直接 403。</summary>
    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EmbyPlayer/1.0");
        return client;
    }
    private readonly Dictionary<string, RatingsInfo> _cache = new();
    private readonly object _gate = new();

    public RatingsService(SettingsService settings, TmdbClient tmdb)
    {
        _settings = settings;
        _tmdb = tmdb;
    }

    /// <summary>取某个条目的评分（同一 item 只查一次；超时或出错当没有，绝不拖住详情页）。</summary>
    public async Task<RatingsInfo> GetAsync(BaseItemDto item, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(item.Id, out var cached))
            {
                return cached;
            }
        }

        // 外部请求整体限时：TMDB 的客户端自带 3 次重试，网络不通时能拖上半分钟，
        // 评分不值得让用户等这么久——超时就当没有，也不写缓存（下次进详情页还能再试）。
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));

        RatingsInfo result;
        var complete = true;
        try
        {
            result = await FetchAsync(item, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            result = RatingsInfo.Empty;
            complete = false;
        }
        catch
        {
            result = RatingsInfo.Empty;
            complete = false;
        }

        if (complete)
        {
            lock (_gate)
            {
                _cache[item.Id] = result;
            }
        }
        return result;
    }

    /// <summary>清空缓存（切换服务器 / 修改 Key 后调用）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }

    private async Task<RatingsInfo> FetchAsync(BaseItemDto item, CancellationToken ct)
    {
        var ids = item.ProviderIds ?? new Dictionary<string, string>();
        ids.TryGetValue("Tmdb", out var tmdbId);
        ids.TryGetValue("Imdb", out var imdbId);

        // 剧集、季、集都按 tv 处理（TMDB 剧集评分与电影评分字段结构一致）
        var isTv = item.Type is "Series" or "Season" or "Episode";

        TmdbRating? tmdbRating;

        if (string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(imdbId))
        {
            // 连 provider id 都没刮到：按片名 + 年份搜一次 TMDB，用它补评分与 IMDb id
            var found = await SearchByTitleAsync(item.Name, item.ProductionYear, isTv, ct);
            if (found is null)
            {
                return RatingsInfo.Empty;
            }
            tmdbRating = found.VoteAverage is double vote and > 0
                ? new TmdbRating(found.Id, vote, found.VoteCount ?? 0)
                : null;
            imdbId = await TryGetImdbIdAsync(found.Id.ToString(), isTv, ct);
        }
        else
        {
            // TMDB 评分与「补 IMDb id」互不依赖，并发跑
            var tmdbTask = FetchTmdbAsync(tmdbId, imdbId, isTv, ct);
            var imdbIdTask = string.IsNullOrEmpty(imdbId)
                ? TryGetImdbIdAsync(tmdbId!, isTv, ct)
                : Task.FromResult<string?>(imdbId);

            await Task.WhenAll(tmdbTask, imdbIdTask);
            tmdbRating = tmdbTask.Result;
            imdbId = imdbIdTask.Result;

            // 只有 IMDb id 时 TMDB 是靠反查拿到的，用反查到的 TMDB id 再补一次
            if (string.IsNullOrEmpty(imdbId) && tmdbRating is not null)
            {
                imdbId = await TryGetImdbIdAsync(tmdbRating.Id.ToString(), isTv, ct);
            }
        }

        var imdb = await FetchImdbAsync(imdbId, isTv, ct);

        return new RatingsInfo
        {
            Tmdb = tmdbRating?.Vote,
            TmdbVotes = tmdbRating?.Votes,
            Imdb = imdb?.Rating,
            ImdbVotes = imdb?.Votes
        };
    }

    /// <summary>用 TMDB 的 external_ids 把 TMDB id 换成 IMDb id；拿不到就返回 null。</summary>
    private async Task<string?> TryGetImdbIdAsync(string tmdbId, bool isTv, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tmdbId) || !_tmdb.HasApiKey)
        {
            return null;
        }
        try
        {
            return await _tmdb.GetImdbIdAsync(isTv ? "tv" : "movie", tmdbId, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>条目没有 provider id 时按片名 + 年份搜 TMDB。</summary>
    private async Task<TmdbMovie?> SearchByTitleAsync(string? name, int? year, bool isTv, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || !_tmdb.HasApiKey)
        {
            return null;
        }
        try
        {
            return await _tmdb.SearchAsync(name, year, isTv, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>TMDB 评分：有 TMDB id 直接查，只有 IMDb id 时先反查。</summary>
    private async Task<TmdbRating?> FetchTmdbAsync(string? tmdbId, string? imdbId, bool isTv, CancellationToken ct)
    {
        if (!_tmdb.HasApiKey)
        {
            return null;
        }
        try
        {
            if (!string.IsNullOrEmpty(tmdbId))
            {
                return await _tmdb.GetRatingAsync(isTv ? "tv" : "movie", tmdbId, ct);
            }
            if (!string.IsNullOrEmpty(imdbId))
            {
                return await _tmdb.FindByImdbAsync(imdbId, isTv, ct);
            }
        }
        catch
        {
            // 网络不通 / Key 失效：当作没有 TMDB 评分
        }
        return null;
    }

    /// <summary>
    /// IMDb 评分：优先 OMDb（能拿到票数，但要在设置里填免费 Key）；
    /// 没填 Key 或它失败时走 Cinemeta（Stremio 的公共元数据服务，免密钥，只有分数没有票数）。
    /// 这样即使什么都不配置，详情页也能看到 IMDb 评分。
    /// </summary>
    private async Task<(double Rating, int? Votes)?> FetchImdbAsync(string? imdbId, bool isTv, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(imdbId))
        {
            return null;
        }

        // 用户没填自己的 Key 时用内置公共 Key（omdbapi.com 免费额度，1000 次/天）
        var key = string.IsNullOrWhiteSpace(_settings.Current.OmdbApiKey)
            ? SettingsService.DefaultOmdbApiKey
            : _settings.Current.OmdbApiKey!.Trim();
        if (!string.IsNullOrEmpty(key))
        {
            var viaOmdb = await FetchOmdbAsync(imdbId!, key, ct);
            if (viaOmdb is not null)
            {
                return viaOmdb;
            }
        }

        return await FetchCinemetaImdbAsync(imdbId!, isTv, ct);
    }

    private async Task<(double Rating, int? Votes)?> FetchOmdbAsync(string imdbId, string key, CancellationToken ct)
    {
        try
        {
            // 接口地址可在设置页「外部服务」换成自建镜像；
            // 有人会把整条调用链接填进去，这里先归一化成「scheme://host/」
            var (root, _) = SettingsService.NormalizeOmdbEndpoint(_settings.Current.OmdbBaseUrl);
            var url = $"{root}?apikey={Uri.EscapeDataString(key)}&i={Uri.EscapeDataString(imdbId)}";
            await using var stream = await _http.GetStreamAsync(url, ct);
            var dto = await JsonSerializer.DeserializeAsync<OmdbResponse>(stream, JsonOptions, ct);
            if (dto is null || !string.IsNullOrEmpty(dto.Error))
            {
                return null;
            }
            if (double.TryParse(dto.ImdbRating, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var rating))
            {
                return (rating, ParseVotes(dto.ImdbVotes));
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>
    /// Cinemeta：Stremio 的公共元数据接口，按 IMDb id 取，不需要任何 Key。
    /// 只回分数不回票数，所以票数留空（界面上就不显示「N 人评分」）。
    /// </summary>
    private async Task<(double Rating, int? Votes)?> FetchCinemetaImdbAsync(string imdbId, bool isTv, CancellationToken ct)
    {
        try
        {
            var kind = isTv ? "series" : "movie";
            var url = $"https://v3-cinemeta.strem.io/meta/{kind}/{Uri.EscapeDataString(imdbId)}.json";
            await using var stream = await _http.GetStreamAsync(url, ct);
            var dto = await JsonSerializer.DeserializeAsync<CinemetaResponse>(stream, JsonOptions, ct);
            if (double.TryParse(dto?.Meta?.ImdbRating, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var rating) && rating > 0)
            {
                return (rating, null);
            }
        }
        catch
        {
        }
        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>OMDb 的票数是 "1,234,567" 这种带千分位的字符串。</summary>
    private static int ParseVotes(string? raw) =>
        int.TryParse(raw?.Replace(",", ""), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private sealed class OmdbResponse
    {
        [JsonPropertyName("imdbRating")]
        public string? ImdbRating { get; set; }

        [JsonPropertyName("imdbVotes")]
        public string? ImdbVotes { get; set; }

        [JsonPropertyName("Response")]
        public string? Response { get; set; }

        [JsonPropertyName("Error")]
        public string? Error { get; set; }
    }

    private sealed class CinemetaResponse
    {
        [JsonPropertyName("meta")]
        public CinemetaMeta? Meta { get; set; }
    }

    private sealed class CinemetaMeta
    {
        [JsonPropertyName("imdbRating")]
        public string? ImdbRating { get; set; }
    }
}
