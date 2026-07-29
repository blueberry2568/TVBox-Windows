using System.Net;
using System.Text.RegularExpressions;
using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;
using TVBoxForWindows.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace TVBoxForWindows.UI.Pages;

/// <summary>详情页（契约 §5.2）：参数 DetailArgs（PushUrl 场景 SiteKey 空 → SiteService 构造推送 Vod，
/// PlayerContent 走 push 分支 parse=0 直接播）。Hero 海报背景 + 信息 + 操作行（播放记忆上次线路集数、
/// 收藏切换、换源搜索）+ 线路 Tab + 集数流式布局（>200 集分段下拉）+ 倒序开关。点击集 → PlayerPage。</summary>
public sealed partial class DetailPage : Page
{
    const int SegmentSize = 100;

    Site _site;
    Vod _vod;
    string _vodId;
    List<VodFlag> _flags = new();
    List<Episode> _ordered = new(); // 当前线路按显示顺序排列的集数
    int _flagIndex;
    int _segment;
    bool _reverse, _squelch;

    public DetailPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not DetailArgs args) return;
        // PushUrl 场景：SiteKey 空 → 空站点（SiteService.DetailContent 的 push 分支），vodId=PushUrl
        _site = VodConfigService.Instance.GetSite(args.SiteKey);
        _vodId = string.IsNullOrEmpty(args.PushUrl) ? args.VodId : args.PushUrl;
        if (!string.IsNullOrEmpty(args.Name)) TitleText.Text = args.Name;
        _ = LoadDetail();
    }

    // ---------- 加载与绑定 ----------

    async Task LoadDetail()
    {
        if (string.IsNullOrEmpty(_vodId)) { ShowMsg("缺少内容参数"); return; }
        Busy.IsActive = true;
        MsgBar.IsOpen = false;
        try
        {
            var result = await SiteService.DetailContent(_site, _vodId);
            if (result.List.Count == 0)
            {
                ShowMsg(string.IsNullOrEmpty(result.Msg) ? "没有找到内容" : result.Msg);
                return;
            }
            Bind(result.Vod);
        }
        catch (Exception e)
        {
            Logger.E("DetailPage", e.Message);
            ShowMsg(e.Message);
        }
        finally { Busy.IsActive = false; }
    }

    void Bind(Vod vod)
    {
        _vod = vod;
        _site = vod.Site ?? _site;
        HeroPanel.Visibility = Visibility.Visible;
        TitleText.Text = NameText.Text = vod.CleanName;
        Poster.Source = vod.Pic;
        Backdrop.Source = vod.Pic;
        MetaText.Text = Join(vod.Year, vod.Area, vod.TypeName, vod.Remarks);
        SetOptional(DirectorText, "导演：", vod.Director);
        SetOptional(ActorText, "演员：", vod.Actor);
        var content = StripHtml(vod.Content);
        ContentText.Text = content;
        ContentText.Visibility = ExpandButton.Visibility = content.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        _flags = vod.GetFlags();
        var history = FindHistory();
        _flagIndex = 0;
        if (history != null)
        {
            var fi = _flags.FindIndex(f => f.Flag == history.VodFlag);
            if (fi >= 0) _flagIndex = fi;
            _reverse = history.RevSort;
        }
        RevButton.IsChecked = _reverse;
        FlagsPanel.Visibility = _flags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_flags.Count == 0) ShowMsg("暂无播放线路");
        _squelch = true;
        FlagList.ItemsSource = _flags;
        FlagList.SelectedIndex = _flags.Count > 0 ? _flagIndex : -1;
        _squelch = false;
        _segment = 0;
        RefreshEpisodes();
        UpdatePlayButton();
        UpdateKeepState();
    }

    // ---------- 线路与集数 ----------

    VodFlag CurrentFlag => _flags.Count > 0 ? _flags[Math.Clamp(_flagIndex, 0, _flags.Count - 1)] : null;

    void OnFlagChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_squelch || FlagList.SelectedIndex < 0) return;
        _flagIndex = FlagList.SelectedIndex;
        _segment = 0;
        RefreshEpisodes();
        UpdatePlayButton();
    }

    /// <summary>重建当前线路的显示顺序（倒序开关）与分段下拉，再切片填充网格。</summary>
    void RefreshEpisodes()
    {
        var episodes = CurrentFlag?.Episodes ?? new List<Episode>();
        _ordered = _reverse ? Enumerable.Reverse(episodes).ToList() : new List<Episode>(episodes);
        CountText.Text = episodes.Count > 0 ? $"共 {episodes.Count} 集" : "";
        if (_ordered.Count > 200)
        {
            var labels = new List<string>();
            for (int start = 0; start < _ordered.Count; start += SegmentSize)
                labels.Add($"{start + 1}-{Math.Min(start + SegmentSize, _ordered.Count)}");
            _squelch = true;
            SegmentCombo.ItemsSource = labels;
            _segment = Math.Clamp(_segment, 0, labels.Count - 1);
            SegmentCombo.SelectedIndex = _segment;
            _squelch = false;
            SegmentCombo.Visibility = Visibility.Visible;
        }
        else
        {
            _segment = 0;
            SegmentCombo.Visibility = Visibility.Collapsed;
        }
        ApplySlice();
    }

    void ApplySlice()
        => EpisodeGrid.ItemsSource = _ordered.Count > 200
            ? _ordered.Skip(_segment * SegmentSize).Take(SegmentSize).ToList()
            : _ordered;

    void OnSegmentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_squelch || SegmentCombo.SelectedIndex < 0) return;
        _segment = SegmentCombo.SelectedIndex;
        ApplySlice();
    }

    void OnReverse(object sender, RoutedEventArgs e)
    {
        _reverse = RevButton.IsChecked == true;
        _segment = 0;
        RefreshEpisodes();
    }

    void OnEpisodeClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Episode ep) return;
        var episodes = CurrentFlag?.Episodes;
        if (episodes == null) return;
        var index = episodes.IndexOf(ep);
        Play(_flagIndex, index < 0 ? 0 : index);
    }

    // ---------- 操作行 ----------

    /// <summary>播放按钮：记忆上次 flag/ep（Stores.FindHistory：flag 名匹配 + EpisodeUrl 匹配）。</summary>
    void OnPlay(object sender, RoutedEventArgs e) => Play(_flagIndex, RememberedEpisodeIndex());

    void Play(int flagIndex, int epIndex)
    {
        if (_vod == null || _flags.Count == 0) return;
        var session = PlaySession.FromDetail(_site, _vod, flagIndex, epIndex);
        Frame.Navigate(typeof(PlayerPage), new PlayerArgs { Session = session });
    }

    void OnKeep(object sender, RoutedEventArgs e)
    {
        if (_vod == null) { KeepButton.IsChecked = false; return; }
        var cid = VodConfigService.Cid;
        var key = HistoryKey();
        if (KeepButton.IsChecked == true)
            Stores.SaveKeep(new Keep
            {
                Key = key,
                Cid = cid,
                SiteName = _site?.Name ?? "",
                VodName = _vod.CleanName,
                VodPic = _vod.Pic,
                Type = 0,
            });
        else Stores.DeleteKeep(cid, key);
        UpdateKeepState();
    }

    void OnSearchOther(object sender, RoutedEventArgs e)
    {
        var keyword = _vod?.CleanName;
        if (!string.IsNullOrEmpty(keyword)) Frame.Navigate(typeof(SearchPage), keyword);
    }

    void OnToggleContent(object sender, RoutedEventArgs e)
    {
        var expand = ContentText.MaxLines != 0;
        ContentText.MaxLines = expand ? 0 : 3;
        ExpandButton.Content = expand ? "收起" : "展开";
    }

    void OnBack(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    // ---------- 状态辅助 ----------

    string HistoryKey() => (_site?.Key ?? "") + "@" + (_vod?.Id ?? _vodId ?? "");

    History FindHistory() => Stores.FindHistory(VodConfigService.Cid, HistoryKey());

    /// <summary>上次看到的集在当前线路中的索引（按 EpisodeUrl 匹配，找不到回 0）。</summary>
    int RememberedEpisodeIndex()
    {
        var history = FindHistory();
        var episodes = CurrentFlag?.Episodes;
        if (history == null || episodes == null || string.IsNullOrEmpty(history.EpisodeUrl)) return 0;
        var index = episodes.FindIndex(ep => ep.Url == history.EpisodeUrl);
        return index < 0 ? 0 : index;
    }

    void UpdatePlayButton()
    {
        var episodes = CurrentFlag?.Episodes;
        var index = RememberedEpisodeIndex();
        PlayText.Text = FindHistory() != null && episodes is { Count: > 0 } && index < episodes.Count
            ? "续播 " + episodes[index].Name
            : "播放";
    }

    void UpdateKeepState()
    {
        var kept = _vod != null && Stores.FindKeep(VodConfigService.Cid, HistoryKey()) != null;
        KeepButton.IsChecked = kept;
        KeepIcon.Glyph = kept ? "\uE735" : "\uE734"; // FavoriteStarFill / FavoriteStar
        KeepText.Text = kept ? "已收藏" : "收藏";
    }

    void ShowMsg(string msg)
    {
        MsgBar.Message = msg ?? "";
        MsgBar.IsOpen = !string.IsNullOrEmpty(msg);
    }

    static string Join(params string[] parts)
        => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));

    static void SetOptional(TextBlock block, string prefix, string value)
    {
        var text = StripHtml(value);
        block.Text = text.Length > 0 ? prefix + text : "";
        block.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>简介/演职员可能带 HTML：解码实体并去标签。</summary>
    static string StripHtml(string text)
    {
        try { return Regex.Replace(WebUtility.HtmlDecode(text ?? ""), "<[^>]+>", "").Trim(); }
        catch { return (text ?? "").Trim(); }
    }
}
