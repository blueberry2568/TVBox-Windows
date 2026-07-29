# TVBox for Windows 发布清单

本文用于生成不包含开发者配置、历史、收藏、日志或私人订阅的 GitHub Release。正式发布应从干净的 Git 提交构建，不要直接压缩日常运行目录或 `%LOCALAPPDATA%\TVBox for Windows`。

## 1. 发布前确认

- 确认工作树中的改动均属于本次版本，没有临时截图、日志、测试配置或下载文件。
- 确认 `windows/TVBox.Windows/wexfnwconfig.json`、`prefs.json`、`configs.json`、`history.json`、`keep.json`、`app.log` 等本地文件未被 Git 跟踪。
- 搜索仓库中的私人订阅、带用户名/密码的 URL、Cookie、Token、私钥和 `C:\Users\...` 本机路径。
- 更新版本号、README、变更说明和已知问题。
- 确认项目级 `LICENSE`、第三方版权声明及所分发 FFmpeg/Node 二进制的许可要求。
- 从可信来源准备签名证书；证书和密码不得放入仓库。

建议检查命令：

```powershell
git status --short
git ls-files | Select-String -Pattern '(prefs|configs|history|keep)\.json|app\.log|wexfnwconfig'
rg -n --hidden -g '!**/bin/**' -g '!**/obj/**' -g '!artifacts/**' `
  'https?://[^/@\s:]+:[^/@\s]+@|C:\\Users\\|github_pat_|ghp_|BEGIN .*PRIVATE KEY'
```

任何匹配都必须人工判断。公共文档和依赖源码可能含普通示例 URL，但私人源地址、凭据和本机路径不得发布。

## 2. 恢复与严格构建

x64：

```powershell
dotnet restore .\windows\TVBox.Windows\TVBox.Windows.csproj -r win-x64
dotnet build .\windows\TVBox.Windows\TVBox.Windows.csproj `
  -c Release -p:Platform=x64 -r win-x64 --no-restore -warnaserror
```

正式发布要求零错误、零警告。不要把 `--no-restore` 用于首次 `publish`，否则可能得到文件看似齐全但缺失 .NET runtime pack 解析信息的无效包。当前内置 FFmpeg 与 Node.js 二进制均为 x64，不得把同一输出标记或发布为 ARM64。

## 3. 生成便携包

```powershell
.\scripts\Publish-Portable.ps1 -Version 1.0.2
```

默认输出：

```text
artifacts/
|-- TVBox-x64-1.0.2/
|-- TVBox-x64-1.0.2.zip
`-- SHA256SUMS.txt
```

脚本执行以下检查：

- 使用 Release、自包含 Windows App SDK 和明确的 RID 发布。
- 禁止包中出现常见用户数据文件和运行时缓存目录。
- 校验根目录启动器、`app/TVBox.exe`、`.deps.json`、`.runtimeconfig.json`、`app/Assets/node/node.exe`、JS 运行库和关键 FFmpeg DLL。
- 扫描文本文件中的本机用户路径、带凭据 URL、私钥和常见 GitHub Token。
- 复制公开 README，创建 ZIP 并写入 SHA-256。

使用 `-ZipOnly` 可在成功压缩后删除未压缩目录。使用自定义 `-OutputRoot` 时，脚本仍会验证所有删除目标位于该输出目录下。

## 4. 安装包

安装器应只从同一版本的干净便携目录取文件，不得从 `bin/`、`obj/`、旧 `windows/publish/` 或用户运行目录取文件。

```powershell
.\installer\build.ps1 -Version 1.0.2
```

安装包输出为 `artifacts\TVBox-Setup-x64-1.0.2.msi`，并追加写入 `artifacts\SHA256SUMS.txt`。

至少确认：

- 安装向导可以修改安装路径，开始菜单名称使用 `TVBox`。
- 升级前能关闭正在运行的 `TVBox.exe`，或明确提示用户退出。
- 卸载程序默认不要静默删除 `%LOCALAPPDATA%\TVBox for Windows`，避免误删用户收藏和历史；如提供清理选项，必须显式说明且由用户选择。
- 安装器架构必须标记为 x64，不得误标为 ARM64。
- 签名发生在最终文件生成后；签名后的安装包重新计算 SHA-256。

## 5. 人工验收

自动构建通过不等于播放行为已验证。发布前由人工至少检查：

- 全新数据目录首次启动，没有预置点播源、直播源、收藏或历史。
- 点播配置、CatPawOpen 配置和独立直播配置可以添加、切换和重载。
- 点播详情、搜索、播放、暂停、拖动进度、上下集、线路、倍率、比例和字幕。
- 直播频道、上下频道、暂停、线路切换及可回看内容的进度行为。
- 普通窗口、最大化、视频/直播全屏、画中画及退出后的窗口和侧栏状态。
- 100%、125%、150%、200% 缩放，以及有条件时的多显示器。
- 退出后没有遗留 Node 子进程或占用发布目录文件。

音画同步、爆音、异常时间戳、特定编码和长时间播放必须通过真实内容人工试听，不能仅凭编译或 UI 截图判断。

## 6. Release 内容

每个 GitHub Release 建议包含：

- x64 安装包
- x64 便携 ZIP
- `SHA256SUMS.txt`
- 版本变更、修复项、已知问题、最低系统版本和升级说明

Release 说明中不要粘贴私人订阅或带鉴权的复现地址。若没有代码签名，应明确说明 SmartScreen 可能提示未知发布者。

安装并配置好 GitHub CLI 后可使用类似命令发布；实际仓库、标签和文件名以当前版本为准：

```powershell
git tag -a v1.0.2 -m 'TVBox for Windows v1.0.2'
git push origin v1.0.2
gh release create v1.0.2 .\artifacts\TVBox-x64-1.0.2.zip `
  .\artifacts\TVBox-Setup-x64-1.0.2.msi `
  .\artifacts\SHA256SUMS.txt --verify-tag --title 'TVBox for Windows v1.0.2'
```

先创建草稿 Release 并人工核对附件、哈希和说明，再公开发布。

## 7. 发布后

- 从 GitHub Release 重新下载附件并核对 SHA-256，而不是只检查本地原文件。
- 在干净 Windows 用户环境中安装或解压一次，确认 `TVBox.exe` 可启动。
- 确认 Release 附件中没有 `prefs.json`、`configs.json`、`history.json`、`keep.json`、日志、PDB、证书或源地址文件。
- 若发现凭据或私人源泄露，立即删除 Release、撤销相应凭据、清理 Git 历史并重新发版；仅删除当前文件不足以消除 Git 历史中的泄露。
