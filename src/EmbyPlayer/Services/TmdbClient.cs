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
    private const string BaseUrl = "https://api.themoviedb.org/3";

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
        ?? throw new InvalidOperationException("尚未配置 TMDB API Key，请在「设置 → 在线内容（TMDB）」中填写。");

    /// <summary>海报墙大图请求宽度，与 EmbyApiClient.BackdropWidth 对齐。</summary>
    public const int BackdropWidth = 1280;

    /// <summary>拼 TMDB 图片 CDN 地址；path 为空返回 null。</summary>
    public static string? GetImageUrl(string? path, int width = BackdropWidth) =>
        string.IsNullOrEmpty(path) ? null : $"https://image.tmdb.org/t/p/w{width}{path}";

    /// <summary>
    /// 图片备用地址：走公共图片代理 images.weserv.nl。
    /// 国内直连 image.tmdb.org 常被拦（TLS 握手失败），主图源加载失败时用它兜底。
    /// </summary>
    public static string? GetFallbackImageUrl(string? path, int width = BackdropWidth) =>
        string.IsNullOrEmpty(path) ? null : $"https://wsrv.nl/?url=image.tmdb.org/t/p/w{width}{path}";

    /// <summary>本周热门电影（zh-CN），海报墙轮播数据源。</summary>
    public async Task<List<TmdbMovie>> GetTrendingAsync(CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/trending/movie/week?language=zh-CN&api_key={ApiKey}";
        var response = await GetAsync<TmdbPagedResponse<TmdbMovie>>(url, ct);
        return response.Results;
    }

    /// <summary>电影详情（补片长与类型名，zh-CN）。</summary>
    public async Task<TmdbMovieDetail?> GetMovieDetailAsync(int movieId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/movie/{movieId}?language=zh-CN&api_key={ApiKey}";
        return await GetAsync<TmdbMovieDetail>(url, ct);
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        // TMDB 经由某些网络时偶发「连接被重置 / 0 字节响应」，重试两次即可，
        // 否则首页会一次抖动就直接空屏
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await _http.GetAsync(url, ct);
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
