using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace EmbyPlayer.Services;

/// <summary>缓存分类：元数据 / 图片与网络 / 弹幕 / 播放。</summary>
public enum CacheKind
{
    Metadata,
    Images,
    Danmaku,
    Playback
}

/// <summary>
/// 各类本地缓存的统一收口：目录在哪、多大、怎么清、超了上限怎么淘汰。
///
/// 之前各服务各写各的临时目录（弹幕写 temp、字幕写 temp、图片只放内存），
/// 缓存管理页没法统一统计；这里统一到
/// <c>%LOCALAPPDATA%\EmbyPlayer\Cache\{metadata|images|danmaku|playback}</c>。
/// </summary>
public static class CacheService
{
    /// <summary>缓存根目录。</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EmbyPlayer", "Cache");

    /// <summary>某一类缓存的目录（不存在时创建）。</summary>
    public static string DirectoryOf(CacheKind kind)
    {
        var dir = Path.Combine(Root, FolderName(kind));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FolderName(CacheKind kind) => kind switch
    {
        CacheKind.Metadata => "metadata",
        CacheKind.Images => "images",
        CacheKind.Danmaku => "danmaku",
        CacheKind.Playback => "playback",
        _ => "other"
    };

    /// <summary>统计某一类缓存：总字节数 + 文件数（含子目录）。</summary>
    public static (long Bytes, int Count) Measure(CacheKind kind)
    {
        var dir = Path.Combine(Root, FolderName(kind));
        if (!Directory.Exists(dir))
        {
            return (0, 0);
        }

        long bytes = 0;
        var count = 0;
        try
        {
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                bytes += file.Length;
                count++;
            }
        }
        catch
        {
            // 正在被占用 / 权限问题：按已统计到的算，不让页面崩
        }

        return (bytes, count);
    }

    /// <summary>删除某一类缓存的全部文件，返回成功删除的个数（被占用的会跳过）。</summary>
    public static int Clear(CacheKind kind)
    {
        var dir = Path.Combine(Root, FolderName(kind));
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        var removed = 0;
        try
        {
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    file.Delete();
                    removed++;
                }
                catch
                {
                    // 播放中的字幕 / 弹幕文件被占用，跳过
                }
            }

            foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories())
            {
                try
                {
                    sub.Delete(true);
                }
                catch
                {
                    // 同上
                }
            }
        }
        catch
        {
            // 目录枚举失败：当作没删掉，页面照常刷新
        }

        return removed;
    }

    /// <summary>清空所有分类。</summary>
    public static int ClearAll() =>
        Clear(CacheKind.Metadata) + Clear(CacheKind.Images) +
        Clear(CacheKind.Danmaku) + Clear(CacheKind.Playback);

    /// <summary>
    /// 超过上限时按「最久没用过」淘汰，直到总量回到上限以内。limitBytes &lt;= 0 表示不限制。
    /// </summary>
    public static void Trim(CacheKind kind, long limitBytes)
    {
        if (limitBytes <= 0)
        {
            return;
        }

        var dir = Path.Combine(Root, FolderName(kind));
        if (!Directory.Exists(dir))
        {
            return;
        }

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .OrderBy(f => f.LastAccessTimeUtc)
                .ToList();
        }
        catch
        {
            return;
        }

        var total = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (total <= limitBytes)
            {
                break;
            }

            try
            {
                total -= file.Length;
                file.Delete();
            }
            catch
            {
                // 被占用：留给下一轮
            }
        }
    }

    // ==================== 元数据缓存（API 响应） ====================

    /// <summary>缓存一个请求的响应体，key 用请求 URL。返回写入的路径。</summary>
    public static string WriteMetadata(string url, string content)
    {
        var dir = DirectoryOf(CacheKind.Metadata);
        var path = Path.Combine(dir, Hash(url) + ".json");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// 读取尚未过期的响应体；过期 / 不存在 / ttl 非正（= 未启用缓存）时返回 null。
    /// </summary>
    public static string? ReadMetadata(string url, TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
        {
            return null;
        }

        var path = Path.Combine(Path.Combine(Root, FolderName(CacheKind.Metadata)), Hash(url) + ".json");
        if (!File.Exists(path))
        {
            return null;
        }

        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > ttl)
        {
            return null;
        }

        return File.ReadAllText(path);
    }

    /// <summary>由 URL 推断磁盘缓存文件名（图片用，尽量保留扩展名）。</summary>
    public static string PathFor(string url, string defaultExtension = ".bin")
    {
        var ext = defaultExtension;
        var path = url.Split('?')[0];
        var dot = path.LastIndexOf('.');
        var slash = path.LastIndexOf('/');
        if (dot > slash && dot >= 0 && path.Length - dot <= 6)
        {
            ext = path[dot..];
        }

        return Hash(url) + ext;
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>MB → 字节；小于等于 0 表示不限制。</summary>
    public static long ToBytes(double megabytes) =>
        megabytes <= 0 ? 0 : (long)Math.Round(megabytes * 1024 * 1024, MidpointRounding.AwayFromZero);
}
