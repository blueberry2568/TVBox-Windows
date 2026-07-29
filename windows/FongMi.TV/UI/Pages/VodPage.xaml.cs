using System.Collections.ObjectModel;
using FongMi.TV.Core;
using FongMi.TV.Engine;
using FongMi.TV.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace FongMi.TV.UI.Pages;

/// <summary>点播浏览页（契约 §5.2）：站点下拉 + 分类横向 Tab（HomeContent 的 Types）+
/// 筛选面板（Filters 展开，选中改 extend 后重载）+ 无限滚动网格（pg 递增，触底加载，pagecount 停止）+
/// folder 类型子层导航（面包屑返回）。点击条目 → DetailPage。</summary>
public sealed partial class VodPage : Page
{
    const int MaxAutoFill = 3; // 首屏未填满时的自动补页上限

    readonly ObservableCollection<VodCell> _items = new();
    readonly List<FolderLevel> _folders = new();
    List<VodClass> _types = new();
    Site _site;
    VodClass _type;
    int _page = 1;
    int? _pageCount;
    bool _loading, _end, _squelch;
    int _seq, _autoFill;
    ScrollViewer _scroller;
    string _configUrl; // 已初始化的配置地址，配置切换后重建

    public VodPage()
    {
        InitializeComponent();
        ContentGrid.ItemsSource = _items;
        // 配置重载后重建站点/分类（若当前正显示本页）
        VodConfigService.Instance.Loaded += () =>
        {
            _configUrl = null;
            if (IsLoaded) InitSites();
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_configUrl != VodConfigService.Instance.Config?.Url) InitSites();
    }

    // ---------- 站点 ----------

    /// <summary>填充站点下拉（非隐藏站），默认选中 Home 站点。</summary>
    void InitSites()
    {
        _configUrl = VodConfigService.Instance.Config?.Url;
        var sites = VodConfigService.Instance.Sites.Where(s => !s.IsHidden).ToList();
        _squelch = true;
        SiteCombo.ItemsSource = sites;
        var index = sites.FindIndex(s => s.Key == VodConfigService.Instance.Home.Key);
        SiteCombo.SelectedIndex = sites.Count == 0 ? -1 : Math.Max(0, index);
        _squelch = false;
        LoadSite(SiteCombo.SelectedItem as Site);
    }

    void OnSiteChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_squelch || SiteCombo.SelectedItem is not Site site || site == _site) return;
        LoadSite(site);
    }

    /// <summary>加载站点分类（HomeContent 的 Types）；SpiderNull 站点由 Result.Msg 提示不支持。</summary>
    async void LoadSite(Site site)
    {
        var seq = ++_seq;
        _site = site;
        _type = null;
        _types = new List<VodClass>();
        _folders.Clear();
        UpdateFolderBar();
        ClassList.ItemsSource = null;
        FilterToggle.Visibility = Visibility.Collapsed;
        FilterToggle.IsChecked = false;
        FilterCard.Visibility = Visibility.Collapsed;
        _items.Clear();
        _end = true;
        MsgBar.IsOpen = false;
        EmptyText.Visibility = Visibility.Collapsed;
        if (site == null) { EmptyText.Visibility = Visibility.Visible; return; }
        Busy.IsActive = true;
        try
        {
            var result = await SiteService.HomeContent(site);
            if (seq != _seq) return;
            _types = result.Types ?? new List<VodClass>();
            _squelch = true;
            ClassList.ItemsSource = _types;
            _squelch = false;
            if (_types.Count > 0)
            {
                _squelch = true;
                ClassList.SelectedIndex = 0;
                _squelch = false;
                SelectClass(_types[0]);
            }
            else if (result.List.Count > 0)
            {
                // 无分类站点：退化展示首页推荐（不翻页）
                foreach (var v in result.List) _items.Add(ToCell(site, v));
                _end = true;
            }
            else
            {
                ShowMsg(string.IsNullOrEmpty(result.Msg) ? "该站点没有分类" : result.Msg);
                EmptyText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception e)
        {
            Logger.E("VodPage", "site: " + e.Message);
            if (seq == _seq) ShowMsg(e.Message);
        }
        finally { if (seq == _seq) Busy.IsActive = false; }
    }

    // ---------- 分类 ----------

    void OnClassChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_squelch || ClassList.SelectedItem is not VodClass type || type == _type) return;
        SelectClass(type);
    }

    void SelectClass(VodClass type)
    {
        _type = type;
        _folders.Clear();
        UpdateFolderBar();
        BuildFilterPanel();
        ReloadContent();
    }

    // ---------- 筛选面板 ----------

    void OnFilterToggle(object sender, RoutedEventArgs e)
        => FilterCard.Visibility = FilterToggle.IsChecked == true && FilterPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>按当前分类的 Filters 构建面板：每个 Filter 一行 chips（单选，选中写 Extend，取消移除）。</summary>
    void BuildFilterPanel()
    {
        FilterPanel.Children.Clear();
        var filters = _type?.Filters ?? new List<Filter>();
        FilterToggle.Visibility = filters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (filters.Count == 0)
        {
            FilterToggle.IsChecked = false;
            FilterCard.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (var filter in filters)
        {
            // init 默认值：预置进 Extend（与 Android FilterAdapter 的 activated 初值一致）
            if (!string.IsNullOrEmpty(filter.Init) && !_type.Extend.ContainsKey(filter.Key) && filter.Value.Any(v => v.V == filter.Init))
                _type.Extend[filter.Key] = filter.Init;

            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _type.Extend.TryGetValue(filter.Key, out var selected);
            foreach (var value in filter.Value)
            {
                var chip = new ToggleButton { Content = value.N, Tag = value.V, IsChecked = selected != null && selected == value.V };
                var key = filter.Key;
                chip.Click += (s, e) => OnChipClick(chips, chip, key);
                chips.Children.Add(chip);
            }
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock
            {
                Text = string.IsNullOrEmpty(filter.Name) ? filter.Key : filter.Name,
                MinWidth = 48,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var brush) && brush is Brush b)
                label.Foreground = b;
            var scroller = new ScrollViewer
            {
                Content = chips,
                HorizontalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 2, 0, 2),
            };
            Grid.SetColumn(scroller, 1);
            row.Children.Add(label);
            row.Children.Add(scroller);
            FilterPanel.Children.Add(row);
        }
        FilterCard.Visibility = FilterToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>chip 单选切换：改 Extend 后回到分类根重新加载。</summary>
    void OnChipClick(StackPanel chips, ToggleButton chip, string key)
    {
        if (_type == null) return;
        if (chip.IsChecked == true)
        {
            foreach (var other in chips.Children.OfType<ToggleButton>())
                if (other != chip) other.IsChecked = false;
            _type.Extend[key] = chip.Tag as string ?? "";
        }
        else _type.Extend.Remove(key);
        _folders.Clear();
        UpdateFolderBar();
        ReloadContent();
    }

    // ---------- 内容加载（无限滚动） ----------

    /// <summary>重置分页并加载第一页（不动 folder 栈，folder 进出也走这里）。</summary>
    void ReloadContent()
    {
        _seq++;          // 使在途请求的结果失效
        _loading = false;
        Busy.IsActive = false; // 由新的 LoadPage 重新点亮
        _items.Clear();
        _page = 1;
        _pageCount = null;
        _end = false;
        _autoFill = 0;
        MsgBar.IsOpen = false;
        EmptyText.Visibility = Visibility.Collapsed;
        _ = LoadPage();
    }

    async Task LoadPage()
    {
        if (_loading || _end || _site == null) return;
        var tid = _folders.Count > 0 ? _folders[^1].Tid : _type?.TypeId;
        if (string.IsNullOrEmpty(tid)) { _end = true; return; }
        var seq = _seq;
        _loading = true;
        Busy.IsActive = true;
        try
        {
            var extend = new Dictionary<string, string>(_type?.Extend ?? new Dictionary<string, string>());
            var result = await SiteService.CategoryContent(_site, tid, _page.ToString(), true, extend);
            if (seq != _seq) return;
            if (_pageCount == null && result.PageCount is > 0) _pageCount = result.PageCount;
            int added = 0;
            foreach (var v in result.List) { _items.Add(ToCell(_site, v)); added++; }
            if (added == 0) _end = true; else _page++;
            if (_pageCount is > 0 && _page > _pageCount) _end = true;
            if (_items.Count == 0 && !string.IsNullOrEmpty(result.Msg)) ShowMsg(result.Msg);
            EmptyText.Visibility = _items.Count == 0 && !MsgBar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception e)
        {
            Logger.E("VodPage", "category: " + e.Message);
            if (seq == _seq) { _end = true; if (_items.Count == 0) ShowMsg(e.Message); }
        }
        finally
        {
            if (seq == _seq)
            {
                _loading = false;
                Busy.IsActive = false;
                AutoFill();
            }
        }
    }

    /// <summary>首屏内容不足一屏时自动补页（上限 MaxAutoFill，防循环）。</summary>
    void AutoFill()
    {
        if (_end || _scroller == null || _autoFill >= MaxAutoFill) return;
        ContentGrid.UpdateLayout();
        if (_scroller.ScrollableHeight > 1) return;
        _autoFill++;
        _ = LoadPage();
    }

    void OnGridLoaded(object sender, RoutedEventArgs e)
    {
        if (_scroller != null) return;
        _scroller = FindScrollViewer(ContentGrid);
        if (_scroller == null) return;
        // 触底加载：距底部 400 内触发下一页
        _scroller.ViewChanged += (s, a) =>
        {
            if (_scroller.VerticalOffset >= _scroller.ScrollableHeight - 400) _ = LoadPage();
        };
    }

    static ScrollViewer FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var inner = FindScrollViewer(child);
            if (inner != null) return inner;
        }
        return null;
    }

    // ---------- 点击与 folder 层级 ----------

    async void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not VodCell cell) return;
        if (cell.IsFolder)
        {
            // folder 类型：用 vod_id 作 tid 进入子层
            _folders.Add(new FolderLevel { Tid = cell.VodId, Name = cell.Title });
            UpdateFolderBar();
            ReloadContent();
            return;
        }
        if (VodActionRouter.ShouldSearch(cell.SiteKey, cell.VodId))
        {
            Frame.Navigate(typeof(SearchPage), cell.Title);
            return;
        }
        var routed = await VodActionRouter.RouteAsync(cell.SiteKey, cell.VodId, cell.Title, cell.Remark, cell.Action);
        if (routed.Consumed)
        {
            if (!string.IsNullOrEmpty(routed.Message)) ShowMsg(routed.Message);
            return;
        }
        Frame.Navigate(typeof(DetailPage), new DetailArgs { SiteKey = cell.SiteKey, VodId = cell.VodId, Name = cell.Title });
    }

    void OnFolderBack(object sender, RoutedEventArgs e)
    {
        if (_folders.Count == 0) return;
        _folders.RemoveAt(_folders.Count - 1);
        UpdateFolderBar();
        ReloadContent();
    }

    void UpdateFolderBar()
    {
        FolderBar.Visibility = _folders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var root = string.IsNullOrEmpty(_type?.TypeName) ? "根目录" : _type.TypeName;
        FolderPathText.Text = _folders.Count > 0 ? root + " / " + string.Join(" / ", _folders.Select(f => f.Name)) : "";
    }

    // ---------- 辅助 ----------

    void ShowMsg(string msg)
    {
        MsgBar.Message = msg ?? "";
        MsgBar.IsOpen = !string.IsNullOrEmpty(msg);
    }

    static VodCell ToCell(Site site, Vod v) => new()
    {
        Pic = v.Pic,
        Title = v.CleanName,
        Remark = v.IsFolder && string.IsNullOrEmpty(v.Remarks) ? "文件夹" : v.Remarks,
        SiteKey = site.Key,
        VodId = v.Id,
        IsFolder = v.IsFolder,
        Action = v.Action,
    };

    class FolderLevel
    {
        public string Tid;
        public string Name;
    }
}

/// <summary>点播网格视图项（DataTemplate Binding 用，公共属性 getter）。</summary>
public class VodCell
{
    public string Pic { get; set; }
    public string Title { get; set; }
    public string Remark { get; set; }
    public string SiteKey { get; set; }
    public string VodId { get; set; }
    public bool IsFolder { get; set; }
    public string Action { get; set; }
}
