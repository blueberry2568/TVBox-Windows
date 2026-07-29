# TVBox for Windows 运行时契约

> 当前状态：Windows 版只保留 Node.js、JavaScript/Jint 和普通 HTTP/API 站点。
> JAR/Python 侧车及其资源已移除；旧版相关文档、目录和运行时不得重新接回构建。

## 1. 运行时目录

- 应用数据：`%LOCALAPPDATA%\TVBox for Windows`
- Node 脚本缓存：`%LOCALAPPDATA%\TVBox for Windows\node\index.js`
- JS 模块缓存：`%LOCALAPPDATA%\TVBox for Windows\js`
- 本地文件：`%LOCALAPPDATA%\TVBox for Windows\local`
- 内置 Node：`TVBox.Windows\Assets\node\node.exe`
- FFmpeg：`TVBox.Windows\ffmpeg\*.dll`
- 应用本地服务：`http://127.0.0.1:9978` 起逐端口探测

启动时会后台删除旧版本遗留的 `runtime`、`jar`、`py`、`pyenv` 目录。

## 2. Node 服务型配置源

代表源：

```text
https://example.com/cat/index.js
https://example.com/cat/index.js.md5
```

`index.js` 是完整 Node 服务，不是 Jint Spider。加载顺序：

1. `NodeSource` 下载并校验 MD5，临时文件写完后原子替换缓存。
2. `NodeRuntime` 使用内置 `node.exe` 启动脚本。
3. 强制 `HOST=127.0.0.1`，端口从 9989 起探测。
4. 轮询 `GET /config`，将 `video.sites` 摊平为 TVBox 配置。
5. 相对站点 API 重写为当前 Node 服务的绝对地址。
6. 配置切换和应用退出时必须停止 Node 进程树。

若用户设置了全局代理，Node 子进程继承 `HTTP_PROXY`、`HTTPS_PROXY`，并设置
`NO_PROXY=127.0.0.1,localhost`。

### 2.1 站点路由

Node 站点由 `NodeSpider` 转发：

| 能力 | 请求 |
|---|---|
| home | `POST {api}/home`，body `{filter}` |
| homeVideo | 可选 `POST {api}/homeVideo`；404 表示不支持并缓存 |
| category | `POST {api}/category`，同时发送 `id/tid`、`page/pg`、`filters/extend` |
| detail | `POST {api}/detail`，同时发送 `id/ids` |
| search | `POST {api}/search`，同时发送 `wd/key`、`page/pg` |
| play | `POST {api}/play`，body 含 `id/flag/vipFlags` |
| action | 可选 `POST {api}/action` |
| proxy | 媒体源返回的 Node 绝对代理 URL 由播放器直接请求 |

非 2xx 必须在 `NodeSpider` 层转为可见错误。只对连接重置、超时、502/503/504 等
明确瞬态错误重试一次；登录缺失、Cookie 缺失、404 和业务错误不得盲目重试。

### 2.2 配置中心

`nodejs_baseset` 的卡片结构为：

```text
vod_id = config-center
```

该卡通常没有 `action`。点击时直接用系统浏览器打开：

```text
{NodeRuntime.BaseUrl}/website
```

不能打开原始 `.js.md5` 配置 URL，也不能进入影片详情页。

## 3. JavaScript/Jint Spider

- 每个 `JsSpider` 独立 Jint Engine，并用 `SemaphoreSlim` 串行调用。
- 支持模块加载、`req/reqs`、HTML 规则、加解密、缓存和本地代理。
- `getProxy()` 与 `js2Proxy()` 生成的地址必须携带 `siteKey`，避免代理串站或 500。
- 所有网络请求统一走 `Net.HttpUtil`，继承 hosts、DoH、代理、广告和 Header 规则。

## 4. 已移除能力

以下运行时不再分发，也不应在设置或加载流程中自动下载：

- JAR / dex2jar / JVM sidecar
- Python runner / venv / pip 依赖
- Android 原生 Thunder、TVBus、ForceTech 等库

对应站点返回明确的 Windows 不支持提示，不得让异常冒泡导致整个配置加载失败。

## 5. 播放网络约束

- 最终 HTTP(S) URL 必须规范化为 `Uri.AbsoluteUri`。
- 普通结果使用 `Result.RealUrl`，保留 `playUrl + url` 语义。
- 未提供 `User-Agent` 时补 `Setting.Ua`。
- 配置顶层 `headers` 规则在请求发出前注入。
- Flyleaf 必须开启 `Demuxer.FormatOptToUnderlying`，让 UA、Referer、Cookie 等传给
  HLS 子清单和分片。
- Flyleaf 3.10.4 支持的倍速范围为 1x 到 4x；超出范围的数据需迁移回 1x。
- 硬解打开但持续无视频帧时，只允许自动切软件解码重试一次。
