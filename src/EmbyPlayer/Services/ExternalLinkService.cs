using System;
using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyPlayer.Services;

/// <summary>设置页「外部服务」里可连接的服务标识。</summary>
public enum ExternalServiceId
{
    Tmdb,
    Omdb
}

/// <summary>连接状态：未配置 / 已保存待验证 / 校验中 / 已连接 / 失败。</summary>
public enum ExternalLinkState
{
    NotConfigured,
    Saved,
    Checking,
    Connected,
    Failed
}

/// <summary>
/// 外部服务联通性校验：拿用户在设置页填的凭据真的打一次接口，
/// 让用户当场知道「填对了没有」，而不是等到首页空屏才发现问题。
/// 只读端点、不写任何数据，全程短超时，失败只影响状态文字。
/// </summary>
public static class ExternalLinkService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly HttpClient Http = CreateClient();

    /// <summary>带上 User-Agent：部分公共 API（含 OMDb 前面的 CDN）会拒掉空 UA 的请求。</summary>
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EmbyPlayer/1.0");
        return client;
    }

    /// <summary>校验成功返回 (true, 说明)；失败返回 (false, 中文原因)。</summary>
    public static async Task<(bool Ok, string Message)> VerifyAsync(
        ExternalServiceId id, string? credential, AppSettings settings, CancellationToken ct = default)
    {
        var key = credential?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return (false, "还没有填写凭据。");
        }

        return id switch
        {
            ExternalServiceId.Tmdb => await VerifyTmdbAsync(key!, settings, ct),
            ExternalServiceId.Omdb => await VerifyOmdbAsync(key!, settings, ct),
            _ => (false, "未知的服务类型。")
        };
    }

    /// <summary>TMDB：/authentication 只要 Key 合法就返回 success=true。</summary>
    private static async Task<(bool Ok, string Message)> VerifyTmdbAsync(
        string key, AppSettings settings, CancellationToken ct)
    {
        var root = string.IsNullOrWhiteSpace(settings.TmdbBaseUrl)
            ? "https://api.themoviedb.org/3"
            : settings.TmdbBaseUrl!.Trim().TrimEnd('/');
        // 与 TmdbClient.BuildRequest 同规则：v4 令牌走 Bearer，v3 Key 走 ?api_key
        var isBearer = key.StartsWith("eyJ", StringComparison.OrdinalIgnoreCase) || key.Length > 64;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            isBearer ? $"{root}/authentication?language=zh-CN"
                     : $"{root}/authentication?language=zh-CN&api_key={Uri.EscapeDataString(key)}");
        if (isBearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        try
        {
            using var response = await Http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return (false, "Key 无效（401）。请到 TMDB 后台重新复制完整的 v3 API Key 或 v4 令牌。");
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var dto = await JsonSerializer.DeserializeAsync<TmdbAuthResponse>(stream, JsonOptions, ct);
            return dto?.Success is true
                ? (true, "已连接：接口正常，可拉取热门趋势与评分。")
                : (false, "接口返回异常，请检查 Key 或自定义接口地址。");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, $"连不上（{ShortError(ex)}）。若在国内可试试填写镜像地址。");
        }
    }

    /// <summary>OMDb：拿一部固定影片试查，Response=True 即 Key 可用。</summary>
    private static async Task<(bool Ok, string Message)> VerifyOmdbAsync(
        string key, AppSettings settings, CancellationToken ct)
    {
        // 接口地址容错：填了整条调用链接时自动拆出根地址与 apikey
        var (root, keyInUrl) = SettingsService.NormalizeOmdbEndpoint(settings.OmdbBaseUrl);
        // 从网页/剪贴板复制来的 Key 常带零宽字符或首尾空白，直接拼进 URL 就变 Invalid API key
        var clean = string.IsNullOrEmpty(keyInUrl)
            ? new string(key.Where(char.IsLetterOrDigit).ToArray())
            : keyInUrl;
        var url = $"{root}?apikey={Uri.EscapeDataString(clean)}&t=Inception";

        try
        {
            // 不走 GetStreamAsync：它拿不到错误响应体，401/403 时只能看到状态码，
            // 而 OMDb 会把真正原因（Key 无效 / 额度用完 / 请求被拦）写在 body 里
            using var response = await Http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "omdb-verify.log"),
                    $"[{DateTime.Now:HH:mm:ss}] {url} -> {(int)response.StatusCode} body={body}\n");
            }
            catch
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var reason = body.Length > 160 ? body[..160] : body;
                return (false, $"接口返回 {(int)response.StatusCode}：{reason}");
            }

            var dto = JsonSerializer.Deserialize<OmdbProbe>(body, JsonOptions);
            if (dto is null)
            {
                return (false, "接口返回了无法解析的数据。");
            }

            if (!string.IsNullOrEmpty(dto.Error))
            {
                return (false, dto.Error.Contains("key", StringComparison.OrdinalIgnoreCase)
                    || dto.Error.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                    ? "Key 无效：" + dto.Error
                    : "接口返回错误：" + dto.Error);
            }

            return string.Equals(dto.Response, "True", StringComparison.OrdinalIgnoreCase)
                ? (true, "已连接：详情页可显示 IMDb 评分。")
                : (false, "接口可用但查不到测试影片，请检查 Key 的额度。");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return (false, "服务器拒绝访问（403）：多半是网络代理拦了明文 http，把接口地址改成 https 再试。");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, $"连不上（{ShortError(ex)}）。");
        }
    }

    private static string ShortError(Exception ex) => ex switch
    {
        TaskCanceledException => "超时",
        HttpRequestException e => e.StatusCode is null ? "网络错误" : $"HTTP {(int)e.StatusCode}",
        _ => "请求失败"
    };

    private sealed class TmdbAuthResponse
    {
        public bool Success { get; set; }
    }

    private sealed class OmdbProbe
    {
        public string? Response { get; set; }
        public string? Error { get; set; }
    }
}
