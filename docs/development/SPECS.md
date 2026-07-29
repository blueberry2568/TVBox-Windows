# 移植规格（从 Android 源码自动提取，编码前必读）


---

<!-- SECTION: Server -->

# FongMi.TV 本地 HTTP 服务器协议规格（C# 移植用）

源码：`app/src/main/java/com/fongmi/android/tv/server/`（Nano.java, Server.java, process/*, impl/Process.java）、`docs/LOCAL.md`、`app/src/mobile/java/com/fongmi/android/tv/utils/ScanTask.java`、`catvod/.../com/github/catvod/Proxy.java`

## 1. 端口策略与 /device

- 端口从 **9978 依次尝试到 9998**（`for i=9978; i<9999`），第一个绑定成功的端口即生效，并写入全局 `Proxy.set(port)`（`Proxy.getUrl(local)` = `http://{127.0.0.1|局域网IP}:{port}/proxy`）。
- 服务器地址：`Server.getAddress(local)` = `http://{127.0.0.1|Util.getIp()}:{port}`。
- 所有端点 GET/POST 均可（Nano 在 POST 时先 parseBody；multipart 的 content-type 强制补 `; charset=utf-8`）。默认响应 `text/plain`，成功 `OK`，异常 `500 + 错误消息`。未匹配任何处理器的路径回退到内置 assets 静态文件（`/` → index.html，404 返回空 HTML）。
- `GET /device` → `text/plain`，内容为 Device JSON：
```json
{"uuid":"AndroidID","name":"设备名","ip":"http://192.168.x.x:9978","type":0,"serial":"...","eth":"MAC","wlan":"MAC","time":1690000000000}
```
  注意 **ip 字段含 `http://` 前缀与端口**。type：0=电视(leanback) 1=手机(mobile) 2=DLNA。db 中仅存 id/uuid/name/ip/type；serial/eth/wlan/time 是响应附加字段。
- `GET /tvbus` → `LiveConfig.getResp()`（直播 tvbus 配置 JSON）。

## 2. 端点总览

| 方法 | 路径 | 参数 | 响应 |
|---|---|---|---|
| GET/POST | `/action?do=...` | 见下 | 恒为 `200 OK`（"OK"），do 为空也返回 OK |
| GET/POST | `/cache?do=get/set/del` | key,value,rule | get 返回存储值(空则"")；其余 "OK" |
| GET | `/media` | — | 播放状态 JSON（text/plain 装 JSON） |
| GET | `/file/{相对路径}` | Range/If-None-Match/If-Range 头 | 目录=JSON 列表；文件=流(206/304/416/ETag) |
| POST | `/upload?path=` | multipart 文件 | "OK"；.zip 自动解压到 path，其他复制到 path/文件名 |
| GET | `/newFolder?path=&name=` | — | "OK"，mkdirs |
| GET | `/delFolder?path=` / `/delFile?path=` | — | "OK"，递归删除（两端点实现相同：`Path.clear`） |
| GET | `/parse?jxs=&url=` | — | text/html，`String.format(parse.html, jxs, url)` |
| GET/POST | `/proxy?...` | 任意 | 由爬虫 proxy() 决定 |
| GET | `/device` | — | 设备 JSON |
| GET | `/tvbus` | — | 直播配置文本 |

### /action 的 do= 子命令（Action.java；全部只触发事件后立即返回 "OK"）
| do | 参数 | 语义 |
|---|---|---|
| `control` | `type`=play/pause/stop/prev/next/repeat/replay | 控制 PlaybackService 播放器 |
| `danmaku` | `text` | 向当前播放器实时发送一条弹幕 `player.sendDanmaku(text)` |
| `refresh` | `type`=live/detail/player/category（无参）；`subtitle`/`danmaku` 带 `path`(URL)；`vod` 带 `json`(Vod JSON) | 发 RefreshEvent 刷新对应页面 / 注入字幕、弹幕 / 更新 Vod |
| `push` | `url` | ServerEvent.push(url)，推送 URL 播放 |
| `file` | `path`(本地路径) | `.apk`→安装；`.srt/.ssa/.ass`→RefreshEvent.subtitle(path)；其他→ServerEvent.setting(path) 当配置载入 |
| `search` | `word` | 触发界面关键字搜索 |
| `setting` | `text`(配置内容或URL), `name`(可选显示名) | ServerEvent.setting 载入配置 |
| `cast` | `config`(Config JSON), `device`(Device JSON), `history`(History JSON) | 接收端：CastEvent.post(Config.find(config), device, history)，本机开始播放该历史条目 |
| `sync` | 见 §5 | 多设备同步 |

注：**没有 `/newconfig` 端点**（docs 与源码均无；只有 `/newFolder`）。`do=subtitle` 单独不存在，字幕通过 `do=refresh&type=subtitle` 或 `do=file` 注入。

## 3. /proxy 转发规则（Proxy.java + BaseLoader.proxy）

请求参数合并：`params = queryString ∪ 全部请求头(小写键) ∪ POST body files`，整体传给 `BaseLoader.get().proxy(params)`。路由：
1. 含 `siteKey` → `getSpider(siteKey).proxy(params)`（定向到该站点爬虫，JS/PY/JAR 均可）。
2. `do=js` → JsLoader：转给**最近使用**的 JS spider 的 proxy()；无 recent 返回 null→500。
3. `do=py` → PyLoader：同上，最近使用的 Python spider。
4. 其他（含 `do=local` 等任意值/缺省）→ JarLoader.proxy：先调 recent jar 的静态 `Proxy.proxy(Map)` 方法，返回 null 则遍历其他已加载 jar 的 proxy 方法取第一个非 null。**"do=local" 无专门分支**，由 jar 内自行处理。

返回值 `Object[] rs` 的解释（createResponse）：
- `rs[0] instanceof NanoHTTPD.Response` → 直接返回；
- 否则需 `rs.length>=3`：`rs[0]`=int 状态码（非标准码 100-599 也允许），`rs[1]`=Content-Type 字符串，`rs[2]`=InputStream（**chunked 响应**），可选 `rs[3]`=Map<String,String> 附加响应头。
- null/长度不足 → 500 "Invalid proxy response"。
- **服务器本身不做任何 m3u8 处理**；m3u8 广告过滤/重写在爬虫或播放器层。

## 4. /file、/cache、/upload 行为细节

- `/file/{path}`：path=URL 去掉前 5 字符 `/file`，相对 `Path.root()`（应用根目录）。目录→JSON `{"parent":..., "files":[{name,path,time,dir}]}`；path 为相对根路径（以根为前缀截断，**带前导 `/`**，如 `/videos/a.mp4`）；time 格式 `yyyy/MM/dd HH:mm:ss`；dir 1/0。parent："." 表示已是根，"" 表示上层即根，否则相对路径。文件→按扩展名 MIME 流式返回：ETag = CRC32(绝对路径+lastModified+length) 的 16 进制；支持 `If-None-Match`(*或相等→304)、`If-Range`(不匹配 ETag 则忽略 Range)、单段 `Range: bytes=a-b`（越界→416 + `Content-Range: bytes */len`；部分→206 + Content-Range），响应带 Content-Length / Accept-Ranges: bytes / ETag。
- `/cache`：SharedPreferences 键值；键 = `"cache_" + (rule为空?"":rule+"_") + key`。do=get 返回字符串值；set 写 `value`；del 删除。总是 200。
- `/upload?path=`：遍历 multipart files（NanoHTTPD 将临时文件路径放 files map，原始文件名在 params 同名键）；文件名 .zip（大小写不敏感）→ 解压到 `root/path`，否则复制为 `root/path/文件名`。

## 5. 多设备同步协议（do=sync）

`POST /action?do=sync&type={history|keep}&mode={0|1|2}&force={true|...}[&device={DeviceJSON}]`，Body 为 form-urlencoded。

- **mode 语义**：`0`=双向（先发送后接收，默认）；`1`=仅接收（处理本请求 body 中的 targets）；`2`=仅发送（向 device 回发自己的数据）。force=true 时接收方先删本地再合并，否则直接合并。
- **发送**（mode 0/2 且带 device 参数）：向 `“{device.ip}/action?do=sync&mode=0&type={type}”` POST FormBody：
  - history：`config`=Config JSON（请求里的 config 经 Config.find 解析，url 为空则用当前 Config.vod()）、`targets`=`History[]` JSON（当前配置 cid 下全部历史）。
  - keep：`targets`=`Keep[]` JSON（全部点播收藏）、`configs`=`Config[]` JSON（Config.findUrls()）。
  - 注意回发用 `mode=0`，对端因不带 device 参数而只会走接收分支，不会无限回弹。
- **接收**（mode 0/1）：
  - history：解析 `config`（url 为空则丢弃不处理）；若 config.url == 当前 VodConfig.url：force→History.delete(cid)，然后 History.sync(targets)、发 history 刷新事件；否则先 VodConfig.load(config) 成功后再执行同样合并。
  - keep：解析 `targets`(Keep[]) 与 `configs`(Config[])；若本机无 VodConfig 且 configs 非空，先加载 configs[0] 再合并；合并 = (force→Keep.deleteAll()) + Keep.sync(configs, targets) + keep 刷新事件。
- JSON 形状：History{key,vodPic,vodName,vodFlag,vodRemarks,episodeUrl,revSort,revPlay,createTime,opening,ending,position,duration,speed,scale,cid}；Keep{key,siteName,vodName,vodPic,createTime,type,cid}；Config{id,type,time,url,json,name,logo,home,parse,notice,danmaku}。

## 6. 设备发现 / cast / 实时通道

- **无 WebSocket、无 SSE**。全部是 HTTP 轮询/单发。
- 设备发现（ScanTask.java，mobile flavor）：取本机地址前缀，对 `x.x.x.1..255:9978`（**固定 9978**，不扫其他端口）并发 GET `/device`，1 秒超时，解析成功即回调 `device.save()`。也支持手动输入 url 直连。DLNA 设备另经 jUPnP 发现（type=2）。
- 投屏流程：发起端把 `Config`/`Device`(自己)/`History` JSON 作为 query 发 GET `目标ip/action?do=cast&...`；接收端 CastEvent 触发本地播放。
- `/media` 状态（Media.java，与 docs 有出入，**以代码为准**）：播放器未运行或已释放 → `{}`；否则 `{state,speed,duration,position,url,title,artist,artwork}`；state：3=播放中，6=缓冲，2=READY 暂停，1=其他（docs 写 1=缓冲/2=暂停/3=播放，代码实际 6=缓冲）。speed float、duration/position 毫秒 long（无则 -1 由播放器返回），字符串字段无则 ""。

### C# 移植要点
- 处理器按注册顺序匹配 `url.startsWith(prefix)`：Action→Cache→Local→Media→Parse→Proxy；`/tvbus`、`/device` 在链前硬编码。
- `/action` 永远回 200 OK（即使参数缺失/内部出错也不报错）；`/media` 用 UI 线程取播放器状态（超时/异常回 `{}`）。
- `/parse` 从 assets 读 `parse.html` 做 `String.format(html, jxs, url)`（%s 两个占位）。

---

<!-- SECTION: VodFlow -->

# FongMi TV 站点/解析/播放 行为规格（源自 Android 源码）

源文件：`app/src/main/java/com/fongmi/android/tv/api/SiteApi.java`、`model/SiteViewModel.java`、`player/parse/ParseJob.java`、`player/PlayerManager.java`、`api/config/VodConfig.java`、`bean/Result.java`、`bean/Parse.java`、`docs/SPIDER.md`

## 1. type 0/1 站点（内建 XML/JSON API）

通用请求规则（SiteApi.call）：
- 若 site.ext 非空，加参数 `extend=<ext>`；ext 长度 ≤1000 用 GET query，>1000 改为 POST form body。请求带 site.header。
- `ac` 参数值：type0 → `videolist`；type1/4 → `detail`。

| 操作 | 请求 | 备注 |
|---|---|---|
| home | GET site.api（无参数，带 site.header） | `Result.fromType(type, body)`：type0 走 XML(simpleframework, root=`rss`, `class/ty`、`list/video`)，其余走 JSON gson。之后 fetchPic + setTypes |
| category | `ac`、`t=tid`、`pg=page`；type1 且 extend 非空时加 `f=<extend的JSON>` | filter 布尔本身不上送（仅 spider 用） |
| detail | `ac`、`ids=<id>` | 返回后对 vod.setFlags() 执行 Source.get().parse()（extractor 预处理） |
| search | `wd=keyword`、`quick=true/false`、`extend=`(空串)；page!="1" 时加 `pg=page` | 关键词先 Trans.t2s 繁转简；quick 且 site.quickSearch!=1 直接返回空；结果 fetchPic，每个 vod.setSite(site) |

- **fetchPic**（仅 type≤2、列表非空且首项无 pic 时）：收集 id（若 site.categories 非空则只留 typeName 在 categories 内的项），二次请求 `ac=<videolist|detail>&ids=id1,id2,...` 用详情结果替换列表；ids 为空则清空列表。
- **filters 来源**：homeContent 返回 JSON 的 `filters` 字段（key=type_id → Filter 数组）；setTypes 把 filters 挂到对应 Class 上；site.categories 非空时按其顺序/白名单过滤重排分类。
- **XML(type0) 与 JSON(type1) 差异**：仅解析层不同——XML `rss>class>ty`、`rss>list>video` 映射到同一 Result；XML 无 filters/pagecount 等扩展字段。type0 category 用 `ac=videolist`，type1 用 `ac=detail`，且 filter 条件仅 type1 支持（`f=`）。

## 2. type 4 站点
- home：先 `site.fetchExt()`（ext 以 http 开头则 GET 拉取内容替换 ext），请求参数 `filter=true`（+extend），JSON 解析，setTypes。
- category：`ac=detail`、`t`、`pg`、`ext=Base64URLSafe(JSON(extend))`（+extend 参数）。
- detail：同 type1（`ac=detail&ids=`）。
- search：同 type1。
- player：`play=<id>&flag=<flag>`（+extend），返回 JSON Result；flag 为空则回填入参 flag；`result.setUrl(Source.fetch(result))`（extractor：如磁力/网盘）；header 置 site.header（仅当 result 无 header，见 §5）。
- action：type3 调 spider.action(str)，type4 直接 `OkHttp.string(action)` 把 action 当 URL GET；其余类型返回空。

## 3. playerContent 产出
- **type 3**：`spider.playerContent(flag, id, VodConfig.flags)` 返回 JSON Result；flag 空回填；url 过 Source.fetch；header 补 site.header；setKey(key)。
- **type 4**：见上。
- **type 0/1（及其它）**：不发请求。构造 Result：url=id、flag=flag、header=site.header、playUrl=site.playUrl；`parse = (Sniffer.isVideoFormat(id) && playUrl为空) ? 0 : 1`；url 过 Source.fetch。即：裸视频直链直接播，否则需解析。
- **push_agent**（site 为空且 key=push_agent）：url=id、parse=0。
- **标志语义**：`parse==1 || jx==1` → needParse，进入 ParseJob；`isUseParse()` = 配置有 parses 且（playUrl 为空且全局 `flags` 含 result.flag，或 jx==1）——决定是否用配置默认解析器。`playUrl` 前缀：`json:<url>`→临时 type1 解析器；`parse:<name>`→按名取配置解析器；其他→作为解析 URL 前缀（getRealUrl=playUrl+url）。`flag` 用于：spider 入参、解析器 ext.flag 匹配（getParses(type,flag)）、jsonExtMix 入参。`jxFrom` 强制显示解析器来源名。
- PlayerManager.parse(): spec=PlaySpec.fromParse(result...)（携带 format/drm/subs/danmaku），ParseJob 成功回调 onParseSuccess(headers,url,from)：移除 Range 头 → spec.setHeaders/setUrl → 播放；失败 onParseError → 报"解析失败"。播放超时（TIMEOUT_PLAY）报超时错误；解码错误 retry 一次（软硬解切换 toggleDecode），第二次报错。

## 4. ParseJob 解析算法
选择解析器 setParse(result, useParse)：useParse→VodConfig 当前 parse；playUrl 以 `json:` 开头→Parse(type=1, url=去前缀)；`parse:` 开头→按名查；仍空→Parse(type=0, url=playUrl)。然后 `parse.setHeader(result.getHeader())`（仅当 parse 自身 ext.header 为空时生效）、click = site.click 优先，否则 result.click。单线程执行 + TIMEOUT_PARSE_DEF 总超时（超时 cancel → onParseError）。按 parse.type 分派：
- **type 1（json）**：GET `parse.url + webUrl`（带 parse.header）→ JSON；取顶层 `url`，为空再取 `data.url`；headers 从响应 JSON 顶层键中提取 user-agent/referer/cookie/ua（大小写不敏感，fixHeader 规范化），一个都没有则用 parse.header。成功判定 `url.length() > 40`，否则（fatal 时）失败。
- **type 0（web 嗅探）**：WebView 加载 `parse.url + webUrl`，带 parse.header 与 click 脚本，拦截网络请求嗅探视频 URL（Sniffer/manualVideoCheck）；无 WebView 支持直接失败。
- **type 2（jsonExtend 聚合）**：收集配置中所有 type1 解析器 {name→extUrl}（extUrl=在url的?后插入 `cat_ext=Base64URLSafe(ext)`），调 `BaseLoader.jsonExt(parse.url, jxs, webUrl)`（JS 引擎聚合）→ Result；结果 header=parse.header；若结果仍 needParse → 转 web 嗅探该 url，否则成功（from=jxFrom）。
- **type 3（jsonMix 混合）**：收集全部解析器 {name→{type,ext,url}}，调 `BaseLoader.jsonExtMix(flag, parse.url, parse.name, jxs, webUrl)`；结果处理同 type2。
- **type 4（God/超级解析，parses 非空时自动插入为第 0 项 Parse.god()）**：json = getParses(1, flag)，webs = getParses(0, flag)（先按 ext.flag 含 flag 过滤，过滤后为空则用全量）；所有 json 解析器并发 jsonParse(fatal=false)，webs 合并成一个本地服务器聚合页 `http://127.0.0.1:port/parse?jxs=url1;url2&url=webUrl` 用 WebView 嗅探；首个成功者胜（AtomicBoolean done + CAS）；latch 全部结束仍无结果 → onParseError。

## 5. 播放 header 优先级
`Result.setHeader` 只在现有 header 为空时写入 ⇒ 优先级：
1. spider/type4 playerContent 返回 JSON 的 `header` 字段（最高）
2. site.header（配置 sites[].header；SiteApi 兜底填入）
3. 走解析时：json 解析器响应内的 ua/user-agent/referer/cookie 键 > parse.ext.header > result.header（经 parse.setHeader 链传入）
4. PlayerManager 收到后删除 `Range` 头再交给引擎。
（全局 config `headers` 数组作用于 OkHttp 层请求 hosts 匹配，非播放 header。）

## 6. VodConfig.parseConfig 字段清单（易漏项）
顶层：`spider`（全局 jar，Site 未给 jar 时继承）、`sites`、`parses`、`flags`（VIP 平台旗标）、`headers`（全局请求头规则）、`proxy`、`rules`、`doh`、`hosts`、`ads`、`wallpaper`（另存 WALL 配置并同步）、`lives`（拆出存 LIVE 配置）、`logo`、`notice`、`danmaku`、`urls`（Depot 多仓：取第一个递归加载）、`msg`（存在即抛错显示）。
- parses 非空时在头部插入 Parse.god()（type=4）；默认 parse 按 config.parse 名匹配否则第 0 个；home 站按 config.home 匹配否则第 0 个；sites 去重（distinct）并与数据库已存 Site 同步（sync 记忆 searchable/changeable 等用户覆写）。

## 7. Spider 方法签名表（SPIDER.md）
| 方法 | 签名 | 返回 |
|---|---|---|
| init | `void init(Context, String extend)` | — |
| homeContent | `String homeContent(boolean filter)` | Result JSON：`class`[+`filters`] |
| homeVideoContent | `String homeVideoContent()` | Result：`list` |
| categoryContent | `String categoryContent(String tid, String pg, boolean filter, HashMap<String,String> extend)` | Result：`list`[+`pagecount`] |
| detailContent | `String detailContent(List<String> ids)` | Result：`list`（1个完整 Vod） |
| searchContent | `String searchContent(String key, boolean quick)` / `(String key, boolean quick, String pg)` | Result：`list`（page=1 时调两参版本） |
| playerContent | `String playerContent(String flag, String id, List<String> vipFlags)` | 播放 Result（下述字段） |
| liveContent | `String liveContent(String url)` | 原始文本（TXT/M3U/JSON） |
| proxy | `Object[] proxy(Map<String,String> params)` | `{code, mime, InputStream[, headersMap]}` |
| action | `String action(String action)` | Result JSON |
| manualVideoCheck / isVideoFormat | `boolean manualVideoCheck()` / `boolean isVideoFormat(String url)` | 嗅探人工判定 |
| destroy | `void destroy()` | — |

playerContent Result 字段：`url`(必)、`parse`(0直接/1需解析)、`jx`(同parse=1)、`playUrl`(json:/parse:/前缀)、`header`、`flag`(覆盖)、`jxFrom`、`format`(MIME，跳过探测)、`click`、`code`(非0抑制msg)、`danmaku[]{url,name}`、`subs[]{url,name,lang,format,flag}`、`drm{type,key,header,forceKey}`、`artwork`、`desc`、`position`(ms)、`lrc`；通用 `msg`。
Vod 关键字段：`vod_id/vod_name/vod_pic/vod_remarks/type_name/vod_play_from/vod_play_url`、`vod_tag:"folder"`（vod_id 作 tid 再调 categoryContent）、`action`、`cate/land/circle/ratio/style`。Class：`type_id`(可缩写`id`)/`type_name`(可缩写`name`)/`type_flag:"1"`=文件夹/land/circle/ratio。Filter：`key/name/init/value[{n,v}]`。
集数编码：`$$$` 分源、`#` 分集、`$` 分名称与URL；集数 value 即 playerContent 的 `id`。
proxy 协议：媒体 URL 用 `proxy://`（py: `proxy://?do=py`，js: `proxy://?do=js`）转发到本地代理，由 Spider.proxy() 处理。

---

<!-- SECTION: QuickJS -->

# FongMi QuickJS 爬虫引擎 API 规格（移植到 C# Jint 用）

源码根：`TV-fongmi/quickjs/src/main/java/com/fongmi/quickjs/`，内置 JS 库：`quickjs/src/main/assets/js/lib/`（注意：不在 app 模块！app/src/main/assets/js/ 下只有 `jquery.min.js`、`script.js`，供 WebView 嗅探用，与爬虫无关）。

## 1. 生命周期与调用约定（Spider.java）

- 每个 Spider 一个独立 JS 上下文 + **单线程 executor**：所有 JS 调用都串行提交到该线程。
- **初始化顺序**（`initializeJS`）：
  1. 创建 ctx，设 Console（console.log/info/warn/error → 日志）。
  2. `evaluate(js/lib/http.js)`（非 module，定义全局 `req`/`http` 包装及 `global/window/self` → globalThis 别名）。
  3. 注册全局对象 `local`（见 §3）与 Global 全局函数（见 §2）。
  4. 设置 module loader：`moduleNormalizeName(base, name) = UriUtil.resolve(base, name)`（URL 相对解析）；模块源码经 `Module.get().fetch(name)` 获取后编译。
  5. 尝试 dex 加载 `com.github.catvod.js.Function`（外部 jar 注入的额外全局函数，如 pdfa/pdfh/pd/pdfl/gzip/ungzip/reqs 等——**不在本仓库内**，由站点 jar 提供，失败静默忽略；C# 版可自行内置这些常用函数）。
- **加载 spider 脚本**（`createObj`）——统一按 ES module 处理：
  1. `content = Module.fetch(api)`；`cat = content.contains("__jsEvalReturn")`（**t4/cat 判定：源码文本包含 `__jsEvalReturn` 字符串**）。
  2. 把源码中 `__JS_SPIDER__` 字面量替换为 `globalThis.__JS_SPIDER__`，以 module 方式 evaluate（模块名 = api URL）。
  3. evaluate `js/lib/spider.js`（用 `String.format` 将 `%s` 替换为 api URL）：
     ```js
     import * as spider from '%s'
     if (!globalThis.__JS_SPIDER__) {
       if (spider.__jsEvalReturn) { globalThis.req = http; globalThis.__JS_SPIDER__ = spider.__jsEvalReturn() }
       else if (spider.default) { globalThis.__JS_SPIDER__ = typeof spider.default === 'function' ? spider.default() : spider.default }
     }
     ```
     即三种形态：a) 脚本自己给 `__JS_SPIDER__` 赋值；b) cat 风格 `export function __jsEvalReturn(){return {init,home,...}}`（此时全局 `req` 被替换为可 async 的 `http`）；c) `export default {…}` 或 `export default ()=>({…})`。
  4. `jsObject = globalThis.__JS_SPIDER__`（方法容器对象）。
- **方法调用**：`call(func, args)` = 取 `jsObject[func]`，不存在则返回 null；调用后若返回值是 Promise（有 `then`），走 then/catch 取结果（Async.java）——**所有方法均支持同步或 async**。
- **方法表**（Java 接口 → JS 方法名与参数）：
  | Java | JS 调用 | 备注 |
  |---|---|---|
  | init(ctx, extend) | `init(ext)` | 非 cat：ext 为 JSON 对象则 parse 成对象，否则原字符串。cat：包成 `{stype:3, skey:<siteKey>, ext:<对象或字符串>}` |
  | homeContent(filter) | `home(filter:bool)` → JSON 字符串 |
  | homeVideoContent() | `homeVod()` |
  | categoryContent | `category(tid, pg, filter:bool, extend:obj)` | extend 为 string→string 映射对象 |
  | detailContent(ids) | `detail(ids[0]:string)` |
  | searchContent | `search(key, quick)` 或 `search(key, quick, pg)` |
  | playerContent | `play(flag, id, vipFlags:string[])` |
  | liveContent(url) | `live(url)` |
  | manualVideoCheck | `sniffer()` → bool |
  | isVideoFormat(url) | `isVideo(url)` → bool |
  | action(action) | `action(action)` |
  | destroy() | `destroy()` 后释放 ctx |
  | proxy(params) | 见 §5 |

## 2. 全局 API（Global.java + http.js）

| 函数 | 签名 | 行为 |
|---|---|---|
| `s2t(text)` | str→str | 简→繁 |
| `t2s(text)` | str→str | 繁→简 |
| `getPort()` | →int | 本地服务器端口 |
| `getProxy(local)` | bool→str | `http://{local?127.0.0.1:局域网IP}:{port}/proxy?do=js` |
| `js2Proxy(dynamic, siteType, siteKey, url, headers)` | (bool,int,str,str,obj)→str | `getProxy(!dynamic) + "&from=catvod&siteType={t}&siteKey={k}&header={urlencode(JSON(headers))}&url={urlencode(url)}"` |
| `setTimeout(fn, delay)` | →int(id) | 定时器，回调投递到 spider 单线程；返回 0 表示失败 |
| `clearTimeout(id)` | →null | |
| `_http(url, options)` | | options 含 `complete` 回调则异步（完成时 `complete(res)`），否则同步等价 `req` |
| `req(url, options)` | →resObj | 同步 HTTP（见下）。失败返回 `{code:"", content:"", headers:{}}` |
| `joinUrl(parent, child)` | →str | URL 相对解析（同 URI.resolve 语义） |
| `md5X(text)` | →str | MD5 hex |
| `aesX(mode, encrypt, input, inBase64, key, iv, outBase64)` | →str | mode 如 `"AES/CBC/PKCS7"`（Java 侧拼 `mode+"Padding"`）；key/iv 不足 16 字节补零；iv 为 null 时不用 IV；inBase64 时先把 `_→/ -→+` 再解码；失败返回 `""` |
| `rsaX(mode, pub, encrypt, input, inBase64, key, outBase64)` | →str | mode `"RSA/PKCS1"`→PKCS1Padding、`"RSA/None/NoPadding"`；key 为 PEM（自动剥壳）；pub=X509 公钥 / PKCS8 私钥 |

**http.js 包装**（普通脚本预加载）：`req(url,options)` = `http(url,{async:false,...options})` 同步；`http(url,options)` 默认返回 Promise（内部走 `_http` + complete 回调），失败 resolve `{ok:false,status:500,url}`。cat spider 加载后全局 `req` 被 `http`（Promise 版）覆盖。

**req 的 options 字段**（Req.java，经 JSON 序列化解析）：
- `method`：`get`(默认)/`post`/`header`(=HEAD)
- `headers`：对象（string→string）
- `timeout`：ms，默认 10000
- `redirect`：1=跟随（默认），其他不跟随
- `buffer`：0=文本（按响应头 Content-Type 的 charset，默认 UTF-8）；1=字节数组转 JS int 数组；2=base64 字符串；3=原始 byte[]
- `postType`：`json`(默认，data 作 JSON body)/`form`(urlencoded)/`form-data`(multipart)，均取 `data` 字段（对象）
- `body`：字符串原始 body（当 data 为空且 headers 有 Content-Type 时使用）
- `data`：任意 JSON（配合 postType）

**返回结构**：`{code:int, headers:{k:v或[v,...](多值)}, content:按buffer}`。错误时 `{code:"", headers:{}, content:""}`。

## 3. local API（Local.java）

全局对象 `local`，三个方法，实际键 = `"cache_" + (rule ? rule + "_" : "") + key`，存 SharedPreferences（C# 用键值持久化存储即可）：
- `local.get(rule, key)` → string（无则空/null）
- `local.set(rule, key, value:string)`
- `local.delete(rule, key)`

## 4. 模块解析（Module.java + moduleNormalizeName）

- import 说明符规范化：`UriUtil.resolve(当前模块名, import路径)` —— 相对路径按 URL 解析（spider 的模块名就是其 http URL，故 `./util.js` 解析为同目录 URL）。
- 取源码 `Module.fetch(name)`，LRU 缓存 50 条（键=完整名）：
  - `http...` 开头 → HTTP GET 下载
  - `assets...` 开头 → 读 APK assets（如 `assets://js/lib/cat.js`）
  - `lib/...` 开头 → 读 assets `js/lib/...`（**内置库**：`cat.js`(cheerio+dayjs+CryptoJS+jinja2+jsonpath 打包，导出 Crypto,Uri,_,cheerio,contains,dayjs,html,jinja2,jp,jpo,load,merge,parseHTML,root,text,xml)、`cheerio.min.js`、`crypto-js.js`、`gbk.js`、`similarity.js`；另有非模块的 `http.js` 与加载器 `spider.js`）
  - 其他前缀 → null（失败）

## 5. Proxy 机制

- **URL 生成**：`js2Proxy` 生成 `http://{ip}:{port}/proxy?do=js&from=catvod&siteType=..&siteKey=..&header=..&url=..`（dynamic=true 用局域网 IP，false 用 127.0.0.1）。t4 spider 也可自行用 `getProxy()` 拼任意参数。
- **服务器回调**（Nano `/proxy` → BaseLoader.proxy(params)，params = query 参数 + 请求头合并）：
  - 有 `siteKey` 参数 → 直接找该站点 spider 调用；否则 `do=js` → 最近使用的 js spider（`setRecent` 记录）。
- **Spider.proxy(params) 两条路径**（按 `from` 参数分流）：
  - **proxy1（普通，from≠catvod）**：`proxy(paramsObj)` 传入全部参数对象，JS 返回**数组** `[code:int, contentType:str, body, headers?:jsonstr, base64?:int]`；body 若为 byte[] 直接流；否则字符串——第 5 元素为 1 时按 base64 解码（含 `base64,` 前缀则先截掉）。Java 侧返回 `[code, contentType, stream, headersMap]`。
  - **proxy2（catvod，from=catvod）**：`proxy(urlSegments:string[], headerObj)`，其中 urlSegments = `params["url"].split("/")`，headerObj = `JSON.parse(params["header"])`；JS 返回 **JSON 字符串**，按 Res.java 解析：`{code(默认200), buffer(2=content是base64), content, headers}`，contentType 取 headers 的 Content-Type（默认 `application/octet-stream`）。Java 侧返回 `[code, contentType, stream]`。
- **HTTP 响应组装**（server/process/Proxy.java）：`newChunkedResponse(status, contentType, stream)`，若有第 4 元素 headers 逐个 addHeader；rs 为 null/空 → 500 错误。

## 移植要点提醒

- 所有 spider 方法可能返回 Promise，需统一 await。
- 单线程约束：Jint Engine 非线程安全，同样需要每 spider 一个专属调度线程（setTimeout/_http 回调也投递到该线程）。
- ES module 支持是硬需求（spider.js 加载器、lib/cat.js import）；Jint 需启用 modules 并实现自定义 ModuleLoader 复刻 §4 解析规则。
- `cat` 判定影响 init 的 ext 编组（`{stype:3, skey, ext}`）和全局 req 的替换。
