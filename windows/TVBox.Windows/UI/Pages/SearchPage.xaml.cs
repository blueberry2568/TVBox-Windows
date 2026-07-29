using System.Collections.ObjectModel;
using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace TVBoxForWindows.UI.Pages;

/// <summary>搜索页：SearchService.SearchAll 流式结果按站点分组（顶部横向站点 + 纵向卡片）、
/// 搜索历史 chips（Setting 存 JSON list）、进行中的 ProgressBar 与取消。导航参数可带初始关键词（string）。</summary>
public sealed partial class SearchPage : Page
{
    const string HistoryKey = "keyword_history";
    const int HistoryMax = 16;

    readonly ObservableCollection<SiteGroup> _groups = new();
    CancellationTokenSource _cts;

    public SearchPage()
    {
        InitializeComponent();
        SiteList.ItemsSource = _groups;
        GroupedResultList.ItemsSource = _groups;
        RefreshDisplayStyle();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        BackButton.Visibility = Frame.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        RefreshDisplayStyle();
        UpdateScope();
        LoadChips();
        if (e.Parameter is string keyword && !string.IsNullOrWhiteSpace(keyword))
        {
            SearchBox.Text = keyword;
            StartSearch(keyword);
        }
    }

    void UpdateScope()
    {
        var count = VodConfigService.Instance.Sites.Count(s => s.IsSearchable && !s.IsHidden);
        ScopeText.Text = count > 0 ? $"将搜索 {count} 个站点" : "暂无可搜索站点";
    }

    void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => StartSearch(args.QueryText);

    async void StartSearch(string keyword)
    {
        keyword = (keyword ?? "").Trim();
        if (keyword.Length == 0) return;
        SaveKeyword(keyword);
        LoadChips();
        UpdateScope();
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _groups.Clear();
        ResultGrid.ItemsSource = null;
        SiteList.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        SearchProgress.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        try
        {
            // 每站完成即回调（契约 §2.5：回调已切 UI 线程，空结果不回调）
            await SearchService.SearchAll(keyword, false, (site, list) =>
            {
                if (cts.IsCancellationRequested) return;
                var items = list
                    .Select(v => new VodItem { Pic = v.Pic, Title = v.CleanName, Remark = v.Remarks, SiteKey = site.Key, VodId = v.Id })
                    .ToList();
                _groups.Add(new SiteGroup(site.Name, items));
                RefreshDisplayStyle();
                if (SiteList.SelectedIndex < 0) SiteList.SelectedIndex = 0;
            }, cts.Token);
        }
        catch (Exception e) { Logger.E("SearchPage", e.Message); }
        finally
        {
            if (cts == _cts)
            {
                SearchProgress.Visibility = Visibility.Collapsed;
                CancelButton.Visibility = Visibility.Collapsed;
                EmptyText.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    void OnBack(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        if (Frame.CanGoBack) Frame.GoBack();
    }

    void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VodItem item)
            Frame.Navigate(typeof(DetailPage), new DetailArgs { SiteKey = item.SiteKey, VodId = item.VodId, Name = item.Title });
    }

    void OnGroupedResultClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VodItem item)
            Frame.Navigate(typeof(DetailPage), new DetailArgs { SiteKey = item.SiteKey, VodId = item.VodId, Name = item.Title });
    }

    public void RefreshDisplayStyle()
    {
        var grouped = Setting.SearchDisplay == 1;
        GroupedResultList.Visibility = grouped ? Visibility.Visible : Visibility.Collapsed;
        SiteList.Visibility = !grouped && _groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultGrid.Visibility = grouped ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnSiteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ResultGrid.ItemsSource = (SiteList.SelectedItem as SiteGroup)?.Items;
        DispatcherQueue.TryEnqueue(() => FindScrollViewer(ResultGrid)?.ChangeView(null, 0, null, true));
    }

    void OnSitePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var viewer = FindScrollViewer(SiteList);
        if (viewer == null || viewer.ScrollableWidth <= 0) return;
        var delta = e.GetCurrentPoint(SiteList).Properties.MouseWheelDelta;
        viewer.ChangeView(Math.Clamp(viewer.HorizontalOffset - delta, 0, viewer.ScrollableWidth), null, null, true);
        e.Handled = true;
    }

    static ScrollViewer FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    // ---------- 搜索历史（Setting 存 JSON list） ----------

    List<string> GetKeywords() => JsonUtil.Deserialize<List<string>>(Setting.GetString(HistoryKey)) ?? new();

    void SaveKeyword(string keyword)
    {
        var list = GetKeywords();
        list.Remove(keyword);
        list.Insert(0, keyword);
        if (list.Count > HistoryMax) list = list.Take(HistoryMax).ToList();
        Setting.Put(HistoryKey, JsonUtil.Serialize(list));
    }

    void LoadChips()
    {
        ChipsPanel.Children.Clear();
        var list = GetKeywords();
        HistoryPanel.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var word in list)
        {
            var chip = new Button { Content = word };
            chip.Click += (s, e) => { SearchBox.Text = word; StartSearch(word); };
            ChipsPanel.Children.Add(chip);
        }
    }

    void OnClearHistory(object sender, RoutedEventArgs e)
    {
        Setting.Remove(HistoryKey);
        LoadChips();
    }
}

/// <summary>按站点分组的结果组（Expander DataTemplate Binding 用）。</summary>
public class SiteGroup
{
    public SiteGroup(string siteName, List<VodItem> items)
    {
        SiteName = siteName;
        Items = items;
    }

    public string SiteName { get; }
    public List<VodItem> Items { get; }
    public string Header => $"{SiteName}（{Items.Count}）";
}

/// <summary>搜索结果卡片视图项。</summary>
public class VodItem
{
    public string Pic { get; set; }
    public string Title { get; set; }
    public string Remark { get; set; }
    public string SiteKey { get; set; }
    public string VodId { get; set; }
    public string Action { get; set; }
}
