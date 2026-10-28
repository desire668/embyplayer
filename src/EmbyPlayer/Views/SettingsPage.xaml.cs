using System.Collections.ObjectModel;
using System.ComponentModel;
using EmbyPlayer.Services;
using EmbyPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace EmbyPlayer.Views;

/// <summary>设置页左侧的一个分类：图标 + 名称 + 关键词（搜索用）。</summary>
public sealed class SettingsCategory
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Glyph { get; set; } = "";
    public string Keywords { get; set; } = "";
    public SolidColorBrush IconBrush { get; set; } = new(Colors.Gray);

    /// <summary>无障碍 / UIA 里显示的名字（默认会显示成类型全名）。</summary>
    public override string ToString() => Name;
}

/// <summary>
/// 设置页：左侧分类导航 + 右侧分组卡片。
/// 只做「分类切换 / 搜索过滤」，每个分类的表单都在 XAML 里静态声明，切换只是显隐。
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    /// <summary>左侧列表现有分类（搜索时会替换成过滤后的子集）。</summary>
    public ObservableCollection<SettingsCategory> Categories { get; } = new();

    private readonly List<SettingsCategory> _allCategories = new();

    /// <summary>重建列表会触发 SelectionChanged，用这个标记跳过重复显隐。</summary>
    private bool _suppressSelection;

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        BuildCategories();
        InitializeComponent();
        BuildShortcutRows();
        // 录制按键：预览阶段就接管，避免方向键 / PageUp 被 ScrollViewer 抢去滚动
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnShortcutKeyDown), true);
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnShortcutElsewherePressed), true);
        Loaded += OnLoaded;
    }

    private void BuildCategories()
    {
        Add("general", "通用", "\uE713", "#FF1F6FEB", "首页 内容源 播放 轮播 动效");
        Add("appearance", "外观", "\uE790", "#FFB455C8", "主题 颜色 浅色 深色 动效");
        Add("player", "播放器", "\uE768", "#FFE0524B", "mpv 硬件解码 缓存 音量 音轨 参数");
        Add("shortcuts", "快捷键", "\uE765", "#FF5B8DEF", "快捷键 键位 按键 mpv 暂停 快进 音量 全屏 字幕 音轨 弹幕");
        Add("subtitle", "字幕与弹幕", "\uE8D2", "#FFD99A2B", "弹幕 字幕 字号 描边 服务器");
        Add("external", "外部服务", "\uE71B", "#FF0F9D58", "外部 服务 账号 连接 api key 授权 接口 镜像 omdb trakt 验证 断开");
        Add("cache", "缓存管理", "\uE8B7", "#FF3FA84B", "缓存 清理 上限 元数据 图片 弹幕 播放 目录");
        Add("about", "关于", "\uE946", "#FF6B7280", "版本 更新 github 仓库");

        foreach (var category in _allCategories)
        {
            Categories.Add(category);
        }
    }

    private void Add(string key, string name, string glyph, string color, string keywords) =>
        _allCategories.Add(new SettingsCategory
        {
            Key = key,
            Name = name,
            Glyph = glyph,
            Keywords = keywords,
            IconBrush = new SolidColorBrush(ParseColor(color))
        });

    private static Color ParseColor(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Color.FromArgb(
            (byte)((value >> 24) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (NavList.SelectedIndex < 0 && Categories.Count > 0)
        {
            NavList.SelectedIndex = 0;
        }
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || NavList.SelectedItem is not SettingsCategory category)
        {
            return;
        }
        ShowSection(category.Key);
    }

    /// <summary>切换右侧内容：只把当前分类的 StackPanel 显示出来，其余折叠。</summary>
    private void ShowSection(string key)
    {
        SecGeneral.Visibility = key == "general" ? Visibility.Visible : Visibility.Collapsed;
        SecAppearance.Visibility = key == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        SecPlayer.Visibility = key == "player" ? Visibility.Visible : Visibility.Collapsed;
        SecShortcuts.Visibility = key == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        SecSubtitle.Visibility = key == "subtitle" ? Visibility.Visible : Visibility.Collapsed;
        SecExternal.Visibility = key == "external" ? Visibility.Visible : Visibility.Collapsed;
        SecCache.Visibility = key == "cache" ? Visibility.Visible : Visibility.Collapsed;
        SecAbout.Visibility = key == "about" ? Visibility.Visible : Visibility.Collapsed;

        ContentScroll.ChangeView(null, 0, null, true);
    }

    /// <summary>搜索：按名称与关键词过滤左侧分类，尽量保留原选中项。</summary>
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        var query = sender.Text.Trim();
        var currentKey = (NavList.SelectedItem as SettingsCategory)?.Key;

        var matched = string.IsNullOrEmpty(query)
            ? _allCategories
            : _allCategories
                .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            c.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        _suppressSelection = true;
        Categories.Clear();
        foreach (var category in matched)
        {
            Categories.Add(category);
        }
        _suppressSelection = false;

        if (matched.Count == 0)
        {
            return;
        }

        var index = matched.FindIndex(c => c.Key == currentKey);
        NavList.SelectedIndex = index >= 0 ? index : 0;
        ShowSection(matched[NavList.SelectedIndex].Key);
    }

    // ==================== 快捷键 ====================

    /// <summary>正在录制按键的那一行（null 表示没在录制）。</summary>
    private ShortcutRow? _captureRow;

    /// <summary>录制中的胶囊按钮，用来还原边框颜色。</summary>
    private Button? _captureButton;

    private static readonly SolidColorBrush CaptureStroke = new(Color.FromArgb(0xFF, 0x1F, 0x6F, 0xEB));

    /// <summary>按 ViewModel 里的快捷键列表生成右侧的「功能名 + 键位胶囊」。</summary>
    private void BuildShortcutRows()
    {
        ShortcutList.Children.Clear();
        foreach (var row in ViewModel.Shortcuts)
        {
            var pill = new Button
            {
                Style = (Style)Resources["ShortcutKeyButtonStyle"],
                Content = row.DisplayText,
                Tag = row
            };
            ToolTipService.SetToolTip(pill, "点击后按下新按键；Esc 取消，Delete 取消绑定");
            pill.Click += ShortcutPill_Click;

            var capturedRow = row;
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ShortcutRow.DisplayText))
                {
                    pill.Content = capturedRow.DisplayText;
                }
            };

            ShortcutList.Children.Add(new SettingRow
            {
                Header = row.Label,
                ControlWidth = 210,
                Body = pill
            });
        }
    }

    private void ShortcutPill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button pill || pill.Tag is not ShortcutRow row)
        {
            return;
        }
        if (ReferenceEquals(_captureRow, row))
        {
            return;
        }

        EndCapture();
        _captureRow = row;
        _captureButton = pill;
        row.IsCapturing = true;
        pill.BorderBrush = CaptureStroke;
        pill.Focus(FocusState.Programmatic);
        ViewModel.ShortcutStatus = $"正在录制「{row.Label}」：按下想用的键，Esc 取消。";
    }

    private void OnShortcutKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_captureRow is not { } row)
        {
            return;
        }
        // 拦截住，否则空格会再点一次按钮、方向键 / PageUp 会滚动页面
        e.Handled = true;

        if (e.Key == VirtualKey.Escape)
        {
            CancelCapture("已取消录制。");
            return;
        }

        if (e.Key is VirtualKey.Delete or VirtualKey.Back)
        {
            ViewModel.SetShortcut(row, Array.Empty<string>());
            EndCapture();
            return;
        }

        var key = MapKey(e.Key);
        if (key is null)
        {
            return; // Ctrl / Shift 这类修饰键单独按下不算，继续等真正的键
        }

        ViewModel.SetShortcut(row, new[] { ComposeWithModifiers(key) });
        EndCapture();
    }

    /// <summary>点别处就放弃录制（点同一个胶囊不算，避免刚点开就被自己取消）。</summary>
    private void OnShortcutElsewherePressed(object sender, PointerRoutedEventArgs e)
    {
        if (_captureRow is null || _captureButton is null)
        {
            return;
        }
        if (e.OriginalSource is DependencyObject source && IsInside(source, _captureButton))
        {
            return;
        }
        CancelCapture("已取消录制。");
    }

    private static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void CancelCapture(string status)
    {
        if (_captureRow is null)
        {
            return;
        }
        EndCapture();
        ViewModel.ShortcutStatus = status;
    }

    private void EndCapture()
    {
        if (_captureButton is not null)
        {
            _captureButton.ClearValue(Control.BorderBrushProperty);
        }
        if (_captureRow is not null)
        {
            _captureRow.IsCapturing = false;
        }
        _captureRow = null;
        _captureButton = null;
    }

    /// <summary>录制时按住 Ctrl / Alt / Shift 就记成组合键。</summary>
    private static string ComposeWithModifiers(string key)
    {
        var mods = new List<string>();
        if (IsKeyDown(VirtualKey.Control))
        {
            mods.Add("Ctrl");
        }
        if (IsKeyDown(VirtualKey.Menu))
        {
            mods.Add("Alt");
        }
        if (IsKeyDown(VirtualKey.Shift))
        {
            mods.Add("Shift");
        }
        return mods.Count == 0 ? key : string.Join("+", mods) + "+" + key;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Windows 虚拟键 → 设置页里显示的键名（与 mpv 键名一一对应，见 MpvShortcuts.ToMpvKey）。</summary>
    private static string? MapKey(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.Space: return "Space";
            case VirtualKey.Left: return "ArrowLeft";
            case VirtualKey.Right: return "ArrowRight";
            case VirtualKey.Up: return "ArrowUp";
            case VirtualKey.Down: return "ArrowDown";
            case VirtualKey.PageUp: return "PageUp";
            case VirtualKey.PageDown: return "PageDown";
            case VirtualKey.Home: return "Home";
            case VirtualKey.End: return "End";
            case VirtualKey.Insert: return "Insert";
            case VirtualKey.Tab: return "Tab";
            case VirtualKey.Enter: return "Enter";
            case VirtualKey.Add: return "Num+";
            case VirtualKey.Subtract: return "Num-";
            case VirtualKey.Multiply: return "Num*";
            case VirtualKey.Divide: return "Num/";
            case VirtualKey.Decimal: return "Num.";
        }

        // Windows.System.VirtualKey 里没有 OEM 标点键的枚举名，按虚拟键码判断
        switch ((int)key)
        {
            case 0xBD: return "-";   // OemMinus
            case 0xBB: return "=";   // OemPlus
            case 0xDB: return "[";   // OemOpenBrackets
            case 0xDD: return "]";   // OemCloseBrackets
            case 0xBF: return "/";   // OemQuestion
            case 0xDC: return "\\";  // OemPipe
            case 0xBE: return ".";   // OemPeriod
            case 0xDE: return "'";   // OemQuotes
            case 0xC0: return "`";   // Oem3
        }

        if (key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            return ((char)('A' + ((int)key - (int)VirtualKey.A))).ToString();
        }
        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
        {
            return ((char)('0' + ((int)key - (int)VirtualKey.Number0))).ToString();
        }
        if (key is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9)
        {
            return "Num" + ((int)key - (int)VirtualKey.NumberPad0);
        }
        if (key is >= VirtualKey.F1 and <= VirtualKey.F24)
        {
            return "F" + ((int)key - (int)VirtualKey.F1 + 1);
        }
        return null;
    }

    private async void BrowseMpv_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add(".exe");

        var navigation = App.Services.GetRequiredService<NavigationService>();
        var hwnd = (navigation.Shell as MainWindow)?.Hwnd ?? IntPtr.Zero;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ViewModel.MpvPath = file.Path;
        }
    }
}
