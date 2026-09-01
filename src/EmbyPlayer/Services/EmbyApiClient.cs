using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EmbyPlayer.Models;

namespace EmbyPlayer.Services;

public sealed class EmbyApiException : Exception
{
    public int StatusCode { get; }

    public EmbyApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

public sealed class EmbyApiClient : IDisposable
{
    private const string ClientName = "EmbyPlayer";
    private const string ClientVersion = "1.0.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>服务器返回 401（凭据失效）时触发，应用应回到登录页。</summary>
    public event Action? Unauthorized;

    public string? ServerUrl { get; private set; }
    public string? Token { get; private set; }

    /// <summary>当前实例使用的用户 ID（可为某个服务器创建独立实例并指定其用户）。</summary>
    public string? UserId { get; private set; }

    public EmbyApiClient(SettingsService settings)
    {
        _settings = settings;
        if (_settings.IsLoggedIn)
        {
            Configure(_settings.ServerUrl!, _settings.AccessToken);
        }
    }

    public void Configure(string serverUrl, string? token) =>
        Configure(serverUrl, token, _settings.CurrentServer?.UserId);

    public void Configure(string serverUrl, string? token, string? userId)
    {
        ServerUrl = NormalizeUrl(serverUrl);
        Token = token;
        UserId = userId;

        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.BaseAddress = new Uri(ServerUrl);
        client.DefaultRequestHeaders.Add("X-Emby-Client", ClientName);
        client.DefaultRequestHeaders.Add("X-Emby-Client-Version", ClientVersion);
        client.DefaultRequestHeaders.Add("X-Emby-Device-Name", Environment.MachineName);
        client.DefaultRequestHeaders.Add("X-Emby-Device-Id", _settings.Current.DeviceId);
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Add("X-Emby-Token", token);
        }

        var old = _http;
        _http = client;
        old.Dispose();
    }

    public static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }
        return url.TrimEnd('/');
    }

    /// <summary>获取服务器的显示名称。优先匿名端点 /System/Info/public，失败时带 token 请求 /System/Info。</summary>
    public static async Task<string?> GetServerNameAsync(string baseUrl, string? token = null, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            // Emby/Jellyfin 的公开端点，无需登录
            try
            {
                var json = await http.GetStringAsync($"{NormalizeUrl(baseUrl)}/System/Info/public", ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("ServerName", out var name))
                {
                    return name.GetString();
                }
            }
            catch
            {
                // 继续尝试需要鉴权的端点
            }

            var request = new HttpRequestMessage(HttpMethod.Get, "/System/Info");
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Add("X-Emby-Token", token);
            }
            var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc2 = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc2.RootElement.TryGetProperty("ServerName", out var name2) ? name2.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<AuthenticationResult> AuthenticateByNameAsync(string username, string password, CancellationToken ct = default)
    {
        var authValue = $"MediaBrowser Client=\"{ClientName}\", Device=\"{Environment.MachineName}\", " +
                        $"DeviceId=\"{_settings.Current.DeviceId}\", Version=\"{ClientVersion}\"";
        var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { Username = username, Pw = password }),
                Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", authValue);

        var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new EmbyApiException((int)response.StatusCode, "登录失败：用户名、密码或服务器地址不正确。");
        }
        return await DeserializeAsync<AuthenticationResult>(response);
    }

    public Task<List<BaseItemDto>> GetViewsAsync(CancellationToken ct = default) =>
        GetAsync<QueryResult<BaseItemDto>>($"/Users/{UserId}/Views", ct)
            .ContinueWith(t => t.Result.Items, ct);

    public async Task<QueryResult<BaseItemDto>> GetItemsAsync(
        string parentId, string includeItemTypes, int limit = 500,
        string sortBy = "SortName", string sortOrder = "Ascending", CancellationToken ct = default)
    {
        var url = $"/Users/{UserId}/Items?ParentId={parentId}&Recursive=true" +
                  $"&IncludeItemTypes={includeItemTypes}" +
                  "&Fields=Overview,PrimaryImageAspectRatio,SortName,UserData" +
                  $"&ImageTypeLimit=1&SortBy={sortBy}&SortOrder={sortOrder}" +
                  $"&Limit={limit}";
        return await GetAsync<QueryResult<BaseItemDto>>(url, ct);
    }

    public async Task<BaseItemDto> GetItemAsync(string itemId, CancellationToken ct = default) =>
        await GetAsync<BaseItemDto>(
            $"/Users/{UserId}/Items/{itemId}?Fields=Overview,PrimaryImageAspectRatio,SortName,UserData", ct);

    public async Task<QueryResult<BaseItemDto>> GetSeasonsAsync(string seriesId, CancellationToken ct = default) =>
        await GetAsync<QueryResult<BaseItemDto>>(
            $"/Shows/{seriesId}/Seasons?UserId={UserId}&Fields=UserData", ct);

    public async Task<QueryResult<BaseItemDto>> GetEpisodesAsync(string seriesId, string seasonId, CancellationToken ct = default) =>
        await GetAsync<QueryResult<BaseItemDto>>(
            $"/Shows/{seriesId}/Episodes?UserId={UserId}&SeasonId={seasonId}" +
            "&Fields=Overview,PrimaryImageAspectRatio,UserData", ct);

    public async Task<BaseItemDto?> GetNextUpAsync(string seriesId, CancellationToken ct = default)
    {
        var result = await GetAsync<QueryResult<BaseItemDto>>(
            $"/Shows/NextUp?SeriesId={seriesId}&UserId={UserId}", ct);
        return result.Items.FirstOrDefault();
    }

    public async Task<PlaybackInfoResponse> GetPlaybackInfoAsync(string itemId, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/Items/{itemId}/PlaybackInfo")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    UserId,
                    MaxStreamingBitrate = 1_000_000_000,
                    StartTimeTicks = 0,
                    AutoOpenLiveStream = true
                }),
                Encoding.UTF8, "application/json")
        };
        var response = await SendAsync(request, ct);
        return await DeserializeAsync<PlaybackInfoResponse>(response);
    }

    public Task ReportPlayingAsync(PlaybackReport report, CancellationToken ct = default) =>
        PostAsync("/Sessions/Playing", report, ct);

    public Task ReportProgressAsync(PlaybackReport report, CancellationToken ct = default) =>
        PostAsync("/Sessions/Playing/Progress", report, ct);

    public Task ReportStoppedAsync(PlaybackReport report, CancellationToken ct = default) =>
        PostAsync("/Sessions/Playing/Stopped", report, ct);

    public Task MarkPlayedAsync(string itemId, CancellationToken ct = default) =>
        PostAsync($"/Users/{UserId}/PlayedItems/{itemId}", null, ct);

    public async Task<QueryResult<BaseItemDto>> SearchAsync(string term, int limit = 50, CancellationToken ct = default) =>
        await GetAsync<QueryResult<BaseItemDto>>(
            $"/Users/{UserId}/Items?SearchTerm={Uri.EscapeDataString(term)}&Recursive=true" +
            "&IncludeItemTypes=Movie,Series,Episode&Limit=" + limit +
            "&Fields=Overview,PrimaryImageAspectRatio,SortName,UserData&ImageTypeLimit=1", ct);

    public async Task<QueryResult<BaseItemDto>> GetResumeAsync(int limit = 50, CancellationToken ct = default) =>
        await GetAsync<QueryResult<BaseItemDto>>(
            $"/Users/{UserId}/Items/Resume?Limit={limit}&MediaTypes=Video" +
            "&Fields=Overview,PrimaryImageAspectRatio,SortName,UserData&ImageTypeLimit=1", ct);

    public async Task<QueryResult<BaseItemDto>> GetFavoritesAsync(int limit = 200, CancellationToken ct = default) =>
        await GetAsync<QueryResult<BaseItemDto>>(
            $"/Users/{UserId}/Items?Filters=IsFavorite&Recursive=true" +
            $"&IncludeItemTypes=Movie,Series,Episode&Limit={limit}" +
            "&Fields=Overview,PrimaryImageAspectRatio,SortName,UserData&ImageTypeLimit=1", ct);

    public Task SetFavoriteAsync(string itemId, bool favorite, CancellationToken ct = default) =>
        favorite
            ? PostAsync($"/Users/{UserId}/FavoriteItems/{itemId}", null, ct)
            : DeleteAsync($"/Users/{UserId}/FavoriteItems/{itemId}", ct);

    public string GetPrimaryImageUrl(string itemId, string? imageTag, int maxWidth = 400)
    {
        var tag = string.IsNullOrEmpty(imageTag) ? "" : $"&tag={Uri.EscapeDataString(imageTag)}";
        return $"{ServerUrl}/Items/{itemId}/Images/Primary?maxWidth={maxWidth}{tag}&api_key={Token}";
    }

    public string BuildStreamUrl(string itemId, string mediaSourceId, string? playSessionId)
    {
        var psid = string.IsNullOrEmpty(playSessionId) ? "" : $"&PlaySessionId={playSessionId}";
        return $"{ServerUrl}/Videos/{itemId}/stream?Static=true&MediaSourceId={mediaSourceId}{psid}&api_key={Token}";
    }

    public string BuildSubtitleUrl(string itemId, string mediaSourceId, MediaStreamInfo stream)
    {
        if (!string.IsNullOrEmpty(stream.DeliveryUrl))
        {
            return ServerUrl + stream.DeliveryUrl + (stream.DeliveryUrl.Contains('?') ? "&" : "?") + $"api_key={Token}";
        }
        return $"{ServerUrl}/Videos/{itemId}/{mediaSourceId}/Subtitles/{stream.Index}/Stream.srt?api_key={Token}";
    }

    public async Task DownloadSubtitleAsync(string url, string filePath, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var fs = File.Create(filePath);
        await response.Content.CopyToAsync(fs, ct);
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        return await DeserializeAsync<T>(response);
    }

    private async Task PostAsync(string url, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }
        using var response = await SendAsync(request, ct);
    }

    private async Task DeleteAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Delete, url), ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await _http.SendAsync(request, ct);
        if ((int)response.StatusCode == 401)
        {
            Unauthorized?.Invoke();
            throw new EmbyApiException(401, "认证已失效，请重新登录。");
        }
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new EmbyApiException((int)response.StatusCode,
                $"服务器返回错误 {(int)response.StatusCode}：{Truncate(text, 200)}");
        }
        return response;
    }

    private static async Task<T> DeserializeAsync<T>(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
        return result ?? throw new EmbyApiException(0, "服务器返回了无法解析的数据。");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose() => _http.Dispose();
}
