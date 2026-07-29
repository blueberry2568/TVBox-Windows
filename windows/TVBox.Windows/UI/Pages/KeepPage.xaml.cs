using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;
using TVBoxForWindows.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace TVBoxForWindows.UI.Pages;

/// <summary>收藏页（契约 §5.2）：收藏网格（PosterCard，备注=站点名）；右键取消收藏；点击 → DetailPage。</summary>
public sealed partial class KeepPage : Page
{
    long _revision = -1;
    int _cid = -1;

    public KeepPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RefreshIfChanged();
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

    /// <summary>右键菜单：取消收藏（sender.DataContext 即视图项）。</summary>
    void OnRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not KeepItem item) return;
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
