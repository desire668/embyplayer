using System.ComponentModel;
using System.Diagnostics;
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

    /// <summary>匹配到的安装包资源文件名（如 EmbyPlayer-Setup-x64-SC-1.0.5.exe）。</summary>
    public string InstallerAssetName { get; set; } = "";

    /// <summary>安装包下载直链（GitHub Release asset 的 browser_download_url）。</summary>
    public string InstallerUrl { get; set; } = "";

    /// <summary>安装包字节数（未知时为 0）。</summary>
    public long InstallerSize { get; set; }

    /// <summary>是否找到可直接下载的安装包（false 时只能跳转 Release 页面）。</summary>
    public bool HasInstaller => !string.IsNullOrEmpty(InstallerUrl);

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

/// <summary>安装包下载进度。</summary>
/// <param name="BytesReceived">已接收字节数。</param>
/// <param name="TotalBytes">总字节数（服务端未提供时为 null）。</param>
public sealed record DownloadProgress(long BytesReceived, long? TotalBytes)
{
    /// <summary>0.0 ~ 1.0 的进度比例（总大小未知时为 0）。</summary>
    public double Ratio => TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0.0, 1.0) : 0.0;

    /// <summary>已接收字节的可读文本，如 "12.4 MB"。</summary>
    public string ReceivedText => FormatBytes(BytesReceived);

    /// <summary>总字节的可读文本（未知时为 "未知大小"）。</summary>
    public string TotalText => TotalBytes is > 0 ? FormatBytes(TotalBytes.Value) : "未知大小";

    /// <summary>字节数格式化为 MB / KB 文本。</summary>
    public static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:F1} MB" : $"{bytes / 1024.0:F0} KB";
}

/// <summary>
/// 通过 GitHub API 检测 EmbyPlayer 仓库（desire668/embyplayer）是否有新 Release，
/// 并支持下载 Release 中的安装包、静默启动安装程序完成覆盖安装。
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
    /// 当前安装的部署变体：SC（自带 .NET 运行时）或 FD（依赖系统 .NET 9 Desktop Runtime）。
    /// 用于从 Release assets 中挑选相同变体的安装包，避免升级后运行时缺失或体积变大。
    /// </summary>
    public static string CurrentVariant => DetectVariant();

    /// <summary>安装包下载目录（%TEMP%\EmbyPlayer\Updates）。</summary>
    public static string UpdateDownloadDirectory => Path.Combine(Path.GetTempPath(), "EmbyPlayer", "Updates");

    /// <summary>
    /// 异步查询 GitHub 最新 Release，并返回是否比当前版本更新。
    /// 任何网络/解析失败均返回 null，不抛异常。
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = CreateClient();

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

            var (assetName, assetUrl, assetSize) = PickInstallerAsset(root, CurrentVariant);

            var info = new UpdateInfo
            {
                LatestTag = tag,
                LatestVersion = tag.TrimStart('v', 'V'),
                ReleaseUrl = url,
                ReleaseNotes = body,
                PublishedAt = published,
                InstallerAssetName = assetName,
                InstallerUrl = assetUrl,
                InstallerSize = assetSize
            };

            return info.IsNewerThan(CurrentVersion) ? info : null;
        }
        catch
        {
            // 网络异常、API 限流、JSON 解析失败等：静默处理
            return null;
        }
    }

    /// <summary>
    /// 下载指定版本的安装包到临时目录，并通过 progress 汇报进度。
    /// 返回下载完成的本地文件路径（已校验为完整文件，非 .part 临时文件）。
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        UpdateInfo info,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!info.HasInstaller)
        {
            throw new InvalidOperationException("该 Release 未提供可直接下载的安装包。");
        }

        Directory.CreateDirectory(UpdateDownloadDirectory);

        // 清理历史安装包，避免长期累积占用磁盘
        foreach (var stale in Directory.EnumerateFiles(UpdateDownloadDirectory, "*.exe"))
        {
            try { File.Delete(stale); } catch { /* 占用中则跳过 */ }
        }

        var fileName = string.IsNullOrWhiteSpace(info.InstallerAssetName)
            ? $"EmbyPlayer-Setup-{info.LatestTag}.exe"
            : info.InstallerAssetName;
        var dest = Path.Combine(UpdateDownloadDirectory, fileName);
        var temp = dest + ".part";
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }

        using var http = CreateClient();
        http.Timeout = Timeout.InfiniteTimeSpan;

        using var response = await http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? (info.InstallerSize > 0 ? info.InstallerSize : (long?)null);
        progress?.Report(new DownloadProgress(0, total));

        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long received = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                progress?.Report(new DownloadProgress(received, total));
            }
            await dst.FlushAsync(ct);
        }

        File.Move(temp, dest, overwrite: true);
        return dest;
    }

    /// <summary>
    /// 以静默方式启动下载好的安装包，自动覆盖安装到当前安装目录。
    /// 程序自身需在此之后立即退出，避免占用待覆盖的文件。
    /// </summary>
    /// <returns>是否成功启动安装进程。</returns>
    public static bool LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            return false;
        }

        var logPath = Path.Combine(UpdateDownloadDirectory, "install.log");
        try { Directory.CreateDirectory(UpdateDownloadDirectory); } catch { }

        var psi = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            // /VERYSILENT   ：无界面静默安装
            // /SUPPRESSMSGBOXES：不弹任何提示框
            // /NORESTART    ：不提示重启
            // /NOCANCEL     ：不显示取消按钮
            // /CLOSEAPPLICATIONS：自动关闭占用文件的进程
            // /SP-          ：跳过启动画面
            Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /SP- /CLOSEAPPLICATIONS /LOG=\"{logPath}\""
        };

        // 安装在 Program Files 等受保护目录时需要管理员权限，否则静默安装会因写入失败而中止
        if (RequiresElevation())
        {
            psi.Verb = "runas";
        }

        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception)
        {
            // 用户取消了 UAC 授权
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub API 要求 User-Agent
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"EmbyPlayer/{CurrentVersionString}");
        http.DefaultRequestValuesForGitHub();
        return http;
    }

    /// <summary>根据 Release assets 挑选与当前变体匹配的安装包。</summary>
    private static (string Name, string Url, long Size) PickInstallerAsset(JsonElement root, string variant)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return ("", "", 0);
        }

        var hasFallback = false;
        var fallbackName = "";
        var fallbackUrl = "";
        long fallbackSize = 0;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;

            if (string.IsNullOrEmpty(url) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (name.IndexOf("EmbyPlayer-Setup", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            // 精确匹配当前变体（如 EmbyPlayer-Setup-x64-SC-1.0.5.exe）
            if (name.IndexOf($"-{variant}-", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return (name, url, size);
            }

            if (!hasFallback)
            {
                hasFallback = true;
                fallbackName = name;
                fallbackUrl = url;
                fallbackSize = size;
            }
        }

        return hasFallback ? (fallbackName, fallbackUrl, fallbackSize) : ("", "", 0);
    }

    /// <summary>判断当前安装是否自带 .NET 运行时（SC），否则为 FD。</summary>
    private static string DetectVariant()
    {
        var baseDir = AppContext.BaseDirectory;
        string[] selfContainedMarkers = { "coreclr.dll", "hostfxr.dll", "System.Private.CoreLib.dll" };
        return selfContainedMarkers.Any(m => File.Exists(Path.Combine(baseDir, m))) ? "SC" : "FD";
    }

    /// <summary>当前安装目录位于受保护目录（Program Files 等）时，覆盖安装需要提权。</summary>
    private static bool RequiresElevation()
    {
        var dir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string[] protectedDirs =
        {
            Environment.GetEnvironmentVariable("ProgramFiles") ?? "",
            Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "",
            Environment.GetEnvironmentVariable("SystemRoot") ?? ""
        };
        return protectedDirs.Any(p => !string.IsNullOrEmpty(p) && dir.StartsWith(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
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
