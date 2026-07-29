using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FongMi.TV.UI.Controls;

/// <summary>海报卡片（契约 §5.1）：宽 150、图 2:3 圆角 8、底部渐变遮罩标题、右上角备注徽标；
/// Hover 1.05 缩放 + 阴影（Composition 隐式动画），点击触发 Click 路由事件。</summary>
public sealed partial class PosterCard : UserControl
{
    public static readonly DependencyProperty PicProperty = DependencyProperty.Register(
        nameof(Pic), typeof(string), typeof(PosterCard),
        new PropertyMetadata(null, (d, e) => ((PosterCard)d).Poster.Source = e.NewValue as string));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PosterCard),
        new PropertyMetadata("", (d, e) => ((PosterCard)d).TitleText.Text = e.NewValue as string ?? ""));

    public static readonly DependencyProperty RemarkProperty = DependencyProperty.Register(
        nameof(Remark), typeof(string), typeof(PosterCard),
        new PropertyMetadata("", (d, e) => ((PosterCard)d).SetRemark(e.NewValue as string)));

    /// <summary>卡片点击（Tapped 触发，不拦截冒泡，可与 ListView/GridView 的 ItemClick 并存）。</summary>
    public event RoutedEventHandler Click;

    public PosterCard()
    {
        InitializeComponent();
        Root.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) };
        Root.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) };
        Root.Shadow = new ThemeShadow();
    }

    public string Pic { get => (string)GetValue(PicProperty); set => SetValue(PicProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Remark { get => (string)GetValue(RemarkProperty); set => SetValue(RemarkProperty, value); }

    void SetRemark(string value)
    {
        RemarkText.Text = value ?? "";
        RemarkBadge.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>WinUI3 的 RectangleGeometry 无 RadiusX/RadiusY，改用 Composition 圆角矩形裁剪保持圆角海报效果。</summary>
    void OnCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(Card);
        var geo = visual.Compositor.CreateRoundedRectangleGeometry();
        geo.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
        geo.CornerRadius = new Vector2(8, 8);
        visual.Clip = visual.Compositor.CreateGeometricClip(geo);
    }

    void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        Root.CenterPoint = new Vector3((float)(Root.ActualWidth / 2), (float)(Root.ActualHeight / 2), 0);
        Root.Scale = new Vector3(1.05f, 1.05f, 1f);
        Root.Translation = new Vector3(0, 0, 16);
    }

    void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        Root.Scale = Vector3.One;
        Root.Translation = Vector3.Zero;
    }

    void OnTapped(object sender, TappedRoutedEventArgs e) => Click?.Invoke(this, e);
}
