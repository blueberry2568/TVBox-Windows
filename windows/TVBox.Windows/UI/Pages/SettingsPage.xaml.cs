using System.IO.Compression;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using TVBoxForWindows.Core;
using TVBoxForWindows.Models;
using TVBoxForWindows.Engine;
using TVBoxForWindows.Live;
using TVBoxForWindows.Net;
using TVBoxForWindows.Player;
using TVBoxForWindows.Server;

namespace TVBoxForWindows.UI.Pages;

/// <summary>设置页：配置管理 / 播放 / 弹幕 / 网络 / 隐私与数据 / 关于。</summary>
public sealed partial class SettingsPage : Page
{
    static readonly int[] TimeoutValues = { 5000, 10000, 15000, 30000 };
    static readonly int[] AreaValues = { 25, 50, 75, 100 };

    bool _updating = true; // InitializeComponent 也会触发 Slider.ValueChanged，必须从构造前就抑制
    bool _vodConfigLoading;
    bool _liveConfigLoading;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _statusTimer;

    public SettingsPage()
    {
        InitializeComponent();
        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(3.5);
        _statusTimer.IsRepeating = false;
        _statusTimer.Tick += (s, e) => StatusInfo.IsOpen = false;
        Loaded += (s, e) =>
        {
            VodConfigService.Instance.Loaded += OnVodLoaded;
            LiveConfigService.Instance.Loaded += OnLiveLoaded;
            LoadAll();
        };
        Unloaded += (s, e) =>
        {
            _statusTimer.Stop();
            CloseConfigHistoryFlyouts();
            VodConfigService.Instance.Loaded -= OnVodLoaded;
            LiveConfigService.Instance.Loaded -= OnLiveLoaded;
        };
    }

    // ---------- 初始化 ----------

    void LoadAll()
    {
        _updating = true;
        NavigationPaneToggle.IsOn = App.Main.IsNavigationPaneOpen;
        SearchDisplayCombo.SelectedIndex = Math.Clamp(Setting.SearchDisplay, 0, 1);
        VodUrlBox.Text = Setting.ConfigVod;
        LiveUrlBox.Text = Setting.ConfigLive;
        RefreshConfigLists();
        RefreshSiteParse();
        RefreshDohs();
        SpeedSlider.Value = Math.Clamp(Setting.Speed, 1, 4);
        SpeedValueText.Text = $"{SpeedSlider.Value:0.##}x";
        ScaleCombo.SelectedIndex = Math.Clamp(Setting.Scale, 0, 4);
        PlayTimeoutCombo.SelectedIndex = TimeoutIndex(Setting.PlayTimeout);
        SkipToggle.IsOn = UiSetting.SkipStartEnd;
        DanmakuLoadToggle.IsOn = Setting.DanmakuLoad;
        DanmakuAutoToggle.IsOn = Setting.DanmakuAuto;
        DanmakuAlphaSlider.Value = Math.Clamp(UiSetting.DanmakuAlpha * 100, 10, 100);
        DanmakuSizeSlider.Value = Math.Clamp(UiSetting.DanmakuSize, 12, 48);
        DanmakuSpeedCombo.SelectedIndex = Math.Clamp(UiSetting.DanmakuSpeed - 1, 0, 2);
        var areaIdx = Array.IndexOf(AreaValues, UiSetting.DanmakuArea);
        DanmakuAreaCombo.SelectedIndex = areaIdx >= 0 ? areaIdx : 1;
        ProxyBox.Text = Setting.Proxy;
        UaBox.Text = Setting.Ua;
        SiteTimeoutCombo.SelectedIndex = TimeoutIndex(Setting.SiteTimeout);
        IncognitoToggle.IsOn = Setting.Incognito;
        LanServerToggle.IsOn = Setting.LocalServerLan;
        _updating = false;
        RefreshCacheSize();
        LoadAbout();
    }

    void OnNavigationPaneToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        App.Main.SetNavigationPaneOpen(NavigationPaneToggle.IsOn);
    }

    void OnSearchDisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || SearchDisplayCombo.SelectedIndex < 0) return;
        Setting.SearchDisplay = SearchDisplayCombo.SelectedIndex;
    }

    static int TimeoutIndex(int ms)
    {
        var idx = Array.IndexOf(TimeoutValues, ms);
        return idx >= 0 ? idx : 2;
    }

    void OnVodLoaded()
    {
        _updating = true;
        RefreshSiteParse();
        RefreshDohs();
        VodUrlBox.Text = Setting.ConfigVod;
        _updating = false;
        RefreshConfigLists();
    }

    void OnLiveLoaded()
    {
        LiveUrlBox.Text = Setting.ConfigLive;
        RefreshConfigLists();
    }

    void ShowStatus(string msg, InfoBarSeverity severity)
    {
        StatusInfo.Severity = severity;
        StatusInfo.Message = msg;
        StatusInfo.IsOpen = true;
        _statusTimer.Stop();
        if (severity is InfoBarSeverity.Success or InfoBarSeverity.Informational) _statusTimer.Start();
    }

    // ---------- ① 配置 ----------

    void RefreshConfigLists()
    {
        VodHistoryList.ItemsSource = Stores.GetConfigs(0);
        LiveHistoryList.ItemsSource = Stores.GetConfigs(1);
    }

    void RefreshSiteParse()
    {
        var svc = VodConfigService.Instance;
        var keep = _updating;
        _updating = true;
        SiteCombo.ItemsSource = svc.Sites;
        SiteCombo.SelectedItem = svc.Sites.Contains(svc.Home) ? svc.Home : null;
        var hasParses = svc.Parses.Count > 0;
        ParseCombo.ItemsSource = hasParses ? svc.Parses : null;
        ParseCombo.SelectedItem = hasParses && svc.Parses.Contains(svc.Parse) ? svc.Parse : null;
        ParseCombo.Visibility = hasParses ? Visibility.Visible : Visibility.Collapsed;
        ParseUnavailableText.Visibility = hasParses ? Visibility.Collapsed : Visibility.Visible;
        _updating = keep;
    }

    async void OnVodLoad(object sender, RoutedEventArgs e)
    {
        if (_vodConfigLoading) return;
        var url = VodUrlBox.Text?.Trim();
        if (string.IsNullOrEmpty(url)) return;
        CloseConfigHistoryFlyouts();
        _vodConfigLoading = true;
        VodLoadButton.IsEnabled = false;
        try
        {
            await VodConfigService.Instance.LoadAsync(Stores.FindConfig(url, 0));
            ShowStatus("点播配置加载成功", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowStatus("点播配置加载失败：" + ex.Message, InfoBarSeverity.Error); }
        finally
        {
            _vodConfigLoading = false;
            VodLoadButton.IsEnabled = true;
            RefreshConfigLists();
        }
    }

    void OnVodUrlLostFocus(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var url = VodUrlBox.Text?.Trim();
        if (!string.IsNullOrEmpty(url) && !string.Equals(url, Setting.ConfigVod, StringComparison.OrdinalIgnoreCase))
            OnVodLoad(sender, e);
    }

    void OnVodUrlKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        OnVodLoad(sender, null);
    }

    void OnVodHistoryClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ConfigRecord rec) { VodUrlBox.Text = rec.Url; OnVodLoad(sender, null); }
    }

    void OnVodHistoryDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConfigRecord rec)
        {
            Stores.DeleteConfig(rec.Url, rec.Type);
            RefreshConfigLists();
        }
    }

    async void OnLiveLoad(object sender, RoutedEventArgs e)
    {
        if (_liveConfigLoading) return;
        var url = LiveUrlBox.Text?.Trim();
        if (string.IsNullOrEmpty(url)) return;
        CloseConfigHistoryFlyouts();
        _liveConfigLoading = true;
        LiveLoadButton.IsEnabled = false;
        try
        {
            await LiveConfigService.Instance.LoadAsync(Stores.FindConfig(url, 1));
            ShowStatus("直播配置加载成功", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowStatus("直播配置加载失败：" + ex.Message, InfoBarSeverity.Error); }
        finally
        {
            _liveConfigLoading = false;
            LiveLoadButton.IsEnabled = true;
            RefreshConfigLists();
        }
    }

    void OnLiveUrlLostFocus(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var url = LiveUrlBox.Text?.Trim();
        if (!string.IsNullOrEmpty(url) && !string.Equals(url, Setting.ConfigLive, StringComparison.OrdinalIgnoreCase))
            OnLiveLoad(sender, e);
    }

    void OnLiveUrlKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        OnLiveLoad(sender, null);
    }

    void OnLiveHistoryClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ConfigRecord rec) { LiveUrlBox.Text = rec.Url; OnLiveLoad(sender, null); }
    }

    void OnLiveHistoryDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ConfigRecord rec) return;
        Stores.DeleteConfig(rec.Url, rec.Type);
        RefreshConfigLists();
    }

    void CloseConfigHistoryFlyouts()
    {
        VodHistoryFlyout?.Hide();
        LiveHistoryFlyout?.Hide();
    }

    void OnSiteChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (SiteCombo.SelectedItem is Site site && !site.IsEmpty) VodConfigService.Instance.SetHome(site);
    }

    void OnParseChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (ParseCombo.SelectedItem is Parse parse) VodConfigService.Instance.SetParse(parse);
    }

    // ---------- ② 播放 ----------

    void OnSpeedChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        SpeedValueText.Text = $"{e.NewValue:0.##}x";
        if (_updating) return;
        Setting.Speed = e.NewValue;
    }

    void OnScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || ScaleCombo.SelectedIndex < 0) return;
        Setting.Scale = ScaleCombo.SelectedIndex;
    }

    void OnPlayTimeoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || PlayTimeoutCombo.SelectedIndex < 0) return;
        Setting.PlayTimeout = TimeoutValues[PlayTimeoutCombo.SelectedIndex];
    }

    void OnSkipToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        UiSetting.SkipStartEnd = SkipToggle.IsOn;
    }

    // ---------- ③ 弹幕 ----------

    void OnDanmakuLoadToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        Setting.DanmakuLoad = DanmakuLoadToggle.IsOn;
    }

    void OnDanmakuAutoToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        Setting.DanmakuAuto = DanmakuAutoToggle.IsOn;
    }

    void OnDanmakuAlphaChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        UiSetting.DanmakuAlpha = (float)(e.NewValue / 100.0);
    }

    void OnDanmakuSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        UiSetting.DanmakuSize = (int)e.NewValue;
    }

    void OnDanmakuSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || DanmakuSpeedCombo.SelectedIndex < 0) return;
        UiSetting.DanmakuSpeed = DanmakuSpeedCombo.SelectedIndex + 1;
    }

    void OnDanmakuAreaChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || DanmakuAreaCombo.SelectedIndex < 0) return;
        UiSetting.DanmakuArea = AreaValues[DanmakuAreaCombo.SelectedIndex];
    }

    // ---------- ④ 网络 ----------

    void RefreshDohs()
    {
        var list = BuiltInDohs();
        foreach (var doh in VodConfigService.Instance.Dohs)
            if (!string.IsNullOrEmpty(doh.Name) && list.All(x => x.Name != doh.Name)) list.Add(doh);
        var keep = _updating;
        _updating = true;
        DohCombo.ItemsSource = list;
        DohCombo.SelectedItem = list.FirstOrDefault(d => d.Name == Setting.Doh && !string.IsNullOrEmpty(Setting.Doh)) ?? list[0];
        _updating = keep;
    }

    static List<Doh> BuiltInDohs() => new()
    {
        new Doh { Name = "关闭", Url = "" },
        new Doh { Name = "腾讯", Url = "https://doh.pub/dns-query" },
        new Doh { Name = "阿里", Url = "https://dns.alidns.com/dns-query" },
        new Doh { Name = "360", Url = "https://doh.360.cn/dns-query" },
        new Doh { Name = "Google", Url = "https://dns.google/dns-query" },
        new Doh { Name = "Cloudflare", Url = "https://cloudflare-dns.com/dns-query" },
        new Doh { Name = "AdGuard", Url = "https://dns.adguard.com/dns-query" },
    };

    void OnDohChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (DohCombo.SelectedItem is not Doh doh) return;
        var off = string.IsNullOrEmpty(doh.Url);
        Setting.Doh = off ? "" : doh.Name;
        NetworkConfig.Doh = off ? null : doh;
    }

    void OnProxyLost(object sender, RoutedEventArgs e)
    {
        var text = ProxyBox.Text?.Trim() ?? "";
        if (text != Setting.Proxy) Setting.Proxy = text;
    }

    void OnUaLost(object sender, RoutedEventArgs e)
    {
        var text = UaBox.Text?.Trim() ?? "";
        if (text != Setting.Ua) Setting.Ua = text;
    }

    void OnSiteTimeoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || SiteTimeoutCombo.SelectedIndex < 0) return;
        Setting.SiteTimeout = TimeoutValues[SiteTimeoutCombo.SelectedIndex];
    }

    // ---------- ⑤ 隐私与数据 ----------

    void OnIncognitoToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        Setting.Incognito = IncognitoToggle.IsOn;
    }

    void OnLanServerToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        LocalServer.Instance.Restart(LanServerToggle.IsOn);
        LoadAbout();
        if (LanServerToggle.IsOn && !LocalServer.Instance.IsLanAccessible)
            ShowStatus("局域网监听不可用，已保持仅本机访问", InfoBarSeverity.Warning);
    }

    async void RefreshCacheSize()
    {
        try
        {
            var size = await Task.Run(() => DirSize(AppPaths.Cache));
            CacheSizeText.Text = "当前缓存：" + FormatSize(size);
        }
        catch { CacheSizeText.Text = ""; }
    }

    static long DirSize(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch { return 0; }
    }

    static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} B",
    };

    async void OnClearCache(object sender, RoutedEventArgs e)
    {
        ClearCacheButton.IsEnabled = false;
        await Task.Run(() =>
        {
            try
            {
                var dir = new DirectoryInfo(AppPaths.Cache);
                if (!dir.Exists) return;
                foreach (var f in dir.EnumerateFiles()) { try { f.Delete(); } catch { } }
                foreach (var d in dir.EnumerateDirectories()) { try { d.Delete(true); } catch { } }
            }
            catch { }
        });
        ClearCacheButton.IsEnabled = true;
        ShowStatus("缓存已清空", InfoBarSeverity.Success);
        RefreshCacheSize();
    }

    async void OnBackup(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
                SuggestedFileName = "TVBox-for-Windows-backup-" + DateTime.Now.ToString("yyyyMMdd"),
            };
            picker.FileTypeChoices.Add("Zip 压缩包", new List<string> { ".zip" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Main));
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            await Task.Run(() =>
            {
                using var fs = new FileStream(file.Path, FileMode.Create, FileAccess.Write);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                foreach (var json in Directory.GetFiles(AppPaths.Root, "*.json"))
                    zip.CreateEntryFromFile(json, Path.GetFileName(json));
            });
            ShowStatus("备份完成：" + file.Path, InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowStatus("备份失败：" + ex.Message, InfoBarSeverity.Error); }
    }

    async void OnRestore(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
            };
            picker.FileTypeFilter.Add(".zip");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Main));
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            await Task.Run(() =>
            {
                using var zip = ZipFile.OpenRead(file.Path);
                foreach (var entry in zip.Entries)
                    if (entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        entry.ExtractToFile(Path.Combine(AppPaths.Root, entry.Name), true);
            });
            Setting.Load();
            ShowStatus("还原完成，建议重启应用使全部数据生效", InfoBarSeverity.Success);
            LoadAll();
        }
        catch (Exception ex) { ShowStatus("还原失败：" + ex.Message, InfoBarSeverity.Error); }
    }

    // ---------- ⑥ 关于 ----------

    void LoadAbout()
    {
        string ver;
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            ver = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch { ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0"; }
        VersionText.Text = "TVBox for Windows v" + ver;

        bool ready = false;
        try { ready = PlayerCore.EngineReady; } catch { }
        FfmpegStatusText.Text = ready ? "✔ FFmpeg 已就绪，可正常播放" : "✘ 未检测到 FFmpeg";
        FfmpegGuidePanel.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;

        try { ServerAddrText.Text = "本机：" + LocalServer.Instance.GetAddress("/"); } catch { ServerAddrText.Text = ""; }
        var lan = LocalServer.Instance.IsLanAccessible ? GetLanAddress() : "";
        ServerLanText.Text = string.IsNullOrEmpty(lan) ? "" : "局域网：" + lan;
    }

    /// <summary>私有辅助：取局域网 IPv4 地址组装服务器地址（契约 GetAddress 只给回环地址）。</summary>
    static string GetLanAddress()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return $"http://{addr.Address}:{LocalServer.Instance.Port}/";
            }
        }
        catch { }
        return "";
    }

    void OnOpenFfmpeg(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = AppPaths.FFmpegDir;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + dir + "\"",
                UseShellExecute = true,
            });
        }
        catch { }
    }

    void OnCopyAddr(object sender, RoutedEventArgs e)
    {
        try
        {
            var lan = LocalServer.Instance.IsLanAccessible ? GetLanAddress() : "";
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(string.IsNullOrEmpty(lan) ? LocalServer.Instance.GetAddress("/") : lan);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
            ShowStatus("地址已复制", InfoBarSeverity.Success);
        }
        catch { }
    }
}

/// <summary>设置页私有设置包装（不改 Core/Setting.cs）：跳片头尾与弹幕样式，键名供播放模块按同名键读取。</summary>
static class UiSetting
{
    /// <summary>自动跳过片头片尾，键 "skip_start_end"。</summary>
    public static bool SkipStartEnd
    {
        get => Setting.GetBool("skip_start_end");
        set => Setting.Put("skip_start_end", value);
    }

    /// <summary>弹幕透明度 0~1，键 "danmaku_alpha"。</summary>
    public static float DanmakuAlpha
    {
        get => Setting.GetFloat("danmaku_alpha", 0.9f);
        set => Setting.Put("danmaku_alpha", value);
    }

    /// <summary>弹幕字号（像素），键 "danmaku_size"。</summary>
    public static int DanmakuSize
    {
        get => Setting.GetInt("danmaku_size", 24);
        set => Setting.Put("danmaku_size", value);
    }

    /// <summary>弹幕速度 1慢 2中 3快，键 "danmaku_speed"。</summary>
    public static int DanmakuSpeed
    {
        get => Setting.GetInt("danmaku_speed", 2);
        set => Setting.Put("danmaku_speed", value);
    }

    /// <summary>弹幕显示区域（屏幕百分比 25/50/75/100），键 "danmaku_area"。</summary>
    public static int DanmakuArea
    {
        get => Setting.GetInt("danmaku_area", 50);
        set => Setting.Put("danmaku_area", value);
    }
}
