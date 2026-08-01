# TVBox for Windows

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)

TVBox for Windows 是 [FongMi/TV](https://github.com/FongMi/TV) 的 Windows 桌面移植项目。应用使用 WinUI 3 构建，以 Flyleaf 和 FFmpeg 负责音视频播放，并提供点播、直播、全局搜索、收藏、历史、字幕、弹幕、画中画与沉浸式全屏等桌面端能力。

> 本项目不内置、不维护、也不分发任何影视源、直播源或账号。请只使用自己有权访问的配置和内容，并遵守所在地法律、内容授权条款与服务提供方规则。

## 界面预览

![TVBox for Windows 点播界面](https://raw.githubusercontent.com/blueberry2568/TVBox-for-Windows/main/docs/images/tvbox-vod.png)

## 主要功能

- 点播站点浏览、分类筛选、详情、线路与选集
- 直播分组、频道切换、多线路、节目单元数据与回看信息解析
- 跨站搜索，支持“站点切换”和“分组纵览”两种展示方式
- 播放速度、画面比例、硬件/软件解码偏好、缓冲与播放超时设置
- 在线字幕与本地字幕（SRT、ASS、SSA、VTT、SUB）
- 弹幕搜索、开关与样式设置
- 沉浸式全屏和可自由缩放的画中画窗口
- 点播、直播、搜索、收藏、历史和设置页面状态保留
- 自定义代理、DoH、User-Agent、请求头及源站规则

## 系统要求

- Windows 10 1809（内部版本 17763）或更高版本，推荐 Windows 11
- 64 位 x64 CPU；当前内置 FFmpeg 和 Node.js 均为 x64，暂不提供 ARM64 包
- 支持 Direct3D 的显卡及较新的显卡驱动
- 加载在线配置、海报、字幕和媒体时需要网络连接

发布包内置私有 .NET Desktop Runtime，启动时不会依赖或检测系统全局安装的 .NET；正常情况下无需另行安装运行时。不同来源和编码格式仍可能受网络、服务端限制、显卡驱动或 FFmpeg 能力影响。

## 安装与运行

请从项目的 GitHub Releases 页面获取正式发布文件，不要从不明镜像下载。

### 安装包

下载 x64 安装程序，核对 Release 页面给出的 SHA-256 后运行。安装向导可以选择安装目录。若安装程序尚未进行代码签名，Windows SmartScreen 可能显示未知发布者；请先确认文件确实来自本项目 Release，再决定是否继续。

### 便携包

1. 下载 x64 便携 ZIP。
2. 完整解压到可写目录，不要直接在压缩包内运行。
3. 运行 `TVBox.exe`。
4. 保持 `TVBox.exe` 与 `assets`、`ffmpeg`、`libs`、`locales`、`node`、`runtime` 目录的相对位置不变，不要只移动主程序。
5. 更新时先退出 TVBox，再用新版本替换整个程序目录。

安装包与便携包使用相同的多文件结构，不采用单文件打包：

```text
TVBox/
|-- TVBox.exe
|-- assets/
|   |-- icons/
|   `-- js/
|-- ffmpeg/
|-- libs/
|   |-- TVBox.dll
|   |-- TVBox.deps.json
|   `-- TVBox.runtimeconfig.json
|-- locales/
|   `-- winui/
|       |-- en-us/ ja-JP/ ko-KR/ zh-CN/ zh-TW/
|       |-- Microsoft.ui.xaml.dll
|       `-- Microsoft.UI.Xaml.Phone.dll
|-- node/
|-- runtime/
|   |-- host/fxr/
|   `-- shared/
|-- LICENSE
|-- README.md
`-- THIRD-PARTY-NOTICES.md
```

根目录的 `TVBox.exe` 是唯一应用入口，并通过 `libs\TVBox.dll` 启动托管程序。`runtime` 是标准私有 dotnet-root，包含 `host\fxr`、`Microsoft.NETCore.App` 和 `Microsoft.WindowsDesktop.App`；因此不会因系统未安装、安装错架构或全局运行时检测异常而拒绝启动。WinUI 的两个本地化模块和中英日韩、繁中 MUI 资源统一放在 `locales\winui`，`libs` 不再保留重复的语言目录。

发布文件本身不包含视频源、直播源、设置、收藏、历史、日志或其他用户状态。运行数据只写入 `%LOCALAPPDATA%\TVBox for Windows`，替换程序文件或正常升级时会保留；数据位置及清理方式见“隐私与本地数据”。

## 添加配置

首次运行且尚未添加点播源时，应用会显示不可跳过的初始源配置：点播配置必填，直播配置可选。点播源加载成功后导航才会解锁；后续可在“设置”中添加、切换或重载配置。CatPawOpen 配置中心在外部浏览器保存变更后，应用会自动检测并重新加载当前配置，无需再到“设置”手动重载。

点播配置中的直播列表可直接用于“直播”页面，也可以单独添加直播地址。源站返回 401、403、404、5xx、TLS 错误或超时通常表示远端服务、鉴权、网络或地区限制异常，不代表播放器一定存在故障。

## 兼容格式

兼容性以具体源的实现和当前版本为准，不保证所有 Android TVBox 配置都能在 Windows 上运行。

| 类型 | 当前支持 |
| --- | --- |
| 点播配置 | TVBox/CatVod JSON；明文、带 `**` 标记的 Base64 配置、`2423` 格式的 AES-CBC 配置 |
| CatPawOpen | Node 服务型 `.js.md5` / `.js` 订阅及伴随配置，包含其点播站点和可发现的直播数据 |
| JavaScript Spider | 由 Jint 执行的 CatVod JavaScript Spider，含常用模块、网络请求与本地代理能力 |
| 普通站点 | TVBox 配置中可由当前 Windows 兼容层直接访问的 HTTP/API 站点 |
| 直播列表 | TXT、M3U/M3U8 播放列表、JSON 分组、TVBox 配置内的 `live` / `lives`、配置仓库及 CatPawOpen 直播 |
| 播放媒体 | HLS、DASH 及 FFmpeg/Flyleaf 支持的常见网络流与本地字幕格式 |

Windows 版不支持依赖 Android/JVM 的 JAR Spider、Python Spider，以及 Thunder、TVBus、ForceTech 等 Android 原生或专有运行库。含这些站点的配置仍可加载其他兼容站点，但相关站点会显示不支持或不可用。

## 隐私与本地数据

运行数据保存在：

```text
%LOCALAPPDATA%\TVBox for Windows
```

常见文件包括：

| 文件/目录 | 内容 |
| --- | --- |
| `prefs.json` | 应用设置、当前配置地址 |
| `configs.json` | 已添加的点播和直播配置记录 |
| `history.json` | 播放历史和进度 |
| `keep.json` | 收藏 |
| `app.log` | 运行日志，可能包含请求地址和错误信息 |
| `cache`、`js`、`node`、`live` 等 | 图片、脚本、订阅和直播缓存 |

隐私模式用于停止新增播放历史，不会自动删除已有历史、收藏或配置。完全退出应用后删除整个 `%LOCALAPPDATA%\TVBox for Windows` 目录可以恢复为全新状态；此操作不可撤销。

发布脚本只打包项目编译输出，不会读取或复制该数据目录。提交问题前请检查并脱敏 `app.log`，至少移除私人订阅地址、用户名、密码、Cookie、Token 和请求头。

## 从源码构建

### 环境

- Windows 10/11
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Git
- 可访问 NuGet.org 的网络

### x64 严格构建

```powershell
dotnet restore .\windows\TVBox.Windows\TVBox.Windows.csproj -r win-x64
dotnet build .\windows\TVBox.Windows\TVBox.Windows.csproj `
  -c Release -p:Platform=x64 -r win-x64 --no-restore -warnaserror
```

项目启用了自包含 Windows App SDK，必须显式指定 RID 和平台。仅运行 `dotnet build -c Release` 会因缺少 Windows 架构而失败。

### 生成干净便携包

```powershell
.\scripts\Publish-Portable.ps1 -Version 1.0.8
```

输出位于 `artifacts/`，包括发布目录、ZIP 和 `SHA256SUMS.txt`。发布目录采用上述结构：根目录提供唯一的 `TVBox.exe` apphost，`libs` 保存应用与第三方依赖，`runtime` 保存标准私有 .NET 运行时，Node、FFmpeg、资源和语言文件分别归类。脚本会校验 apphost 到 `libs` 和私有运行时的部署契约、目录结构、唯一主程序和版本元数据，并在发现用户数据、凭据 URL、私钥、GitHub Token 或本机用户路径时停止打包。

完整发布检查清单见 [docs/RELEASE.md](docs/RELEASE.md)。

## 项目结构

```text
.
|-- windows/TVBox.Windows/       WinUI 3 应用源码
|-- docs/
|   |-- images/                  README 界面截图
|   |-- development/             架构、行为规格与运行时约定
|   `-- RELEASE.md               发布清单
|-- installer/               WiX MSI 安装包工程
|-- scripts/                 干净便携包脚本
|-- LICENSE                  GNU GPL v3 项目许可证
`-- THIRD-PARTY-NOTICES.md   第三方组件与来源说明
```

`windows/publish/`、`artifacts/`、编译目录和本地用户数据均不应提交到 Git。

## 常见问题

### 配置或站点加载失败

先在浏览器确认配置地址可访问，再检查代理、DoH、系统时间和服务端状态。HTTP 401/403 通常与鉴权有关，404 表示远端地址不存在，502/503/504 多为上游暂时不可用。若只有个别站点失败，通常应由该站点源维护者处理。

### M3U8 报 404

播放器只能请求源站实际提供的地址。请检查最终播放地址是否过期、是否需要 Referer、Cookie 或特定 User-Agent，以及主播放列表中的子列表和分片是否仍有效。日志中的 `avformat_open_input` 404 是服务端响应，需要结合最终 URL 和请求头判断。

### 黑屏、无画面或花屏

更新显卡驱动，并在设置中切换硬件/软件解码偏好后重试。若声音正常但画面异常，请记录系统版本、显卡型号、视频编码、是否全屏/画中画以及复现步骤。

### 音画不同步、爆音或倍速卡顿

先恢复 1x 倍速并比较软件解码与硬件解码。网络抖动、异常时间戳和服务端分片也会造成类似现象。提交问题时请说明持续播放时长、倍速、音视频编码和是否能在其他播放器稳定复现。

### 全屏或画中画布局异常

记录 Windows 缩放比例、多显示器排列、窗口进入前是否最大化，以及退出后的实际状态。高 DPI、跨显示器和远程桌面是定位这类问题的重要条件。

### 恢复全新状态

退出应用，按需备份后删除 `%LOCALAPPDATA%\TVBox for Windows`。重新启动后应用会创建空白数据目录，发布包本身不会带入开发者的配置、历史或收藏。

## 反馈问题

Issue 至少应包含：

- TVBox 版本
- Windows 版本、缩放比例、CPU 与显卡型号
- 可重复的操作步骤和期望/实际结果
- 问题是否只发生于单个源、单条线路或特定编码
- 已脱敏的 `%LOCALAPPDATA%\TVBox for Windows\app.log`

请勿公开提交受版权保护的媒体、私人订阅、账号、Cookie、Token 或 DRM 密钥。

## 版权与合规

TVBox for Windows 是 [FongMi/TV](https://github.com/FongMi/TV) 的衍生项目，项目源代码依照 [GNU General Public License v3.0](LICENSE) 发布。各贡献者与上游项目保留其相应代码的著作权。

本项目仅提供客户端技术实现。内容地址、账号、播放权限和网络服务均由用户自行负责。内置 FFmpeg、FlyleafLib、Jint、AngleSharp、Windows App SDK、Node.js 及其传递依赖仍分别遵守各自许可证。具体版本、来源和授权信息见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 以及发布包内各组件旁的 `LICENSE.txt` / `SOURCE.txt`。
