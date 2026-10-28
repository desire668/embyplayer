using CommunityToolkit.Mvvm.ComponentModel;
using EmbyPlayer.Services;

namespace EmbyPlayer.ViewModels;

/// <summary>
/// 设置页「快捷键」里的一行：左边功能名，右边是当前按键。
/// 只在 UI 层做展示与录制，真正的保存由 <see cref="SettingsViewModel"/> 负责。
/// </summary>
public partial class ShortcutRow : ObservableObject
{
    public ShortcutRow(MpvShortcut definition, string tokens)
    {
        Id = definition.Id;
        Label = definition.Label;
        _tokens = tokens;
    }

    public string Id { get; }

    public string Label { get; }

    /// <summary>按键（多个用 ';' 分隔），空串表示未绑定。</summary>
    [ObservableProperty]
    private string _tokens = "";

    /// <summary>正在等用户按键：按钮显示提示语并高亮。</summary>
    [ObservableProperty]
    private bool _isCapturing;

    /// <summary>按钮上显示的文字。</summary>
    public string DisplayText => IsCapturing ? "按下新按键…" : MpvShortcuts.DisplayText(Tokens);

    partial void OnTokensChanged(string value) => OnPropertyChanged(nameof(DisplayText));

    partial void OnIsCapturingChanged(bool value) => OnPropertyChanged(nameof(DisplayText));
}
