using System;
using System.Collections.Generic;
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

    private sealed class Entry
    {
        public Entry(string key, BitmapImage image)
        {
            Key = key;
            Image = image;
        }

        public string Key { get; }
        public BitmapImage Image { get; }
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

        BitmapImage image;
        try
        {
            image = new BitmapImage
            {
                UriSource = new Uri(url),
                // 大图用途（如详情页背景）按接近原始尺寸解码，避免放大后糊成一片
                DecodePixelWidth = decodeWidth ?? DecodeWidth,
                DecodePixelType = DecodePixelType.Logical,
            };
        }
        catch
        {
            // 非 UI 线程或 URL 非法：退回 null，卡片显示首字母占位，不让整页崩掉
            return null;
        }

        if (!string.IsNullOrEmpty(fallbackUrl))
        {
            AttachFallback(image, fallbackUrl!);
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
    /// 主地址加载失败时切到备用地址（TMDB 图片在国内直连常被拦，改用镜像兜底）。
    /// 只切一次——已指向备用地址时不再处理，避免失败后无限重试。
    /// </summary>
    private static void AttachFallback(BitmapImage image, string fallbackUrl)
    {
        image.ImageFailed += (_, _) =>
        {
            try
            {
                if (!string.Equals(image.UriSource?.OriginalString, fallbackUrl, StringComparison.OrdinalIgnoreCase))
                {
                    image.UriSource = new Uri(fallbackUrl);
                }
            }
            catch
            {
                // 兜底也失败：保留首字母占位，不影响其它卡片
            }
        };
    }

    /// <summary>清空缓存（切换服务器 / 重新登录时调用，避免串图）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Lru.Clear();
            Index.Clear();
        }
    }
}
