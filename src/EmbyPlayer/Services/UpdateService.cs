using System.Reflection;
using System.Text.Json;

namespace EmbyPlayer.Services;

/// <summary>GitHub Release 最新版本信息。</summary>
public sealed class UpdateInfo
{
    /// <summary>最新版本号（原始 tag，含 v 前缀，如 "v1.0.1"）。</summary>
    public string LatestTag { get; set; } = "";

    /// <summary>不含 v 前缀的版本号字符串，如 "1.0.1"。</summary>
    public string LatestVersion { get; set; } = "";

    /// <summary>Release 页面 URL（用于浏览器打开下载）。</summary>
    public string ReleaseUrl { get; set; } = "";

    /// <summary>Release notes（Markdown 原文）。</summary>
    public string ReleaseNotes { get; set; } = "";

    /// <summary>发布时间（UTC）。</summary>
    public DateTimeOffset PublishedAt { get; set; }

    /// <summary>当前版本是否落后于 LatestVersion。</summary>
    public bool IsNewerThan(Version current)
    {
        if (string.IsNullOrEmpty(LatestVersion))
        {
            return false;
        }
        if (!Version.TryParse(LatestVersion, out var latest))
        {
            return false;
        }
        // Version 比较忽略 Revision（均为 -1 时只比 Major.Minor.Build）
        return latest > current;
    }
}

/// <summary>
/// 通过 GitHub API 检测 EmbyPlayer 仓库（desire668/embyplayer）是否有新 Release。
/// 所有失败情况静默返回 null，不打扰用户。
/// </summary>
public sealed class UpdateService
{
    private const string RepoOwner = "desire668";
    private const string RepoName = "embyplayer";
    private const string RepoUrl = $"https://github.com/{RepoOwner}/{RepoName}";
    private const string LatestReleaseUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>仓库主页 URL（设置页「关于」展示）。</summary>
    public static string RepositoryUrl => RepoUrl;

    /// <summary>当前程序集版本（来自 csproj AssemblyVersion，自动与构建同步）。</summary>
    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    /// <summary>不含 v 前缀的当前版本字符串（3 段：Major.Minor.Build）。</summary>
    public static string CurrentVersionString => CurrentVersion.ToString(3);

    /// <summary>
    /// 异步查询 GitHub 最新 Release，并返回是否比当前版本更新。
    /// 任何网络/解析失败均返回 null，不抛异常。
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // GitHub API 要求 User-Agent
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"EmbyPlayer/{CurrentVersionString}");
            http.DefaultRequestValuesForGitHub();

            using var response = await http.GetAsync(LatestReleaseUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            var url = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? "" : "";
            var body = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
            var published = root.TryGetProperty("published_at", out var pubEl) && DateTimeOffset.TryParse(pubEl.GetString(), out var dto) ? dto : DateTimeOffset.UtcNow;

            var info = new UpdateInfo
            {
                LatestTag = tag,
                LatestVersion = tag.TrimStart('v', 'V'),
                ReleaseUrl = url,
                ReleaseNotes = body,
                PublishedAt = published
            };

            return info.IsNewerThan(CurrentVersion) ? info : null;
        }
        catch
        {
            // 网络异常、API 限流、JSON 解析失败等：静默处理
            return null;
        }
    }
}

internal static class HttpClientExtensions
{
    /// <summary>为 GitHub API 设置默认 Accept header（vnd.github+json）。</summary>
    public static void DefaultRequestValuesForGitHub(this HttpClient http)
    {
        if (http.DefaultRequestHeaders.Accept.All(h => h.MediaType != "application/vnd.github+json"))
        {
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }
    }
}
