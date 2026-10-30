using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EmbyPlayer.ViewModels;

/// <summary>「查看全部」的去向：跳到媒体库某个分类的网格视图，或去继续观看页。</summary>
public enum MediaSectionViewAll
{
    /// <summary>没有查看全部按钮。</summary>
    None,

    /// <summary>切到 TargetView 对应的网格视图。</summary>
    View,

    /// <summary>去「继续观看」页。</summary>
    Resume,
}

/// <summary>
/// 媒体库概览页里的一个横向分类区：标题 + 简短描述 + 卡片集合。
/// Items 是独立的 ObservableCollection，支持运行中增删；数据由各自异步任务填充，
/// 不同分类之间互不等待、互不影响。
/// </summary>
public partial class MediaSection : ObservableObject
{
    public MediaSection(string title, MediaSectionViewAll viewAll = MediaSectionViewAll.None,
        ViewInfo? targetView = null, string? description = null)
    {
        Title = title;
        ViewAll = viewAll;
        TargetView = targetView;
        Description = description;

        // 卡片动态增删后「查看全部」的显隐要跟着变（空分类不留按钮）
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ViewAllVisibility));
    }

    public string Title { get; }

    public string? Description { get; }

    /// <summary>查看全部按钮的行为与目标。</summary>
    public MediaSectionViewAll ViewAll { get; }

    /// <summary>ViewAll == View 时要切到的媒体库分类。</summary>
    public ViewInfo? TargetView { get; }

    /// <summary>该分类的卡片集合；数据异步回来后整体填入，运行中可自由增删。</summary>
    public ObservableCollection<ItemCard> Items { get; } = new();

    [ObservableProperty]
    private bool _isLoading = true;

    /// <summary>数据回来后按内容决定按钮是否显示：一张卡都没有就不留「查看全部」。</summary>
    public Microsoft.UI.Xaml.Visibility ViewAllVisibility =>
        ViewAll != MediaSectionViewAll.None && !IsLoading && Items.Count > 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility DescriptionVisibility =>
        string.IsNullOrEmpty(Description)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    partial void OnIsLoadingChanged(bool value) =>
        OnPropertyChanged(nameof(ViewAllVisibility));
}
