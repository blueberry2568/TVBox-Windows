using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FongMi.TV.UI.Controls;

/// <summary>海报图控件（契约 §5.4）：字符串 Source 依赖属性（支持 @Referer=/@User-Agent= 后缀），
/// 异步经 ImageLoader 加载；加载中/失败显示灰色占位块 + 🎬 字形；Stretch=UniformToFill。</summary>
public class PosterImage : Grid
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(PosterImage),
        new PropertyMetadata(null, (d, e) => _ = ((PosterImage)d).LoadAsync(e.NewValue as string)));

    readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    readonly Border _placeholder;
    int _version;

    public PosterImage()
    {
        _placeholder = new Border
        {
            Background = FindBrush("ControlFillColorSecondaryBrush"),
            Child = new TextBlock
            {
                Text = "🎬",
                FontSize = 28,
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Children.Add(_placeholder);
        Children.Add(_image);
    }

    public string Source
    {
        get => (string)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    static Brush FindBrush(string key)
    {
        try { if (Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b) return b; } catch { }
        return new SolidColorBrush(Colors.Gray);
    }

    async Task LoadAsync(string pic)
    {
        var version = ++_version;
        _image.Source = null;
        _placeholder.Visibility = Visibility.Visible;
        if (string.IsNullOrWhiteSpace(pic)) return;
        var bmp = await ImageLoader.Load(pic);
        if (version != _version || bmp == null) return; // 已被更新的 Source 覆盖或加载失败
        _image.Source = bmp;
        _placeholder.Visibility = Visibility.Collapsed;
    }
}
