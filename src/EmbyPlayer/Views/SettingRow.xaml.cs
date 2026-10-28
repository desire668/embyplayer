using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyPlayer.Views;

/// <summary>
/// 设置页的一行：左边标题（可选说明），右边放控件。
/// 两种排布：默认「标题在左、控件在右」；<see cref="Stacked"/>=true 时控件换到标题下方整行铺开，
/// 适合路径、Key 这类需要长输入的项。
/// </summary>
public sealed partial class SettingRow : UserControl
{
    public SettingRow()
    {
        InitializeComponent();
        ApplyLayout();
    }

    /// <summary>设置项标题。</summary>
    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingRow),
            new PropertyMetadata(""));

    /// <summary>标题下方的一行灰色说明；为空时不占位。</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow),
            new PropertyMetadata("", OnDescriptionChanged));

    /// <summary>右侧（或下方）的控件。</summary>
    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public static readonly DependencyProperty BodyProperty =
        DependencyProperty.Register(nameof(Body), typeof(object), typeof(SettingRow),
            new PropertyMetadata(null, OnBodyChanged));

    /// <summary>控件是否换到标题下方整行显示。</summary>
    public bool Stacked
    {
        get => (bool)GetValue(StackedProperty);
        set => SetValue(StackedProperty, value);
    }

    public static readonly DependencyProperty StackedProperty =
        DependencyProperty.Register(nameof(Stacked), typeof(bool), typeof(SettingRow),
            new PropertyMetadata(false, OnLayoutChanged));

    /// <summary>右侧控件的固定宽度（默认 240）；Stacked 时忽略。</summary>
    public double ControlWidth
    {
        get => (double)GetValue(ControlWidthProperty);
        set => SetValue(ControlWidthProperty, value);
    }

    public static readonly DependencyProperty ControlWidthProperty =
        DependencyProperty.Register(nameof(ControlWidth), typeof(double), typeof(SettingRow),
            new PropertyMetadata(240d, OnLayoutChanged));

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SettingRow)d).ApplyDescription();

    private static void OnBodyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SettingRow)d).ApplyLayout();

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SettingRow)d).ApplyLayout();

    private void ApplyDescription()
    {
        if (DescBlock is null)
        {
            return;
        }
        DescBlock.Visibility = string.IsNullOrWhiteSpace(Description)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ApplyLayout()
    {
        if (Layout is null || BodyHost is null || HeaderHost is null)
        {
            return;
        }

        if (Stacked)
        {
            // 控件另起一行，整行铺开；标题也不再垂直居中
            Grid.SetRow(BodyHost, 1);
            Grid.SetColumn(BodyHost, 0);
            Grid.SetColumnSpan(BodyHost, 2);
            BodyHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            BodyHost.Width = double.NaN;
            BodyHost.Margin = new Thickness(0, 10, 0, 0);
            HeaderHost.VerticalAlignment = VerticalAlignment.Top;
        }
        else
        {
            Grid.SetRow(BodyHost, 0);
            Grid.SetColumn(BodyHost, 1);
            Grid.SetColumnSpan(BodyHost, 1);
            BodyHost.HorizontalAlignment = HorizontalAlignment.Right;
            BodyHost.Margin = new Thickness(0);
            HeaderHost.VerticalAlignment = VerticalAlignment.Center;

            var width = ControlWidth;
            BodyHost.Width = width > 0 ? width : double.NaN;
        }
    }
}
