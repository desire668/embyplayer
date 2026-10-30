using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyPlayer.Services;

/// <summary>
/// 海报位图的进程内共享缓存。
///
/// 解决的问题：列表页与详情页各自 new 一个 BitmapImage，即使 UriSource 完全相同，
/// 也会各自发起一次 HTTP 请求并重新解码；导航到详情页后列表页被销毁（Frame 默认
/// 不缓存页面），解码结果随即被释放 —— 于是详情页的海报必然重新加载一遍。
///
/// 做法：以 URL 为 key 强引用缓存已解码的 BitmapImage，列表与详情共用同一实例。
/// 命中缓存时 Image.Source 直接指向已解码位图，零网络、零解码、零闪烁。
/// </summary>
public static class ImageCacheService
{
    /// <summary>缓存条目上限，超出按 LRU 淘汰，控制内存占用。</summary>
    private const int Capacity = 120;

    /// <summary>
    /// 解码宽度（逻辑像素）。海报 URL 向服务器请求的是 400px 宽，
    /// 卡片实际显示 180px、详情页 220px，按 300 解码在 1.5x DPI 下仍然清晰，
    /// 同时把单张内存压到约 500KB。
    /// </summary>
    private const int DecodeWidth = 300;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, LinkedListNode<Entry>> Index = new();
    private static readonly LinkedList<Entry> Lru = new();

    /// <summary>把远程图片顺势存一份到磁盘缓存目录（只负责下载，失败就下次再说）。</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private sealed class Entry
    {
        public Entry(string key, ImageSource image)
        {
            Key = key;
            Image = image;
        }

        public string Key { get; }
        public ImageSource Image { get; }
    }

    /// <summary>
    /// 取（或建）与 url 对应的位图。必须在 UI 线程调用 —— BitmapImage 是
    /// DependencyObject，有线程亲和性；绑定求值本来就在 UI 线程，正常路径不会踩到。
    /// </summary>
    public static ImageSource? Get(string? url, string? fallbackUrl = null, int? decodeWidth = null)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        lock (Gate)
        {
            if (Index.TryGetValue(url, out var existing))
            {
                // 命中：提到队首，保持 LRU 顺序
                Lru.Remove(existing);
                Lru.AddFirst(existing);
                return existing.Value.Image;
            }
        }

        // 磁盘缓存命中：直接读本地文件，零网络；没命中就照旧从远程加载，并在后台存一份
        var disk = DiskHit(url);

        if (CreateSource(disk ?? url, decodeWidth ?? DecodeWidth) is not { } image)
        {
            // 非 UI 线程或 URL 非法：退回 null，卡片显示首字母占位，不让整页崩掉
            return null;
        }

        if (disk is null)
        {
            if (!string.IsNullOrEmpty(fallbackUrl))
            {
                AttachFallback(image, fallbackUrl!);
            }

            ScheduleDiskCache(url);
        }

        lock (Gate)
        {
            // 并发下可能已被别的线程建好，以先建成的为准
            if (Index.TryGetValue(url, out var raced))
            {
                return raced.Value.Image;
            }

            var node = Lru.AddFirst(new Entry(url, image));
            Index[url] = node;

            while (Lru.Count > Capacity)
            {
                var last = Lru.Last;
                if (last is null)
                {
                    break;
                }

                Lru.RemoveLast();
                Index.Remove(last.Value.Key);
            }

            return image;
        }
    }

    /// <summary>
    /// 按 URL 建 ImageSource：png / jpg 用 BitmapImage（可按显示尺寸解码省内存），
    /// svg 用 SvgImageSource —— TMDB 的剧名 logo 有不少是 svg，BitmapImage 解不了。
    /// 依赖对象必须在 UI 线程创建，构造失败（URL 非法 / 非 UI 线程）返回 null。
    /// </summary>
    private static ImageSource? CreateSource(string url, int decodeWidth)
    {
        try
        {
            if (url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                // 矢量图按要给的尺寸光栅化，宽度给了就按比例算高度
                return new SvgImageSource(new Uri(url)) { RasterizePixelWidth = decodeWidth };
            }

            return new BitmapImage
            {
                UriSource = new Uri(url),
                // 大图用途（如详情页背景）按接近原始尺寸解码，避免放大后糊成一片
                DecodePixelWidth = decodeWidth,
                DecodePixelType = DecodePixelType.Logical,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>磁盘缓存里已有这张图就返回本地路径，否则 null。</summary>
    private static string? DiskHit(string url)
    {
        try
        {
            var path = Path.Combine(CacheService.DirectoryOf(CacheKind.Images), CacheService.PathFor(url, ".img"));
            return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>后台把远程图片存进磁盘缓存，并按上限淘汰最旧的。</summary>
    private static void ScheduleDiskCache(string url)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var response = await Http.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    return;
                }

                var path = Path.Combine(CacheService.DirectoryOf(CacheKind.Images), CacheService.PathFor(url, ".img"));
                var temp = path + ".tmp";
                await using (var fs = File.Create(temp))
                {
                    await response.Content.CopyToAsync(fs);
                }

                File.Move(temp, path, true);
                TrimDisk();
            }
            catch
            {
                // 下载失败 / 目录不可写：不影响显示，下次再试
            }
        });
    }

    private static void TrimDisk()
    {
        var settings = App.Services?.GetService<SettingsService>();
        if (settings is null)
        {
            return;
        }

        CacheService.Trim(CacheKind.Images, CacheService.ToBytes(settings.Current.ImageCacheLimitMb));
    }

    /// <summary>
    /// 主地址加载失败时切到备用地址（TMDB 图片在国内直连常被拦，改用镜像兜底）。
    /// 只切一次——已指向备用地址时不再处理，避免失败后无限重试。
    /// </summary>
    private static void AttachFallback(ImageSource image, string fallbackUrl)
    {
        switch (image)
        {
            case BitmapImage bitmap:
                bitmap.ImageFailed += (_, _) => SwitchToFallback(bitmap, fallbackUrl);
                break;
            case SvgImageSource svg:
                svg.OpenFailed += (_, _) => SwitchToFallback(svg, fallbackUrl);
                break;
        }
    }

    /// <summary>真正换成备用地址：已指向备用地址时不再处理，避免失败后无限重试。</summary>
    private static void SwitchToFallback(ImageSource image, string fallbackUrl)
    {
        try
        {
            var current = image switch
            {
                BitmapImage bitmap => bitmap.UriSource?.OriginalString,
                SvgImageSource svg => svg.UriSource?.OriginalString,
                _ => null
            };
            if (string.Equals(current, fallbackUrl, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            switch (image)
            {
                case BitmapImage bitmap:
                    bitmap.UriSource = new Uri(fallbackUrl);
                    break;
                case SvgImageSource svg:
                    svg.UriSource = new Uri(fallbackUrl);
                    break;
            }
        }
        catch
        {
            // 兜底也失败：保留占位，不影响其它图片
        }
    }

    /// <summary>清空缓存（切换服务器 / 重新登录 / 缓存页点清理时调用，避免串图）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Lru.Clear();
            Index.Clear();
        }

        CacheService.Clear(CacheKind.Images);
    }
}
