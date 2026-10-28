using System.Text;
using System.Text.RegularExpressions;

namespace EmbyPlayer.Services;

/// <summary>一个可自定义的播放器快捷键：在 mpv 里对应 input.conf 的一条绑定。</summary>
public sealed class MpvShortcut
{
    public required string Id { get; init; }
    public required string Label { get; init; }

    /// <summary>默认按键，多个用 <see cref="MpvShortcuts.Separator"/> 分隔（如 "ArrowLeft;A"）。</summary>
    public required string DefaultKeys { get; init; }

    /// <summary>按下按键时执行的 mpv 命令。</summary>
    public required string Command { get; init; }
}

/// <summary>
/// 播放器快捷键表：把设置页里的按键翻译成 mpv 的 input.conf 文件，
/// 启动 mpv 时通过 --input-conf 加载。只覆盖列出来的这些键，其余保持 mpv 默认。
/// </summary>
public static class MpvShortcuts
{
    /// <summary>设置文件里多个按键之间的分隔符（显示时替换成 " / "）。</summary>
    public const char Separator = ';';

    /// <summary>按键列表（顺序即设置页里的显示顺序）。</summary>
    public static IReadOnlyList<MpvShortcut> All { get; } = new List<MpvShortcut>
    {
        new() { Id = "pause",        Label = "播放 / 暂停",     DefaultKeys = "Space",           Command = "cycle pause" },
        new() { Id = "seekBack",     Label = "快退 5 秒",       DefaultKeys = "ArrowLeft;A",     Command = "seek -5" },
        new() { Id = "seekForward",  Label = "快进 5 秒",       DefaultKeys = "ArrowRight;D",    Command = "seek 5" },
        new() { Id = "volumeUp",     Label = "音量增加",        DefaultKeys = "ArrowUp",         Command = "add volume 5" },
        new() { Id = "volumeDown",   Label = "音量减小",        DefaultKeys = "ArrowDown",       Command = "add volume -5" },
        new() { Id = "mute",         Label = "静音 / 恢复音量", DefaultKeys = "M",               Command = "cycle mute" },
        new() { Id = "fullscreen",   Label = "切换全屏",        DefaultKeys = "F",               Command = "cycle fullscreen" },
        new() { Id = "chapterPrev",  Label = "上一章节",        DefaultKeys = "PageUp",          Command = "add chapter -1" },
        new() { Id = "chapterNext",  Label = "下一章节",        DefaultKeys = "PageDown",        Command = "add chapter 1" },
        new() { Id = "nextEpisode",  Label = "下一集",          DefaultKeys = "N",               Command = "playlist-next" },
        new() { Id = "subtitle",     Label = "切换字幕",        DefaultKeys = "S",               Command = "cycle sub" },
        new() { Id = "audio",        Label = "切换音轨",        DefaultKeys = "L",               Command = "cycle audio" },
        // 弹幕默认不占键位（右键菜单里有「弹幕 → 开启 / 关闭」），留给用户自己指定
        new() { Id = "danmaku",      Label = "弹幕开关",        DefaultKeys = "",                Command = "script-message toggle-danmaku" }
    };

    public static MpvShortcut? Find(string id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>设置文件里的按键串 → 按键列表。空串表示用户主动取消了绑定。</summary>
    public static List<string> SplitKeys(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<string>();
        }
        return raw
            .Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string JoinKeys(IEnumerable<string> keys) => string.Join(Separator, keys);

    /// <summary>设置页里显示的按键文本（多键用 " / " 分隔）。</summary>
    public static string DisplayText(string? tokens)
    {
        var keys = SplitKeys(tokens);
        return keys.Count == 0 ? "未绑定" : string.Join(" / ", keys);
    }

    /// <summary>某个功能当前生效的按键：设置里写过就用设置，否则用默认。</summary>
    public static List<string> ResolveTokens(AppSettings settings, MpvShortcut shortcut)
    {
        if (settings.MpvKeyBindings is { } map && map.TryGetValue(shortcut.Id, out var raw))
        {
            return SplitKeys(raw);
        }
        return SplitKeys(shortcut.DefaultKeys);
    }

    /// <summary>生成 mpv 的 input.conf 内容。</summary>
    public static string BuildInputConf(AppSettings settings)
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();

        foreach (var shortcut in All)
        {
            foreach (var key in ResolveTokens(settings, shortcut))
            {
                var mpvKey = ToMpvKey(key);
                // 同一个键只认第一条绑定，避免多行冲突时行为取决于先后顺序
                if (mpvKey is null || !claimed.Add(mpvKey))
                {
                    continue;
                }
                lines.Add($"{mpvKey} {shortcut.Command}");
            }
        }

        // 用户主动取消绑定的功能：用它原来的默认键写入 ignore，否则 mpv 内置默认还会响应
        foreach (var shortcut in All)
        {
            if (settings.MpvKeyBindings is not { } map || !map.TryGetValue(shortcut.Id, out var raw))
            {
                continue; // 没自定义过，mpv 默认绑定照常生效
            }
            if (SplitKeys(raw).Count > 0)
            {
                continue; // 有自定义键，上面已经写过了
            }
            foreach (var key in SplitKeys(shortcut.DefaultKeys))
            {
                var mpvKey = ToMpvKey(key);
                if (mpvKey is null || !claimed.Add(mpvKey))
                {
                    continue;
                }
                lines.Add($"{mpvKey} ignore");
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("# EmbyPlayer 播放器快捷键，由设置页自动生成，手改会被覆盖。");
        // 右键呼出 menu.conf 里的中文菜单（弹幕开关也在那个菜单里）
        sb.AppendLine("MBTN_RIGHT script-message-to context_menu open");
        foreach (var line in lines)
        {
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    /// <summary>把 input.conf 写到本机并返回路径（每次启动播放前重写，保证与设置一致）。</summary>
    public static string EnsureInputConfFile(AppSettings settings)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmbyPlayer");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "mpv-input.conf");
        var content = BuildInputConf(settings);
        // 内容没变就不动文件，少一次磁盘写入
        if (!File.Exists(path) || File.ReadAllText(path) != content)
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        return path;
    }

    private static readonly Dictionary<string, string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = "SPACE",
        ["ArrowLeft"] = "LEFT",
        ["ArrowRight"] = "RIGHT",
        ["ArrowUp"] = "UP",
        ["ArrowDown"] = "DOWN",
        ["PageUp"] = "PGUP",
        ["PageDown"] = "PGDWN",
        ["Home"] = "HOME",
        ["End"] = "END",
        ["Insert"] = "INS",
        ["Delete"] = "DEL",
        ["Backspace"] = "BACKSPACE",
        ["Tab"] = "TAB",
        ["Enter"] = "ENTER",
        ["Esc"] = "ESC",
        ["Escape"] = "ESC",
        ["Num0"] = "KP0",
        ["Num1"] = "KP1",
        ["Num2"] = "KP2",
        ["Num3"] = "KP3",
        ["Num4"] = "KP4",
        ["Num5"] = "KP5",
        ["Num6"] = "KP6",
        ["Num7"] = "KP7",
        ["Num8"] = "KP8",
        ["Num9"] = "KP9",
        ["Num+"] = "KP_ADD",
        ["Num-"] = "KP_SUB",
        ["Num*"] = "KP_MULT",
        ["Num/"] = "KP_DIV",
        ["Num."] = "KP_DEC",
        ["-"] = "-",
        ["="] = "=",
        ["["] = "[",
        ["]"] = "]",
        ["/"] = "/",
        ["\\"] = "\\",
        ["."] = ".",
        ["`"] = "`",
        ["'"] = "'"
    };

    private static readonly Regex FunctionKey = new(@"^F([1-9]|1[0-9]|2[0-4])$", RegexOptions.IgnoreCase);

    /// <summary>
    /// 设置页里的按键名 → mpv 键名（"ArrowLeft" → "LEFT"，"Ctrl+S" → "CTRL+s"）。
    /// 无法翻译的返回 null，调用方跳过即可。
    /// </summary>
    public static string? ToMpvKey(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
        {
            return null;
        }

        var rest = display.Trim();
        var modifiers = new List<string>();
        while (true)
        {
            var cut = rest.IndexOf('+');
            if (cut <= 0)
            {
                break;
            }
            var head = rest[..cut];
            if (head is "Ctrl" or "Alt" or "Shift" or "Win")
            {
                modifiers.Add(head.ToUpperInvariant());
                rest = rest[(cut + 1)..];
            }
            else
            {
                break;
            }
        }

        string? baseName = null;
        if (NamedKeys.TryGetValue(rest, out var named))
        {
            baseName = named;
        }
        else if (rest.Length == 1 && (char.IsLetterOrDigit(rest[0]) || "-=[]/\\.`'".Contains(rest[0])))
        {
            baseName = char.ToLowerInvariant(rest[0]).ToString();
        }
        else if (FunctionKey.IsMatch(rest))
        {
            baseName = rest.ToUpperInvariant();
        }

        if (baseName is null)
        {
            return null;
        }

        return modifiers.Count == 0 ? baseName : string.Join("+", modifiers) + "+" + baseName;
    }
}
