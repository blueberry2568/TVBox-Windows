using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;
using TVBoxForWindows.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;

namespace TVBoxForWindows.UI.Pages;

/// <summary>收藏页（契约 §5.2）：收藏网格（PosterCard，备注=站点名）；右键取消收藏；点击 → DetailPage。</summary>
public sealed partial class KeepPage : Page
{
    long _revision = -1;
    int _cid = -1;
    KeepItem _contextItem;

    public KeepPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RefreshIfChanged();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        HideContextMenu();
        base.OnNavigatedFrom(e);
    }

    public void RefreshIfChanged()
    {
        var cid = VodConfigService.Cid;
        var revision = Stores.KeepRevision;
        if (_cid == cid && _revision == revision) return;
        Reload();
        _cid = cid;
        _revision = revision;
    }

    /// <summary>重新读取当前配置（cid）下的收藏（Key 格式 siteKey@vodId）。</summary>
    void Reload()
    {
        var items = Stores.GetKeeps(VodConfigService.Cid)
            .Select(k => new KeepItem
            {
                Pic = k.VodPic,
                Title = k.VodName,
                Remark = k.SiteName,
                SiteKey = k.SiteKey,
                VodId = k.VodId,
                Key = k.Key,
            })
            .ToList();
        KeepGrid.ItemsSource = items;
        CountText.Text = items.Count > 0 ? $"共 {items.Count} 条" : "";
        EmptyText.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is KeepItem item)
            Frame.Navigate(typeof(DetailPage), new DetailArgs { SiteKey = item.SiteKey, VodId = item.VodId, Name = item.Title });
    }

    void OnCardPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement target || target.DataContext is not KeepItem item) return;
        if (!e.GetCurrentPoint(target).Properties.IsRightButtonPressed) return;
        _contextItem = item;
        ContextLayer.Visibility = Visibility.Visible;
        ContextLayer.UpdateLayout();
        PositionContextMenu(e.GetCurrentPoint(ContextLayer).Position);
        e.Handled = true;
    }

    void PositionContextMenu(Point point)
    {
        ContextMenuPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Max(152, ContextMenuPanel.DesiredSize.Width);
        var height = Math.Max(48, ContextMenuPanel.DesiredSize.Height);
        Canvas.SetLeft(ContextMenuPanel, Math.Clamp(point.X, 8, Math.Max(8, ContextLayer.ActualWidth - width - 8)));
        Canvas.SetTop(ContextMenuPanel, Math.Clamp(point.Y, 8, Math.Max(8, ContextLayer.ActualHeight - height - 8)));
    }

    void OnContextLayerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ContextLayer)) return;
        HideContextMenu();
        e.Handled = true;
    }

    void HideContextMenu()
    {
        ContextLayer.Visibility = Visibility.Collapsed;
        _contextItem = null;
    }

    void OnRemove(object sender, RoutedEventArgs e)
    {
        var item = _contextItem;
        HideContextMenu();
        if (item == null) return;
        Stores.DeleteKeep(VodConfigService.Cid, item.Key);
        RefreshIfChanged();
    }
}

/// <summary>收藏网格视图项（DataTemplate Binding 用，公共属性 getter）。</summary>
public class KeepItem
{
    public string Pic { get; set; }
    public string Title { get; set; }
    public string Remark { get; set; }
    public string SiteKey { get; set; }
    public string VodId { get; set; }
    public string Key { get; set; }
}
