using System.Net.Http.Headers;
using System.Text.Json;
using EmbyPlayer.Models;

namespace EmbyPlayer.Services;

/// <summary>
/// TMDB（themoviedb.org）只读客户端：海报墙首页大图轮播的推荐数据源。
/// 走 v3 api_key 鉴权（文档：https://developer.themoviedb.org/docs/getting-started），
/// 只访问公开的 trending / movie 详情端点，语言统一 zh-CN。
/// </summary>
public sealed class TmdbClient
{
    /// <summary>官方默认接口根地址；用户在设置页填了自定义地址时改用它。</summary>
    private const string DefaultBaseUrl = "https://api.themoviedb.org/3";

    private const string DefaultImageBaseUrl = "https://image.tmdb.org/t/p";

    private const string DefaultImageProxy = "https://wsrv.nl/?url=";

    /// <summary>实际接口根地址：设置页「外部服务」里可换成自建镜像 / 反代。</summary>
    private string BaseUrl =>
        string.IsNullOrWhiteSpace(_settings.Current.TmdbBaseUrl)
            ? DefaultBaseUrl
            : _settings.Current.TmdbBaseUrl!.Trim().TrimEnd('/');

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SettingsService _settings;

    /// <summary>
    /// Key 来自设置页（本机 settings.json），不写死在源码里——仓库是公开的，
    /// 硬编码会被他人取用并耗尽配额。
    /// </summary>
    public TmdbClient(SettingsService settings) => _settings = settings;

    /// <summary>是否已填写 API Key。未填写时所有请求直接失败，由调用方提示用户去设置页填写。</summary>
    public bool HasApiKey => !string.IsNullOrWhiteSpace(_settings.Current.TmdbApiKey);

    private string ApiKey =>
        _settings.Current.TmdbApiKey?.Trim()
        ?? throw new InvalidOperationException("尚未配置 TMDB API Key，请在「设置 → 外部服务」中填写。");

    /// <summary>海报墙大图请求宽度，与 EmbyApiClient.BackdropWidth 对齐。</summary>
    public const int BackdropWidth = 1280;

    /// <summary>拼 TMDB 图片 CDN 地址；path 为空返回 null。图片前缀可在设置页换成镜像。</summary>
    public string? GetImageUrl(string? path, int width = BackdropWidth)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var imageBase = string.IsNullOrWhiteSpace(_settings.Current.TmdbImageBaseUrl)
            ? DefaultImageBaseUrl
            : _settings.Current.TmdbImageBaseUrl!.Trim().TrimEnd('/');
        return $"{imageBase}/w{width}{path}";
    }

    /// <summary>
    /// 图片备用地址：走公共图片代理（默认 wsrv.nl）。
    /// 国内直连 image.tmdb.org 常被拦（TLS 握手失败），主图源加载失败时用它兜底。
    /// </summary>
    public string? GetFallbackImageUrl(string? path, int width = BackdropWidth)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var proxy = string.IsNullOrWhiteSpace(_settings.Current.ImageProxyBaseUrl)
            ? DefaultImageProxy
            : _settings.Current.ImageProxyBaseUrl!.Trim();
        var imageBase = string.IsNullOrWhiteSpace(_settings.Current.TmdbImageBaseUrl)
            ? DefaultImageBaseUrl
            : _settings.Current.TmdbImageBaseUrl!.Trim().TrimEnd('/');
        return $"{proxy}{imageBase}/w{width}{path}";
    }

    /// <summary>本周热门电影（zh-CN），海报墙轮播数据源。</summary>
    public async Task<List<TmdbMovie>> GetTrendingAsync(CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/trending/movie/week?language=zh-CN";
        var response = await GetAsync<TmdbPagedResponse<TmdbMovie>>(url, ct);
        return response.Results;
    }

    /// <summary>受欢迎的电影（zh-CN），首页「受欢迎的影片」一行的数据源。</summary>
    public async Task<List<TmdbMovie>> GetPopularAsync(CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/movie/popular?language=zh-CN";
        var response = await GetAsync<TmdbPagedResponse<TmdbMovie>>(url, ct);
        return response.Results;
    }

    /// <summary>
    /// 取条目在 IMDb 的 id（external_ids）。Emby 里不少条目只刮到 TMDB id，
    /// 没有 IMDb id 就查不了 IMDb / Trakt 评分，这里补一次。
    /// </summary>
    public async Task<string?> GetImdbIdAsync(string kind, string tmdbId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/{kind}/{Uri.EscapeDataString(tmdbId)}/external_ids";
        var dto = await GetAsync<TmdbExternalIds>(url, ct);
        return string.IsNullOrWhiteSpace(dto.ImdbId) ? null : dto.ImdbId;
    }

    /// <summary>按片名（+年份）搜索：条目完全没有 provider id 时的兜底。</summary>
    public async Task<TmdbMovie?> SearchAsync(string name, int? year, bool isTv, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var kind = isTv ? "tv" : "movie";
        var url = $"{BaseUrl}/search/{kind}?language=zh-CN&query={Uri.EscapeDataString(name)}"
                  + (year is int y and > 0 ? $"&year={y}" : "");
        var response = await GetAsync<TmdbPagedResponse<TmdbMovie>>(url, ct);
        return response.Results.FirstOrDefault();
    }

    /// <summary>剧名 logo 的请求宽度：详情页标题区最宽 480，500 够了，再大也是浪费流量。</summary>
    public const int LogoWidth = 500;

    /// <summary>
    /// 取剧名 logo（images 接口里的 logos）。挑法：先按语言（中文 → 英文 → 无字），
    /// 同语言里优先 png（WinUI 的位图解码不了 svg，虽然能显示但多了一种数据源），
    /// 最后按社区评分降序 —— 也就是「中文台词 + 位图 + 大家觉得好看的那张」。
    /// 一张都没有时返回 null，由调用方退回纯文字标题。
    /// </summary>
    public async Task<string?> GetLogoPathAsync(string kind, string tmdbId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tmdbId))
        {
            return null;
        }

        // include_image_language 用逗号分隔多语言，null 表示「没有语言标记的图」；
        // 不传这个参数时 TMDB 只回默认语言的那批，中文 logo 往往取不到。
        // api_key / Bearer 由 BuildRequest 统一补上。
        var url = $"{BaseUrl}/{kind}/{Uri.EscapeDataString(tmdbId)}/images"
                  + "?include_image_language=zh,zh-CN,en,ja,ko,null";
        TmdbImages images;
        try
        {
            images = await GetAsync<TmdbImages>(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                   or JsonException or InvalidOperationException)
        {
            return null;
        }

        var best = images.Logos
            .Where(l => !string.IsNullOrEmpty(l.FilePath))
            .OrderBy(LanguageRank)
            .ThenBy(IsSvg)
            .ThenByDescending(l => l.VoteAverage ?? 0)
            .Select(l => l.FilePath!)
            .FirstOrDefault();

        return best;

        // zh 最优、en 次之、其它语言再次、无字（null）最后
        static int LanguageRank(TmdbImage logo) => logo.Language switch
        {
            null or "" => 3,
            "zh" or "zh-CN" or "zh-TW" or "zh-HK" => 0,
            "en" or "en-US" => 1,
            _ => 2
        };

        static bool IsSvg(TmdbImage logo) =>
            logo.FilePath!.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>电影详情（补片长与类型名，zh-CN）。</summary>
    public async Task<TmdbMovieDetail?> GetMovieDetailAsync(int movieId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/movie/{movieId}?language=zh-CN";
        return await GetAsync<TmdbMovieDetail>(url, ct);
    }

    /// <summary>
    /// 按 TMDB 自己的 id 取评分。<paramref name="kind"/> 为 "movie" 或 "tv"
    /// （剧集用 tv，两者评分字段相同）。没有评分时返回 null。
    /// </summary>
    public async Task<TmdbRating?> GetRatingAsync(string kind, string tmdbId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/{kind}/{Uri.EscapeDataString(tmdbId)}?language=zh-CN";
        var detail = await GetAsync<TmdbMovieDetail>(url, ct);
        return detail.VoteAverage is double vote and > 0
            ? new TmdbRating(detail.Id, vote, detail.VoteCount ?? 0)
            : null;
    }

    /// <summary>用 IMDb id 反查 TMDB 条目并带回评分（Emby 里常常只刮到 IMDb id）。</summary>
    public async Task<TmdbRating?> FindByImdbAsync(string imdbId, bool isTv, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/find/{Uri.EscapeDataString(imdbId)}?external_source=imdb_id&language=zh-CN";
        var found = await GetAsync<TmdbFindResponse>(url, ct);
        var match = (isTv ? found.TvResults : found.MovieResults).FirstOrDefault()
                    ?? found.MovieResults.FirstOrDefault()
                    ?? found.TvResults.FirstOrDefault();
        return match is { VoteAverage: double vote and > 0 }
            ? new TmdbRating(match.Id, vote, match.VoteCount ?? 0)
            : null;
    }

    /// <summary>
    /// 按 Key 的形态拼请求：
    /// v3「API Key」（32 位十六进制）走 <c>?api_key=</c>；
    /// v4「Read Access Token」（eyJ 开头的长串）走 <c>Authorization: Bearer</c>。
    /// 用户从 TMDB 后台复制到的多半是 v4 令牌，两种都支持就不用让他再去找另一种。
    /// </summary>
    private HttpRequestMessage BuildRequest(string url)
    {
        var key = ApiKey;
        if (IsBearerToken(key))
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return request;
        }

        return new HttpRequestMessage(HttpMethod.Get, url + "&api_key=" + Uri.EscapeDataString(key));
    }

    private static bool IsBearerToken(string key) =>
        key.StartsWith("eyJ", StringComparison.OrdinalIgnoreCase) || key.Length > 64;

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        // TMDB 经由某些网络时偶发「连接被重置 / 0 字节响应」，重试两次即可，
        // 否则首页会一次抖动就直接空屏
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = BuildRequest(url);
                using var response = await _http.SendAsync(request, ct);

                // 401 直接给出可操作的中文提示，不重试（重试也不会变对）
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    throw new InvalidOperationException(
                        "API Key / 令牌无效（401）。请回到 TMDB 后台重新复制完整：32 位的 v3「API Key」，"
                        + "或以 eyJ 开头的 v4「Read Access Token」，两者都支持。");
                }

                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var result = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
                return result ?? throw new InvalidOperationException("TMDB 返回了无法解析的数据。");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                       && !ct.IsCancellationRequested)
            {
                last = ex;
                try
                {
                    await Task.Delay(400 * (attempt + 1), ct);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        throw last ?? new InvalidOperationException("TMDB 请求失败。");
    }
}
