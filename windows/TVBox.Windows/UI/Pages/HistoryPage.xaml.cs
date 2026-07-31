using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;
using TVBoxForWindows.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;

namespace TVBoxForWindows.UI.Pages;

/// <summary>历史页（契约 §5.2）：网格 PosterCard + 底部进度条（Position/Duration）+ 集数备注徽标；
/// 右键菜单删除该条/清空全部（清空 ContentDialog 确认）；点击 → DetailPage。本页只读，保存由 PlayerPage 负责。</summary>
public sealed partial class HistoryPage : Page
{
    long _revision = -1;
    int _cid = -1;
    HistoryItem _contextItem;

    public HistoryPage() => InitializeComponent();

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
        var revision = Stores.HistoryRevision;
        if (_cid == cid && _revision == revision) return;
        Reload();
        _cid = cid;
        _revision = revision;
    }

    /// <summary>重新读取当前配置（cid）下的历史（Key 格式 siteKey@vodId）。</summary>
    void Reload()
    {
        var items = Stores.GetHistories(VodConfigService.Cid).Select(ToItem).ToList();
        HistoryGrid.ItemsSource = items;
        CountText.Text = items.Count > 0 ? $"共 {items.Count} 条" : "";
        ClearButton.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    static HistoryItem ToItem(History h)
    {
        var percent = h.Duration > 0 && h.Position > 0 ? Math.Clamp(h.Position * 100.0 / h.Duration, 0, 100) : 0;
        var site = VodConfigService.Instance.GetSite(h.SiteKey);
        var meta = string.Join(" · ", new[]
        {
            percent > 0 ? $"已看 {percent:0}%" : "",
            site.Name,
        }.Where(s => !string.IsNullOrEmpty(s)));
        return new HistoryItem
        {
            Pic = h.VodPic,
            Title = h.VodName,
            Remark = h.VodRemarks,
            SiteKey = h.SiteKey,
            VodId = h.VodId,
            Key = h.Key,
            Percent = percent,
            Meta = meta,
        };
    }

    void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryItem item)
            Frame.Navigate(typeof(DetailPage), new DetailArgs { SiteKey = item.SiteKey, VodId = item.VodId, Name = item.Title });
    }

    void OnCardPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement target || target.DataContext is not HistoryItem item) return;
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
        var height = Math.Max(88, ContextMenuPanel.DesiredSize.Height);
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

    void OnDeleteOne(object sender, RoutedEventArgs e)
    {
        var item = _contextItem;
        HideContextMenu();
        if (item == null) return;
        Stores.DeleteHistory(VodConfigService.Cid, item.Key);
        RefreshIfChanged();
    }

    /// <summary>清空全部（ContentDialog 确认）。</summary>
    async void OnClearAll(object sender, RoutedEventArgs e)
    {
        HideContextMenu();
        try
        {
            var dialog = new ContentDialog
            {
                Title = "清空历史",
                Content = "确定删除当前配置下的全部观看历史吗？",
                PrimaryButtonText = "清空",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            Stores.DeleteHistories(VodConfigService.Cid);
            RefreshIfChanged();
        }
        catch (Exception ex) { Logger.E("HistoryPage", ex.Message); }
    }
}

/// <summary>历史网格视图项（DataTemplate Binding 用，公共属性 getter）。</summary>
public class HistoryItem
{
    public string Pic { get; set; }
    public string Title { get; set; }
    public string Remark { get; set; }
    public string SiteKey { get; set; }
    public string VodId { get; set; }
    public string Key { get; set; }
    public double Percent { get; set; }
    public string Meta { get; set; }
}
