using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EmbyPlayer.Services;

/// <summary>单条弹幕数据（解析自 bilibili XML）。</summary>
public sealed class DanmakuEntry
{
    public double Time { get; set; }  // 秒
    public int Mode { get; set; }     // 1-3 滚动, 4 底部, 5 顶部
    public int FontSize { get; set; }
    public int Color { get; set; }    // 十进制 RGB
    public string Text { get; set; } = "";
}

/// <summary>ASS 生成参数，由设置页注入。</summary>
public sealed class DanmakuAssOptions
{
    public int PlayResX { get; set; } = 1920;
    public int PlayResY { get; set; } = 1080;
    public int FontSize { get; set; } = 40;
    public double Opacity { get; set; } = 0.85;
    public int ScrollDuration { get; set; } = 8;  // 滚动弹幕横穿屏幕秒数
    public int FixedDuration { get; set; } = 5;    // 顶部/底部停留秒数
    public double DisplayArea { get; set; } = 0.5; // 弹幕占屏比例（0-1）
}

/// <summary>
/// 弹幕服务：解析 bilibili 格式 XML 弹幕，转换为 mpv 可渲染的 ASS 字幕文件。
/// 支持本地文件与 HTTP URL（返回相同 XML）两种来源。
/// </summary>
public sealed class DanmakuService
{
    private static readonly Regex XmlTagPattern = new(@"<d\s+[^>]*p=""([^""]+)""[^>]*>(.*?)</d>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>从本地 XML 文件解析弹幕。</summary>
    public List<DanmakuEntry> ParseFile(string xmlPath)
    {
        if (!File.Exists(xmlPath))
        {
            return new List<DanmakuEntry>();
        }
        return ParseXml(File.ReadAllText(xmlPath));
    }

    /// <summary>从 HTTP/HTTPS URL 下载并解析弹幕 XML。</summary>
    public async Task<List<DanmakuEntry>> ParseUrlAsync(string url, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var content = await http.GetStringAsync(url, ct);
        return ParseXml(content);
    }

    // ---- LogVar 弹幕 API（huangxd-/danmu_api）----
    // 流程：search/anime -> bangumi/:animeId -> comment/:episodeId?format=xml
    // 返回的 XML 即 bilibili 标准格式，复用 ParseXml 解析。

    private sealed class LogVarSearchResult
    {
        public List<LogVarAnime> Animes { get; set; } = new();
    }

    private sealed class LogVarAnime
    {
        public long AnimeId { get; set; }
        public string? AnimeTitle { get; set; }
        public string? Type { get; set; }
        public int EpisodeCount { get; set; }
    }

    private sealed class LogVarBangumiResult
    {
        public LogVarBangumi? Bangumi { get; set; }
    }

    private sealed class LogVarBangumi
    {
        public List<LogVarEpisode> Episodes { get; set; } = new();
    }

    private sealed class LogVarEpisode
    {
        public long EpisodeId { get; set; }
        public string? EpisodeNumber { get; set; }
        public string? EpisodeTitle { get; set; }
    }

    private static readonly JsonSerializerOptions LogVarJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 通过 LogVar 弹幕 API 按标题（及可选季/集）获取弹幕。
    /// baseUrl 形如 https://host/token（可不含 /api/v2），会自动拼接接口路径。
    /// </summary>
    public async Task<List<DanmakuEntry>> FetchFromLogVarAsync(
        string baseUrl, string title, int? season = null, int? episode = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(title))
        {
            return new List<DanmakuEntry>();
        }

        var root = baseUrl.TrimEnd('/');
        // 容错：用户可能已带上 /api 或 /api/v2
        if (root.EndsWith("/api/v2", StringComparison.OrdinalIgnoreCase))
        {
            root = root[..^"/api/v2".Length];
        }
        else if (root.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            root = root[..^"/api".Length];
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // 1) 搜索动漫
        var wantEpisode = episode is > 0 || season is > 0;
        var searchUrl = $"{root}/api/v2/search/anime?keyword={Uri.EscapeDataString(title)}";
        var search = await GetJsonAsync<LogVarSearchResult>(http, searchUrl, ct);
        var anime = PickAnime(search?.Animes, wantEpisode);
        if (anime is null)
        {
            return new List<DanmakuEntry>();
        }

        // 2) 获取剧集列表，选出对应集的 episodeId 作为 commentId
        var bangumiUrl = $"{root}/api/v2/bangumi/{anime.AnimeId}";
        var bangumi = await GetJsonAsync<LogVarBangumiResult>(http, bangumiUrl, ct);
        var episodes = bangumi?.Bangumi?.Episodes;
        if (episodes is null || episodes.Count == 0)
        {
            return new List<DanmakuEntry>();
        }
        var target = PickEpisode(episodes, episode);

        // 3) 拉取弹幕 XML
        var commentUrl = $"{root}/api/v2/comment/{target.EpisodeId}?format=xml";
        var xml = await http.GetStringAsync(commentUrl, ct);
        return ParseXml(xml);
    }

    private static async Task<T?> GetJsonAsync<T>(HttpClient http, string url, CancellationToken ct)
    {
        var json = await http.GetStringAsync(url, ct);
        return JsonSerializer.Deserialize<T>(json, LogVarJsonOptions);
    }

    /// <summary>从搜索结果中挑选最合适的动漫：优先类型匹配（电影 vs 剧集），否则取第一个。</summary>
    private static LogVarAnime? PickAnime(List<LogVarAnime>? animes, bool wantEpisode)
    {
        if (animes is null || animes.Count == 0)
        {
            return null;
        }
        if (wantEpisode)
        {
            var seriesLike = animes.FirstOrDefault(a =>
                a.Type is not null && a.Type != "电影");
            if (seriesLike is not null)
            {
                return seriesLike;
            }
        }
        else
        {
            var movie = animes.FirstOrDefault(a => a.Type == "电影");
            if (movie is not null)
            {
                return movie;
            }
        }
        return animes[0];
    }

    /// <summary>按集号选择剧集；找不到匹配集号时回退到第一集。</summary>
    private static LogVarEpisode PickEpisode(List<LogVarEpisode> episodes, int? episode)
    {
        if (episode is int n)
        {
            var match = episodes.FirstOrDefault(e =>
                int.TryParse(e.EpisodeNumber, out var en) && en == n);
            if (match is not null)
            {
                return match;
            }
        }
        return episodes[0];
    }

    /// <summary>解析 bilibili XML 弹幕文本。</summary>
    public List<DanmakuEntry> ParseXml(string xmlContent)
    {
        var result = new List<DanmakuEntry>();
        // 用正则提取所有 &lt;d p="time,mode,size,color,ts,pool,user,rowid"&gt;内容&lt;/d&gt;
        foreach (Match m in XmlTagPattern.Matches(xmlContent))
        {
            var props = m.Groups[1].Value.Split(',');
            var text = m.Groups[2].Value;
            if (props.Length < 4 || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            if (!double.TryParse(props[0], out var time))
            {
                continue;
            }
            int mode = 1, fontSize = 25, color = 0xFFFFFF;
            int.TryParse(props.ElementAtOrDefault(1), out mode);
            int.TryParse(props.ElementAtOrDefault(2), out fontSize);
            int.TryParse(props.ElementAtOrDefault(3), out color);
            // 仅保留常见模式：1-3 滚动、4 底部、5 顶部；忽略 6+ 特殊弹幕
            if (mode is < 1 or > 5)
            {
                continue;
            }
            result.Add(new DanmakuEntry
            {
                Time = time,
                Mode = mode,
                FontSize = fontSize,
                Color = color,
                Text = UnescapeXml(text)
            });
        }
        // 按时间排序，方便后续轨道分配
        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    /// <summary>将弹幕列表转换为 ASS 字幕文件，返回生成的临时文件路径。</summary>
    public string ConvertToAssFile(List<DanmakuEntry> danmakus, DanmakuAssOptions options)
    {
        var ass = ConvertToAss(danmakus, options);
        var dir = CacheService.DirectoryOf(CacheKind.Danmaku);
        var path = Path.Combine(dir, $"danmaku_{Guid.NewGuid():N}.ass");
        // ASS 必须用 UTF-8 BOM，否则 mpv 可能识别乱码
        File.WriteAllText(path, ass, new UTF8Encoding(true));
        return path;
    }

    /// <summary>生成 ASS 字幕文本。</summary>
    public string ConvertToAss(List<DanmakuEntry> danmakus, DanmakuAssOptions options)
    {
        var alpha = (int)Math.Round((1 - options.Opacity) * 255);
        var alphaHex = $"{alpha:X2}";
        var font = "Microsoft YaHei";

        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("Title: Danmaku");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine($"PlayResX: {options.PlayResX}");
        sb.AppendLine($"PlayResY: {options.PlayResY}");
        sb.AppendLine("Timer: 100.0000");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        // Alignment: 7=左上（滚动用，y=顶部偏移）、8=中上（顶部）、2=中下（底部）
        var defaultColor = $"&H{alphaHex}FFFFFF&";
        sb.AppendLine($"Style: Scroll,{font},{options.FontSize},{defaultColor},&H000000FF&,&H66000000&,&H66000000&,0,0,0,0,100,100,0,0,1,2,0,7,0,0,0,1");
        sb.AppendLine($"Style: Top,{font},{options.FontSize},{defaultColor},&H000000FF&,&H66000000&,&H66000000&,0,0,0,0,100,100,0,0,1,2,0,8,0,0,0,1");
        sb.AppendLine($"Style: Bottom,{font},{options.FontSize},{defaultColor},&H000000FF&,&H66000000&,&H66000000&,0,0,0,0,100,100,0,0,1,2,0,2,0,0,0,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        // 弹幕可用高度（DisplayArea 比例 × 屏幕高度），按字号分行
        var usableHeight = options.PlayResY * options.DisplayArea;
        var laneHeight = options.FontSize + 4;
        var laneCount = Math.Max(1, (int)(usableHeight / laneHeight));

        // 轨道占用追踪
        var scrollLanes = new double[laneCount];  // 每条轨道下次可用的时刻
        var topLanes = new double[laneCount];
        var bottomLanes = new double[laneCount];
        for (var i = 0; i < laneCount; i++)
        {
            scrollLanes[i] = -1;
            topLanes[i] = -1;
            bottomLanes[i] = -1;
        }

        foreach (var d in danmakus)
        {
            var start = d.Time;
            var colorOverride = ColorToAss(d.Color, alphaHex);
            var escaped = EscapeAssText(d.Text);
            if (string.IsNullOrEmpty(escaped))
            {
                continue;
            }

            if (d.Mode is >= 1 and <= 3)
            {
                // 滚动弹幕：右→左
                var end = start + options.ScrollDuration;
                var width = EstimateTextWidth(d.Text, options.FontSize);
                // 该弹幕右边缘进入屏幕需要的时间（用于判断轨道可用）
                var enterTime = width / ((options.PlayResX + width) / (double)options.ScrollDuration);
                var lane = FindLane(scrollLanes, start, enterTime);
                if (lane < 0)
                {
                    continue; // 无可用轨道，丢弃该弹幕（防拥堵）
                }
                scrollLanes[lane] = start + enterTime;
                var y = lane * laneHeight;
                sb.AppendLine($"Dialogue: 0,{FormatAssTime(start)},{FormatAssTime(end)},Scroll,,0,0,0,,{colorOverride}{{\\move({options.PlayResX},{y},{-(int)width},{y})}}{escaped}");
            }
            else if (d.Mode == 5)
            {
                // 顶部弹幕
                var end = start + options.FixedDuration;
                var lane = FindLane(topLanes, start, options.FixedDuration);
                if (lane < 0)
                {
                    continue;
                }
                topLanes[lane] = end;
                var y = lane * laneHeight;
                sb.AppendLine($"Dialogue: 0,{FormatAssTime(start)},{FormatAssTime(end)},Top,,0,0,0,,{colorOverride}{{\\an8\\pos({options.PlayResX / 2},{y})}}{escaped}");
            }
            else if (d.Mode == 4)
            {
                // 底部弹幕
                var end = start + options.FixedDuration;
                var lane = FindLane(bottomLanes, start, options.FixedDuration);
                if (lane < 0)
                {
                    continue;
                }
                bottomLanes[lane] = end;
                var y = options.PlayResY - (lane + 1) * laneHeight;
                sb.AppendLine($"Dialogue: 0,{FormatAssTime(start)},{FormatAssTime(end)},Bottom,,0,0,0,,{colorOverride}{{\\an2\\pos({options.PlayResX / 2},{y})}}{escaped}");
            }
        }

        return sb.ToString();
    }

    /// <summary>查找在 startTime 可用的轨道索引，无可用则返回 -1。</summary>
    private static int FindLane(double[] lanes, double startTime, double holdTime)
    {
        for (var i = 0; i < lanes.Length; i++)
        {
            if (lanes[i] <= startTime)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>估算文本像素宽度（CJK≈1字号，ASCII≈0.6字号）。</summary>
    private static double EstimateTextWidth(string text, int fontSize)
    {
        double width = 0;
        foreach (var c in text)
        {
            width += c > 0x2E80 ? fontSize : fontSize * 0.6;
        }
        return width;
    }

    /// <summary>RGB 十进制 → ASS 颜色覆盖标签（\1c&HBBGGRR&）。</summary>
    private static string ColorToAss(int rgb, string alphaHex)
    {
        // 白色不输出额外标签以省字符
        if (rgb == 0xFFFFFF)
        {
            return $"{{\\1a&H{alphaHex}&}}";
        }
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        return $"{{\\1c&H{b:X2}{g:X2}{r:X2}&\\1a&H{alphaHex}&}}";
    }

    /// <summary>转义 ASS 文本：移除 {}，换行 \n → \N。</summary>
    private static string EscapeAssText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '{' || c == '}')
            {
                sb.Append(' ');
            }
            else if (c == '\n')
            {
                sb.Append("\\N");
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>反转义 XML 实体。</summary>
    private static string UnescapeXml(string text)
    {
        return text
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'");
    }

    /// <summary>格式化 ASS 时间 H:MM:SS.cc。</summary>
    private static string FormatAssTime(double seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
    }
}
