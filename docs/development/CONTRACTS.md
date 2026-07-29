# FongMi.TV for Windows — 架构契约（所有模块开发者必读）

本文件是多模块并行开发的唯一契约。**所有公共类名、命名空间、方法签名必须与本文一致**，
否则集成编译会失败。已存在的文件（Core/Net/Models/Live/Engine 部分）不要重写，直接复用。

## 0. 全局约定

- TargetFramework `net9.0-windows10.0.19041.0`，WindowsAppSDK **1.8**，WinUI3。
- `Nullable` 关闭；`ImplicitUsings` 开启（System/Linq/IO/Collections.Generic/Threading.Tasks 无需 using）。
- 注释使用中文，风格与现有文件一致（`///` 摘要注明"移植自 XXX.java"）。
- 所有网络请求走 `FongMi.TV.Net.HttpUtil`（已实现 hosts/DoH/代理/广告拦截）。
- JSON 一律用 `Models.ModelJson`（宽容解析）或 `Core.JsonUtil`；**不要**引入 Newtonsoft。
- 日志 `Core.Logger.D/E(tag, msg)`。
- UI 线程调度 `App.Post(Action)`。
- 设置读写 `Core.Setting`（键值 + 强类型属性，需要新设置项就照样加属性）。
- 持久化 `Core.Stores`（Config/History/Keep 三表，JSON 文件落盘）。
- 播放内核 FlyleafLib 3.10.4（FFmpeg）；`FlyleafLib.Controls.WinUI` 的 `FlyleafHost` 呈现。
- HTML 解析 AngleSharp；JS 引擎 Jint 4.14。
- **禁止**引入本文未列出的 NuGet 包。

## 1. 既有代码（只读，直接调用）

| 类 | 要点 |
|---|---|
| `Core.Setting` | 键值存储；`SiteTimeout/PlayTimeout/Ua/Doh/Proxy/Incognito/Speed/Scale/ConfigVod/ConfigLive/...` |
| `Core.Stores` | `FindConfig(url,type)` `SaveConfig` `GetConfigs(type)`；History/Keep 同名方法族；`Now()` |
| `Core.Decoder` | `GetJson(url)` 配置下载+解密 |
| `Core.JsonUtil` | `Parse/Deserialize/Serialize/SafeString/SafeListString/ToMap/IsObj` |
| `Core.Sniffer` | `IsVideoFormat(url)` `GetRule(url)` `GetScript(url)` `AiPush/Media` 正则 |
| `Core.UrlUtil` | `Scheme/Host/Resolve/Convert/GetName/FixHeader` |
| `Core.Trans` | `S2T/T2S/Pass()` 繁简转换 |
| `Core.AppPaths` | `Root/Cache/Js/Live/Wall/Restore/Local` 目录；`ReadAsset` |
| `Net.HttpUtil` | `Get/GetString/Execute/Load`；`OkResponse{Code,FinalUrl,Headers,Body,Text()}` |
| `Net.NetworkConfig` | hosts/proxy/ads/headers 规则状态；`ContainOrMatch` |
| `Net.DohResolver` | `ResolveAsync(host, ct)` |
| `Models.*` | `Site/Style/VodClass/Filter/Vod/VodFlag/Episode/Parse/Result/UrlBean/Danmaku/Sub/Drm/Live/LiveGroup/LiveChannel/Catchup/Epg/EpgData/ConfigRecord/History/Keep/Device/Rule/Doh` |
| `Engine.VodConfigService` | `Instance.LoadAsync(cfg)/LoadLatestAsync()`；`Sites/Parses/Home/Parse/Flags/Wall`；`GetSite(key)/SetHome/SetParse/GetParses(type,flag)`；事件 `Loaded` |
| `Live.LiveConfigService` | `Instance.LoadAsync(cfg)`；`Lives/Home`；`GetChannels(live)`；事件 `Loaded` |
| `Live.LiveParser` | `Parse(live)` / `Text(live, text)` |
| `Live.EpgService` | `Instance.Get(channel)` → `Epg` |

## 2. Engine 模块（新增）

### 2.1 `Engine/Spider.cs` — 抽象基类（移植 catvod Spider.java）
```csharp
namespace FongMi.TV.Engine;
public abstract class Spider
{
    public Models.Site Site { get; set; }
    public virtual Task InitAsync(string ext) => Task.CompletedTask;
    public virtual Task<string> HomeContent(bool filter) => Task.FromResult("");
    public virtual Task<string> HomeVideoContent() => Task.FromResult("");
    public virtual Task<string> CategoryContent(string tid, string pg, bool filter, Dictionary<string, string> extend) => Task.FromResult("");
    public virtual Task<string> DetailContent(List<string> ids) => Task.FromResult("");
    public virtual Task<string> SearchContent(string key, bool quick) => Task.FromResult("");
    public virtual Task<string> SearchContent(string key, bool quick, string pg) => SearchContent(key, quick);
    public virtual Task<string> PlayerContent(string flag, string id, List<string> vipFlags) => Task.FromResult("");
    public virtual Task<string> LiveContent(string url) => Task.FromResult("");
    public virtual Task<string> Action(string action) => Task.FromResult("");
    /// <summary>本地代理回调：返回 [int code, string contentType, byte[]|string body] 或 null。</summary>
    public virtual Task<object[]> ProxyLocal(Dictionary<string, string> query) => Task.FromResult<object[]>(null);
    public virtual bool ManualVideoCheck() => false;
    public virtual Task<bool> IsVideoFormat(string url) => Task.FromResult(false);
    public virtual void Destroy() { }
}
public class SpiderNull : Spider { }
```

### 2.2 `Engine/SpiderLoader.cs`
```csharp
public class SpiderLoader
{
    public static SpiderLoader Instance { get; }
    public Task<Spider> GetSpider(Models.Site site);      // type3: .js→JsSpider；csp_/py→SpiderNull(记日志)
    public Task<Spider> GetLiveSpider(Models.Live live);   // live.api .js → JsSpider
    public Spider FindByKey(string key);                   // 供 /proxy?do=js&siteKey= 回调；找不到返回 null
    public void Clear();                                   // Destroy 所有实例
}
```
Jar(csp_)/Python spider 在 Windows 无法运行 dex/chaquopy：返回 `SpiderNull` 并 `Logger.E`，
SiteService 收到空结果时在 `Result.Msg` 注明「该站点使用 JAR/Python 爬虫，Windows 版暂不支持」。

### 2.3 `Engine/JsSpider.cs`（+ `Engine/Js/` 下辅助类）
Jint 实现 QuickJS spider 运行时，规格见 SPECS.md §QuickJS。要点：
- 每个 JsSpider 独立 Jint Engine，单线程调度（`JsSpider` 内部用 `SemaphoreSlim(1,1)` 序列化调用）。
- 支持 ES Module（`import`）与 `__jsEvalReturn` 两种约定；t4 `cat` 开头判定见规格。
- 全局 API：`req/reqs`（同步样式，内部走 HttpUtil）、`local`、`console`、MD5/AES/RSA、
  `pdfa/pdfh/pd/pdfl`（AngleSharp 实现 jsoup 规则）、`joinUrl`、`s2t/t2s`、`gzip/ungzip`、
  `js2Proxy`、`getProxy`（返回 LocalServer 代理地址）。
- 模块加载：http(s)、`assets://`（映射 Assets 目录）、相对路径（相对 api 所在 URL）、缓存到 `AppPaths.Js`。

### 2.4 `Engine/SiteService.cs`（移植 SiteViewModel.java）
```csharp
public static class SiteService
{
    public static Task<Models.Result> HomeContent(Models.Site site);
    public static Task<Models.Result> CategoryContent(Models.Site site, string tid, string pg, bool filter, Dictionary<string, string> extend);
    public static Task<Models.Result> DetailContent(Models.Site site, string vodId);
    public static Task<Models.Result> SearchContent(Models.Site site, string keyword, bool quick, string pg = "1");
    public static Task<Models.Result> PlayerContent(Models.Site site, string flag, string id);
    public static Task<Models.Result> Action(Models.Site site, string action);
}
```
type 0/1/3/4 分派规格见 SPECS.md §VodFlow。所有异常内部捕获 → `Result.Error(msg)`。
返回前一律 `result.Trans()`；detail 结果的 `Vod.Site = site`。

### 2.5 `Engine/SearchService.cs`
```csharp
public class SearchService
{
    /// <summary>并行搜索所有可搜站点；每站完成即回调（UI 线程）。keyword 自动繁→简。</summary>
    public static async Task SearchAll(string keyword, bool quick, Action<Models.Site, List<Models.Vod>> onSiteResult, CancellationToken ct);
}
```
并发度 = `Environment.ProcessorCount`，超时用 site.RequestTimeout，空结果不回调。

## 3. Server 模块（新增）

### 3.1 `Server/LocalServer.cs`
```csharp
namespace FongMi.TV.Server;
public class LocalServer
{
    public static LocalServer Instance { get; }
    public int Port { get; }                    // 9978 起探测至 9998
    public void Start(); public void Stop();
    public string GetAddress(string path);      // "http://127.0.0.1:{Port}" + path（path 以 / 开头）
    // UI 订阅的事件（在 App.Post 内已切 UI 线程）：
    public event Action<string> PushArrived;                 // do=push 的 url（点播推送/网址）
    public event Action<Models.ConfigRecord> RefreshConfig;  // do=refresh 配置刷新
    public event Action<string> DanmakuArrived;              // 弹幕推送 url/内容
    public event Action<Models.Sub> SubtitleArrived;         // 字幕推送
    public event Action<string, string> CastArrived;         // do=cast: (configUrl, historyJson)
}
```
端点规格见 SPECS.md §Server。实现 `HttpListener`，handler 分文件放 `Server/Process/`。
`/proxy?do=js&...` 回调 `SpiderLoader.Instance.FindByKey(siteKey).ProxyLocal(query)`。

## 4. Player 模块（新增）

### 4.1 `Player/PlayerCore.cs`
```csharp
namespace FongMi.TV.Player;
public class PlayerCore : IDisposable
{
    public FlyleafLib.MediaPlayer.Player Fly { get; }   // 供 FlyleafHost 绑定
    public static void StartEngine();                    // App 启动时调用一次：Engine.Start(FFmpegPath=ffmpeg 子目录)
    public static bool EngineReady { get; }              // FFmpeg dll 是否就绪
    public void Open(PlayItem item);
    public void Stop(); public void PlayPause(); public void SeekMs(long ms);
    public float Speed { get; set; }                     // 0.25~5
    public int Scale { get; set; }                       // 0原始 1拉伸 216:9 34:3 4填充
    public long PositionMs { get; } public long DurationMs { get; }
    public bool IsPlaying { get; }
    public event Action Opened; public event Action<string> Errored; public event Action Ended;
    public event Action<long> TimeChanged;               // 毫秒，约 250ms 一次（UI线程）
}
public class PlayItem
{
    public string Url; public Dictionary<string, string> Headers = new();
    public string Format;                                // MIME 提示，可空
    public List<Models.Sub> Subs = new(); public Models.Drm Drm;  // ClearKey 之外 Windows 不支持 → 提示
    public long StartPositionMs;
}
```

### 4.2 `Player/ParseJob.cs`（移植 ParseJob.java）
```csharp
public static class ParseJob
{
    /// <summary>对 web 播放页执行解析（parse type 0-4），返回真实媒体 URL 与 headers。失败抛异常。</summary>
    public static Task<ParseResult> Run(Models.Parse parse, string flag, string webUrl, CancellationToken ct);
}
public class ParseResult { public string Url; public Dictionary<string, string> Headers = new(); }
```
type0=web 嗅探(WebSniffer)、type1=json api、type2=json 扩展、type3=聚合、type4=god(并发所有 type0+嗅探)。规格见 SPECS.md。

### 4.3 `Player/WebSniffer.cs`
```csharp
public static class WebSniffer
{
    /// <summary>隐藏 WebView2 加载 url，拦截首个 Sniffer.IsVideoFormat 命中的请求。click 为注入脚本。</summary>
    public static Task<ParseResult> Sniff(string url, Dictionary<string, string> headers, string click, CancellationToken ct);
}
```
必须在 UI 线程创建 WebView2（`CoreWebView2Environment` 共享一个，UserDataFolder=AppPaths.Cache/webview）。
15 秒超时；拦截用 `WebResourceRequested`（过滤全部资源）。

### 4.4 `Player/DanmakuEngine.cs` + `Player/DanmakuView.cs`
```csharp
public class DanmakuEngine
{
    public Task LoadAsync(string urlOrText);   // B 站 XML / 纯文本每行 JSON 均可
    public List<DanmakuItem> Items { get; }
    public void Clear();
}
public class DanmakuItem { public long TimeMs; public string Text; public int Mode; public uint Color; }
/// DanmakuView : UserControl —— Canvas + Composition 动画滚动弹幕；
/// 公共方法 Bind(PlayerCore core, DanmakuEngine engine)、SetVisible(bool)、SetOpacity/FontScale/Speed/Area。
```

### 4.5 `Player/SubtitleLoader.cs`
外挂字幕（SRT/ASS/VTT）下载到本地缓存后交给 Flyleaf（`Fly.OpenAsync` 或 `Config.Subtitles`），
供播放页「字幕」菜单使用：`public static Task<string> Fetch(Models.Sub sub)` 返回本地路径。

## 5. UI 模块（新增，`UI/` 目录，命名空间 `FongMi.TV.UI`）

### 5.1 视觉规范（务必统一）
- `MainWindow`：`SystemBackdrop = MicaBackdrop`，`ExtendsContentIntoTitleBar = true`，自绘标题栏含 logo + 全局搜索框 + 配置切换按钮。
- 左侧 `NavigationView`（PaneDisplayMode=LeftCompact）：首页/点播/直播/搜索/收藏/历史/设置。
- 海报卡（`UI/Controls/PosterCard.xaml`）：`Width=150`，图 2:3 圆角 8，底部渐变遮罩标题+备注角标，
  Hover 时 1.05 缩放 + 阴影（`Microsoft.UI.Composition` 动画），`Style.IsList` 时列表布局。
- 颜色跟随系统主题；强调色用 `{ThemeResource SystemAccentColor}` 系列资源。
- 图片一律 `UI/Controls/PosterImage.cs`（带 Referer/UA 的异步加载 → 见 5.4 ImageLoader）。
- 页面标题 28px SemiBold，区块标题 18px SemiBold，统一 24px 页边距。

### 5.2 页面清单与职责
| 文件 | 职责 |
|---|---|
| `MainWindow.xaml(.cs)` | 壳：导航 + Frame + 标题栏 + 配置加载流程（无配置时引导页）+ LocalServer 事件接线（push→播放、cast→详情、refresh→重载） |
| `UI/Pages/HomePage` | 站点首页推荐网格 + 「继续观看」横排 + 「收藏」横排；站点切换按钮（换 Home 站点） |
| `UI/Pages/VodPage` | 分类浏览：顶部站点下拉 + 分类 Tab（`VodClass`）+ 筛选面板（Filters 展开）+ 无限滚动网格 |
| `UI/Pages/DetailPage` | 参数 `(Site, vodId)` 或推送 url；模糊海报 Hero + 信息 + 线路 Tab + 集数流式布局 + 收藏/换源搜索 |
| `UI/Pages/PlayerPage` | 参数 `PlaySession`（见 5.3）；FlyleafHost + 控制层（自动隐藏）+ 弹幕层 + 集数侧栏；快捷键：空格/←→/↑↓音量/F 全屏/Esc |
| `UI/Pages/SearchPage` | 关键字 + 逐站流式结果（按站分组）+ 搜索历史（Setting 存 JSON） |
| `UI/Pages/LivePage` | 三栏：直播源&分组 / 频道列表(含台标+当前节目) / 播放区(FlyleafHost)+EPG 面板；数字选台、上下频道；隐藏分组密码解锁 |
| `UI/Pages/HistoryPage` | 网格 + 进度条角标 + 右键删除/清空 |
| `UI/Pages/KeepPage` | 收藏网格 + 右键取消 |
| `UI/Pages/SettingsPage` | 分组卡片：配置管理(vod/live 地址+历史列表+刷新)、播放(解码提示/倍速默认/缩放/跳片头尾)、弹幕、网络(DoH 下拉/代理/UA)、隐私(无痕/清缓存)、关于(FFmpeg 状态检测+指引) |

### 5.3 `UI/PlaySession.cs` — 播放会话（页面间传参与换源状态机）
```csharp
namespace FongMi.TV.UI;
public class PlaySession
{
    public Models.Site Site; public Models.Vod Vod;
    public List<Models.VodFlag> Flags; public int FlagIndex; public int EpisodeIndex;
    public Models.History History;                      // 进度/倍速/片头尾
    public static PlaySession FromDetail(Models.Site site, Models.Vod vod, int flag, int ep);
    public Models.VodFlag CurrentFlag { get; } public Models.Episode CurrentEpisode { get; }
}
```
PlayerPage 内的「播放地址解析流水线」（关键复用逻辑，放 `UI/PlayResolver.cs`）：
```csharp
public static class PlayResolver
{
    /// <summary>SiteService.PlayerContent → NeedParse?→ParseJob → 产出可播 PlayItem（含 danmaku/subs）。</summary>
    public static Task<Player.PlayItem> Resolve(Models.Site site, string flag, Models.Episode ep, CancellationToken ct);
    /// <summary>直播频道 → PlayItem（含 header/format/catchup 无关）。</summary>
    public static Task<Player.PlayItem> ResolveLive(Models.LiveChannel channel, CancellationToken ct);
}
```

### 5.4 `UI/ImageLoader.cs`
`vod_pic` 可能带 `@Referer=`/`@User-Agent=` 后缀或需配置 header：
`public static async Task<BitmapImage> Load(string pic)`；内存 LRU(256) + 磁盘缓存(AppPaths.Cache/img)；
失败返回占位图。`PosterImage` 控件封装（`Source` 字符串依赖属性）。

### 5.5 导航参数
`Frame.Navigate(typeof(DetailPage), new DetailArgs { SiteKey, VodId, Name })` —— 参数类都放 `UI/NavArgs.cs`：
```csharp
public class DetailArgs { public string SiteKey; public string VodId; public string Name; public string PushUrl; }
public class PlayerArgs { public PlaySession Session; }
```

## 6. csproj 包基线（by 集成负责人，勿改版本）
```xml
<PackageReference Include="Microsoft.WindowsAppSDK" Version="1.8.251003001" />
<PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.1742" />
<PackageReference Include="Jint" Version="4.14.0" />
<PackageReference Include="AngleSharp" Version="1.5.2" />
<PackageReference Include="FlyleafLib" Version="3.10.4" />
<PackageReference Include="FlyleafLib.Controls.WinUI" Version="1.3.4" />
```
FFmpeg 原生 DLL 不入库：`PlayerCore.StartEngine()` 探测 `{BaseDir}/ffmpeg/` 与 `{AppPaths.Root}/ffmpeg/`，
缺失时 `EngineReady=false`，设置页/播放页显示下载指引（不自动下载）。

## 7. 兼容性矩阵（对用户可见行为）
| Android 能力 | Windows 方案 |
|---|---|
| ExoPlayer+FFmpeg | FlyleafLib(FFmpeg)；硬解 D3D11VA 自动 |
| JS 爬虫(QuickJS) | Jint + 全套全局 API |
| JAR 爬虫(dex) | 不可运行 → SpiderNull + 站点标注「暂不支持」 |
| Python 爬虫 | 同上 |
| WebView 嗅探 | WebView2 |
| 弹幕 DanmakuFlameMaster | 自研 Composition 渲染 |
| 画中画 | AppWindow CompactOverlay |
| DLNA DMR/DMC | 本期实现 FongMi 局域网投屏协议（/device+cast）；UPnP 后续 |
| TVBus/ForceTech/Thunder | 原生库仅 Android → 提示不支持 |
| 遥控器 | 键盘快捷键 + 遥控端点兼容 |
