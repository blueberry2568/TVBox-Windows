[CmdletBinding()]
param(
    [ValidateSet("x64")]
    [string]$Architecture = "x64",

    [ValidatePattern("^\d+\.\d+\.\d+$")]
    [string]$Version = "1.0.6",

    [string]$OutputRoot = "",

    [switch]$ZipOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Parent
    )

    $fullPath = Get-FullPath $Path
    $fullParent = (Get-FullPath $Parent).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝操作输出目录之外的路径: $fullPath"
    }
    return $fullPath
}

function Remove-OutputItem {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Parent
    )

    $safePath = Assert-ChildPath -Path $Path -Parent $Parent
    if (Test-Path -LiteralPath $safePath) {
        Remove-Item -LiteralPath $safePath -Recurse -Force
    }
}

function Assert-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $path = Join-Path $Root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "发布输出缺少必要文件: $RelativePath"
    }
}

function Assert-RequiredDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $path = Join-Path $Root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "发布输出缺少必要目录: $RelativePath"
    }
}

function Assert-AppHostContract {
    param([Parameter(Mandatory = $true)][string]$Path)

    $binaryText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($Path))
    foreach ($expected in @('libs\TVBox.dll', '..\runtime')) {
        if ($binaryText.IndexOf($expected, [StringComparison]::Ordinal) -lt 0) {
            throw "TVBox.exe apphost 缺少内嵌路径: $expected"
        }
    }
}

function Resolve-ManifestTool {
    param([Parameter(Mandatory = $true)][string]$DotNetPath)

    $globalPackagesOutput = @(& $DotNetPath nuget locals global-packages --list 2>&1)
    $dotNetExitCode = $LASTEXITCODE
    $globalPackagesLine = $globalPackagesOutput | Select-Object -First 1
    if ($dotNetExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($globalPackagesLine)) {
        throw "无法确定 NuGet 全局包目录"
    }

    $globalPackagesRoot = ($globalPackagesLine -replace '^[^:]+:\s*', '').Trim().TrimEnd('\', '/')
    $buildToolsRoot = Join-Path $globalPackagesRoot "microsoft.windows.sdk.buildtools"
    if (-not (Test-Path -LiteralPath $buildToolsRoot -PathType Container)) {
        throw "未找到 Microsoft.Windows.SDK.BuildTools: $buildToolsRoot"
    }

    $candidates = @(
        Get-ChildItem -LiteralPath $buildToolsRoot -Recurse -Filter "mt.exe" -File |
            Where-Object { $_.FullName -match '[\\/]x64[\\/]mt\.exe$' } |
            Sort-Object FullName -Descending
    )
    if ($candidates.Count -eq 0) {
        throw "Microsoft.Windows.SDK.BuildTools 中缺少 x64 mt.exe"
    }

    return $candidates[0].FullName
}

function Set-StructuredActivationManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$ReleaseRoot,
        [Parameter(Mandatory = $true)][string]$ManifestTool,
        [Parameter(Mandatory = $true)][string]$OutputParent
    )

    $manifestPath = Assert-ChildPath `
        -Path (Join-Path $OutputParent ".TVBox-structured.manifest") `
        -Parent $OutputParent
    Remove-OutputItem -Path $manifestPath -Parent $OutputParent

    try {
        & $ManifestTool -nologo "-inputresource:$ExecutablePath;#1" "-out:$manifestPath"
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw "无法从 TVBox.exe 提取应用清单"
        }

        [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
        $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
        $namespaces.AddNamespace("asmv3", "urn:schemas-microsoft-com:asm.v3")
        [Xml.XmlNode[]]$fileNodes = @($manifest.SelectNodes("//asmv3:file", $namespaces))
        if ($fileNodes.Length -eq 0) {
            throw "TVBox.exe 应用清单缺少 WinUI 文件注册"
        }

        foreach ($fileNode in $fileNodes) {
            $fileName = [IO.Path]::GetFileName($fileNode.GetAttribute("name"))
            if ([string]::IsNullOrWhiteSpace($fileName)) {
                throw "TVBox.exe 应用清单包含无效文件名"
            }

            $candidatePaths = @(
                @(
                    "libs\$fileName",
                    "locales\winui\$fileName"
                ) | Where-Object {
                    Test-Path -LiteralPath (Join-Path $ReleaseRoot $_) -PathType Leaf
                }
            )
            if ($candidatePaths.Count -ne 1) {
                throw "WinUI 激活组件位置不唯一或不存在: $fileName"
            }

            $relativePath = $candidatePaths[0]
            $fileNode.SetAttribute("name", $relativePath)
            $fileNode.RemoveAttribute("loadFrom")
        }

        $writerSettings = [Xml.XmlWriterSettings]::new()
        $writerSettings.Encoding = [Text.UTF8Encoding]::new($false)
        $writerSettings.Indent = $false
        $writer = [Xml.XmlWriter]::Create($manifestPath, $writerSettings)
        try { $manifest.Save($writer) } finally { $writer.Dispose() }

        & $ManifestTool -nologo -manifest $manifestPath "-outputresource:$ExecutablePath;#1"
        if ($LASTEXITCODE -ne 0) {
            throw "无法将结构化应用清单写入 TVBox.exe"
        }

        & $ManifestTool -nologo "-inputresource:$ExecutablePath;#1" "-out:$manifestPath"
        if ($LASTEXITCODE -ne 0) {
            throw "无法复核 TVBox.exe 结构化应用清单"
        }

        [xml]$embeddedManifest = Get-Content -LiteralPath $manifestPath -Raw
        $embeddedNamespaces = [Xml.XmlNamespaceManager]::new($embeddedManifest.NameTable)
        $embeddedNamespaces.AddNamespace("asmv3", "urn:schemas-microsoft-com:asm.v3")
        [Xml.XmlNode[]]$embeddedFileNodes = @($embeddedManifest.SelectNodes("//asmv3:file", $embeddedNamespaces))
        if ($embeddedFileNodes.Length -ne $fileNodes.Length) {
            throw "TVBox.exe 结构化应用清单文件注册数量不一致"
        }
        foreach ($fileNode in $embeddedFileNodes) {
            $name = $fileNode.GetAttribute("name")
            $isStructuredPath =
                $name.StartsWith("libs\", [StringComparison]::OrdinalIgnoreCase) -or
                $name.StartsWith("locales\winui\", [StringComparison]::OrdinalIgnoreCase)
            if (-not $isStructuredPath -or $fileNode.HasAttribute("loadFrom") -or
                -not (Test-Path -LiteralPath (Join-Path $ReleaseRoot $name) -PathType Leaf)) {
                throw "TVBox.exe 结构化应用清单路径不正确: $name"
            }
        }
    }
    finally {
        Remove-OutputItem -Path $manifestPath -Parent $OutputParent
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "复制源目录不存在: $Source"
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force
}

function Remove-UnselectedSatelliteResources {
    param(
        [Parameter(Mandatory = $true)][string]$FrameworkDirectory,
        [Parameter(Mandatory = $true)][string[]]$AllowedCultures,
        [Parameter(Mandatory = $true)][string]$OutputParent
    )

    foreach ($directory in Get-ChildItem -LiteralPath $FrameworkDirectory -Directory) {
        $files = @(Get-ChildItem -LiteralPath $directory.FullName -Recurse -File)
        if ($files.Count -eq 0) { continue }

        $isSatelliteDirectory = @(
            $files | Where-Object { -not $_.Name.EndsWith('.resources.dll', [StringComparison]::OrdinalIgnoreCase) }
        ).Count -eq 0
        if ($isSatelliteDirectory -and $AllowedCultures -notcontains $directory.Name) {
            Remove-OutputItem -Path $directory.FullName -Parent $OutputParent
        }
    }
}

function Assert-NoDuplicateFilesAcrossTrees {
    param(
        [Parameter(Mandatory = $true)][string]$LeftRoot,
        [Parameter(Mandatory = $true)][string]$RightRoot
    )

    $rightFilesByName = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in Get-ChildItem -LiteralPath $RightRoot -Recurse -File) {
        if (-not $rightFilesByName.ContainsKey($file.Name)) {
            $rightFilesByName[$file.Name] = [Collections.Generic.List[IO.FileInfo]]::new()
        }
        $rightFilesByName[$file.Name].Add($file)
    }

    $duplicates = [Collections.Generic.List[string]]::new()
    foreach ($leftFile in Get-ChildItem -LiteralPath $LeftRoot -Recurse -File) {
        if (-not $rightFilesByName.ContainsKey($leftFile.Name)) { continue }

        $leftHash = (Get-FileHash -LiteralPath $leftFile.FullName -Algorithm SHA256).Hash
        foreach ($rightFile in $rightFilesByName[$leftFile.Name]) {
            if ($leftFile.Length -ne $rightFile.Length) { continue }
            $rightHash = (Get-FileHash -LiteralPath $rightFile.FullName -Algorithm SHA256).Hash
            if ($leftHash -eq $rightHash) {
                $leftRelative = $leftFile.FullName.Substring($LeftRoot.TrimEnd('\', '/').Length + 1)
                $rightRelative = $rightFile.FullName.Substring($RightRoot.TrimEnd('\', '/').Length + 1)
                $duplicates.Add("libs\$leftRelative <=> runtime\$rightRelative [$leftHash]")
            }
        }
    }

    if ($duplicates.Count -gt 0) {
        throw "libs 与 runtime 包含同名且 SHA256 相同的重复文件:`n$($duplicates -join "`n")"
    }
}

function Test-ReleaseContent {
    param([Parameter(Mandatory = $true)][string]$Root)

    $allowedRootItems = @(
        "TVBox.exe",
        "assets",
        "ffmpeg",
        "libs",
        "locales",
        "node",
        "runtime",
        "LICENSE",
        "README.md",
        "THIRD-PARTY-NOTICES.md"
    )
    $unexpectedRootItems = Get-ChildItem -LiteralPath $Root -Force | Where-Object {
        $allowedRootItems -notcontains $_.Name
    }
    if ($unexpectedRootItems) {
        throw "发布目录顶层包含未归类项目: $($unexpectedRootItems.Name -join ', ')"
    }

    $forbiddenNames = @(
        "prefs.json",
        "preferences.json",
        "settings.json",
        "configs.json",
        "setting_config.json",
        "history.json",
        "keep.json",
        "favorites.json",
        "sources.json",
        "cookies.json",
        "credentials.json",
        "tokens.json",
        "app.log",
        "wexfnwconfig.json"
    )
    $forbiddenExtensions = @(".pdb", ".log", ".tmp", ".bak", ".dmp")
    $forbiddenRootDirectories = @("cache", "js", "live", "wall", "restore", "local")

    $badFiles = Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object {
        $forbiddenNames -contains $_.Name.ToLowerInvariant() -or
        $forbiddenExtensions -contains $_.Extension.ToLowerInvariant()
    }
    if ($badFiles) {
        $relative = $badFiles | ForEach-Object { $_.FullName.Substring($Root.Length).TrimStart('\', '/') }
        throw "发布输出包含用户数据或本地源文件:`n$($relative -join "`n")"
    }

    $badDirectories = @(
        Get-ChildItem -LiteralPath $Root -Directory | Where-Object {
            $forbiddenRootDirectories -contains $_.Name.ToLowerInvariant()
        }
    )
    if ($badDirectories) {
        throw "发布输出包含运行时用户目录: $($badDirectories.Name -join ', ')"
    }

    $textExtensions = @(".config", ".json", ".js", ".md", ".txt", ".xml", ".yaml", ".yml")
    $patterns = [ordered]@{
        "Windows 本机用户路径" = "(?i)[A-Z]:\\Users\\[^\r\n`"']+"
        "带用户名或密码的 URL" = '(?i)https?://[^\s/@:]+:[^\s/@]+@'
        "私钥" = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
        "GitHub Token" = '(?i)\b(?:ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b'
    }

    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File) {
        if ($textExtensions -notcontains $file.Extension.ToLowerInvariant()) { continue }
        if ($file.Length -gt 10MB) { continue }

        $content = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop
        foreach ($entry in $patterns.GetEnumerator()) {
            if ($content -match $entry.Value) {
                $relative = $file.FullName.Substring($Root.Length).TrimStart('\', '/')
                throw "敏感信息扫描失败（$($entry.Key)）: $relative"
            }
        }
    }
}

function Test-PublishInputs {
    param([Parameter(Mandatory = $true)][string[]]$Paths)

    $forbiddenNames = @(
        "prefs.json",
        "preferences.json",
        "settings.json",
        "configs.json",
        "setting_config.json",
        "history.json",
        "keep.json",
        "favorites.json",
        "sources.json",
        "cookies.json",
        "credentials.json",
        "tokens.json",
        "app.log",
        "wexfnwconfig.json"
    )
    $forbiddenExtensions = @(".pdb", ".log", ".tmp", ".bak", ".dmp")
    $textExtensions = @(".config", ".json", ".js", ".md", ".txt", ".xml", ".yaml", ".yml")
    $patterns = [ordered]@{
        "Windows 本机用户路径" = "(?i)[A-Z]:\\Users\\[^\r\n\`"']+"
        "带用户名或密码的 URL" = '(?i)https?://[^\s/@:]+:[^\s/@]+@'
        "私钥" = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
        "GitHub Token" = '(?i)\b(?:ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b'
    }

    $files = foreach ($path in $Paths) {
        if (Test-Path -LiteralPath $path -PathType Container) {
            Get-ChildItem -LiteralPath $path -Recurse -File
        }
        elseif (Test-Path -LiteralPath $path -PathType Leaf) {
            Get-Item -LiteralPath $path
        }
        else {
            throw "发布输入不存在: $path"
        }
    }

    foreach ($file in $files) {
        if ($forbiddenNames -contains $file.Name.ToLowerInvariant() -or
            $forbiddenExtensions -contains $file.Extension.ToLowerInvariant()) {
            throw "发布输入包含用户数据或临时文件: $($file.FullName)"
        }
        if ($textExtensions -notcontains $file.Extension.ToLowerInvariant() -or $file.Length -gt 10MB) { continue }

        $content = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop
        foreach ($entry in $patterns.GetEnumerator()) {
            if ($content -match $entry.Value) {
                throw "发布输入敏感信息扫描失败（$($entry.Key)）: $($file.FullName)"
            }
        }
    }
}

$repoRoot = Get-FullPath (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "windows\TVBox.Windows\TVBox.Windows.csproj"
$privateRuntimeScript = Join-Path $repoRoot "scripts\New-PrivateDotNetRoot.ps1"
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "找不到项目文件: $project"
}
if (-not (Test-Path -LiteralPath $privateRuntimeScript -PathType Leaf)) {
    throw "找不到私有 .NET 运行时构建脚本: $privateRuntimeScript"
}

Test-PublishInputs -Paths @(
    (Join-Path $repoRoot "windows\TVBox.Windows\Assets"),
    (Join-Path $repoRoot "windows\TVBox.Windows\ffmpeg"),
    (Join-Path $repoRoot "windows\TVBox.Windows\node"),
    (Join-Path $repoRoot "LICENSE"),
    (Join-Path $repoRoot "README.md"),
    (Join-Path $repoRoot "THIRD-PARTY-NOTICES.md")
)

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $output = Join-Path $repoRoot "artifacts"
}
elseif ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    $output = $OutputRoot
}
else {
    $output = Join-Path $repoRoot $OutputRoot
}
$output = Get-FullPath $output
New-Item -ItemType Directory -Force -Path $output | Out-Null

$platform = "x64"
$rid = "win-x64"
$packageName = "TVBox-$Architecture-$Version"
$publishDirectory = Assert-ChildPath -Path (Join-Path $output $packageName) -Parent $output
$stagingDirectory = Assert-ChildPath -Path (Join-Path $output ".$packageName-publish") -Parent $output
$assetsDirectory = Assert-ChildPath -Path (Join-Path $publishDirectory "assets") -Parent $publishDirectory
$ffmpegDirectory = Assert-ChildPath -Path (Join-Path $publishDirectory "ffmpeg") -Parent $publishDirectory
$libsDirectory = Assert-ChildPath -Path (Join-Path $publishDirectory "libs") -Parent $publishDirectory
$localesDirectory = Assert-ChildPath -Path (Join-Path $publishDirectory "locales") -Parent $publishDirectory
$localizedWinUiDirectory = Assert-ChildPath -Path (Join-Path $localesDirectory "winui") -Parent $localesDirectory
$nodeDirectory = Assert-ChildPath -Path (Join-Path $publishDirectory "node") -Parent $publishDirectory
$runtimeDirectory = Assert-ChildPath -Path (Join-Path $publishDirectory "runtime") -Parent $publishDirectory
$zipPath = Assert-ChildPath -Path (Join-Path $output "$packageName.zip") -Parent $output

Remove-OutputItem -Path $publishDirectory -Parent $output
Remove-OutputItem -Path $stagingDirectory -Parent $output
Remove-OutputItem -Path $zipPath -Parent $output
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw "未找到 dotnet。请安装 .NET 9 SDK。" }
$manifestTool = Resolve-ManifestTool -DotNetPath $dotnet.Source

Write-Host "Publishing $packageName..."
Push-Location $repoRoot
try {
    # A forced self-contained restore downloads and locks the two framework
    # runtime packs. The application itself is still published as FDD below.
    & $dotnet.Source restore $project `
        -r $rid `
        --force `
        "-p:Platform=$platform" `
        -p:SelfContained=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore 失败，退出代码: $LASTEXITCODE"
    }

    & $dotnet.Source publish $project `
        -c Release `
        "-p:Platform=$platform" `
        -r $rid `
        --self-contained false `
        --no-restore `
        -warnaserror `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        -p:PublishSingleFile=false `
        -p:CreateStructuredPublishAppHost=true `
        "-p:StructuredAppHostPath=$(Join-Path $publishDirectory 'TVBox.exe')" `
        "-p:Version=$Version" `
        "-p:AssemblyVersion=$Version.0" `
        "-p:FileVersion=$Version.0" `
        "-p:InformationalVersion=$Version" `
        -p:IncludeSourceRevisionInInformationalVersion=false `
        -o $stagingDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败，退出代码: $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

& $privateRuntimeScript `
    -Project $project `
    -Destination $runtimeDirectory `
    -RuntimeIdentifier $rid `
    -SkipRestore `
    -Force

$coreVersions = @(Get-ChildItem -LiteralPath (Join-Path $runtimeDirectory "shared\Microsoft.NETCore.App") -Directory)
$desktopVersions = @(Get-ChildItem -LiteralPath (Join-Path $runtimeDirectory "shared\Microsoft.WindowsDesktop.App") -Directory)
if ($coreVersions.Count -ne 1 -or $desktopVersions.Count -ne 1) {
    throw "私有 .NET 运行时框架版本目录不唯一"
}
$coreRuntimeVersion = $coreVersions[0].Name
$desktopRuntimeVersion = $desktopVersions[0].Name

# English resources are neutral assemblies in the .NET runtime. Preserve only
# the four localized satellite sets requested for this distribution.
$runtimeCultures = @('ja', 'ko', 'zh-Hans', 'zh-Hant')
Remove-UnselectedSatelliteResources `
    -FrameworkDirectory $coreVersions[0].FullName `
    -AllowedCultures $runtimeCultures `
    -OutputParent $runtimeDirectory
Remove-UnselectedSatelliteResources `
    -FrameworkDirectory $desktopVersions[0].FullName `
    -AllowedCultures $runtimeCultures `
    -OutputParent $runtimeDirectory

# Runtime components are copied from source-controlled inputs so their release
# location does not depend on the SDK's content-item flattening behavior.
Copy-DirectoryContents `
    -Source (Join-Path $repoRoot "windows\TVBox.Windows\Assets") `
    -Destination $assetsDirectory
Copy-DirectoryContents `
    -Source (Join-Path $repoRoot "windows\TVBox.Windows\node") `
    -Destination $nodeDirectory
Copy-DirectoryContents `
    -Source (Join-Path $repoRoot "windows\TVBox.Windows\ffmpeg") `
    -Destination $ffmpegDirectory

# Remove content copies from the SDK staging area before classifying app files.
foreach ($contentDirectory in @("Assets", "assets", "node", "ffmpeg")) {
    $contentPath = Join-Path $stagingDirectory $contentDirectory
    if (Test-Path -LiteralPath $contentPath) {
        Remove-OutputItem -Path $contentPath -Parent $stagingDirectory
    }
}
Remove-OutputItem -Path (Join-Path $stagingDirectory "TVBox.exe") -Parent $stagingDirectory
Remove-OutputItem -Path (Join-Path $stagingDirectory "THIRD-PARTY-NOTICES.md") -Parent $stagingDirectory

# Native WinUI MUI files must remain adjacent to the modules that own them.
# Keep those two localized modules and their selected resources together under
# locales/winui so libs has no language folders or duplicate MUI payload.
$winUiCultures = @('en-us', 'ja-JP', 'ko-KR', 'zh-CN', 'zh-TW')
New-Item -ItemType Directory -Force -Path $localizedWinUiDirectory | Out-Null
foreach ($directory in Get-ChildItem -LiteralPath $stagingDirectory -Directory) {
    $files = @(Get-ChildItem -LiteralPath $directory.FullName -Recurse -File)
    if ($files.Count -gt 0 -and @($files | Where-Object { $_.Extension -ne ".mui" }).Count -eq 0) {
        if ($winUiCultures -notcontains $directory.Name) {
            Remove-OutputItem -Path $directory.FullName -Parent $stagingDirectory
            continue
        }

        Move-Item -LiteralPath $directory.FullName `
            -Destination (Join-Path $localizedWinUiDirectory $directory.Name) `
            -Force
    }
}

foreach ($localizedModule in @('Microsoft.ui.xaml.dll', 'Microsoft.UI.Xaml.Phone.dll')) {
    $modulePath = Join-Path $stagingDirectory $localizedModule
    Assert-RequiredFile -Root $stagingDirectory -RelativePath $localizedModule
    Move-Item -LiteralPath $modulePath -Destination $localizedWinUiDirectory -Force
}

New-Item -ItemType Directory -Force -Path $libsDirectory | Out-Null
Get-ChildItem -LiteralPath $stagingDirectory -Force |
    Move-Item -Destination $libsDirectory -Force
Remove-OutputItem -Path $stagingDirectory -Parent $output

$readme = Join-Path $repoRoot "README.md"
if (Test-Path -LiteralPath $readme -PathType Leaf) {
    Copy-Item -LiteralPath $readme -Destination (Join-Path $publishDirectory "README.md") -Force
}
$license = Join-Path $repoRoot "LICENSE"
if (Test-Path -LiteralPath $license -PathType Leaf) {
    Copy-Item -LiteralPath $license -Destination (Join-Path $publishDirectory "LICENSE") -Force
}
$notices = Join-Path $repoRoot "THIRD-PARTY-NOTICES.md"
if (Test-Path -LiteralPath $notices -PathType Leaf) {
    Copy-Item -LiteralPath $notices -Destination (Join-Path $publishDirectory "THIRD-PARTY-NOTICES.md") -Force
}

try {
    Set-StructuredActivationManifest `
        -ExecutablePath (Join-Path $publishDirectory "TVBox.exe") `
        -ReleaseRoot $publishDirectory `
        -ManifestTool $manifestTool `
        -OutputParent $output
}
catch {
    throw "TVBox.exe 结构化应用清单处理失败: $($_.Exception.Message)`n$($_.ScriptStackTrace)"
}

$requiredFiles = @(
    "TVBox.exe",
    "LICENSE",
    "README.md",
    "THIRD-PARTY-NOTICES.md",
    "assets\icons\icon.ico",
    "assets\icons\icon.png",
    "assets\icons\icon-title.png",
    "assets\js\lib\cat.js",
    "node\node.exe",
    "node\catpaw-bootstrap.js",
    "node\LICENSE.txt",
    "node\SOURCE.txt",
    "ffmpeg\avcodec-62.dll",
    "ffmpeg\avdevice-62.dll",
    "ffmpeg\avfilter-11.dll",
    "ffmpeg\avformat-62.dll",
    "ffmpeg\avutil-60.dll",
    "ffmpeg\LICENSE.txt",
    "ffmpeg\SOURCE.txt",
    "ffmpeg\swresample-6.dll",
    "ffmpeg\swscale-9.dll",
    "libs\TVBox.dll",
    "libs\TVBox.deps.json",
    "libs\TVBox.runtimeconfig.json",
    "libs\Flyleaf.FFmpeg.Bindings.dll",
    "locales\winui\Microsoft.ui.xaml.dll",
    "locales\winui\Microsoft.UI.Xaml.Phone.dll",
    "locales\winui\zh-CN\Microsoft.ui.xaml.dll.mui",
    "locales\winui\zh-TW\Microsoft.ui.xaml.dll.mui",
    ("runtime\host\fxr\{0}\hostfxr.dll" -f $coreRuntimeVersion),
    ("runtime\shared\Microsoft.NETCore.App\{0}\.version" -f $coreRuntimeVersion),
    ("runtime\shared\Microsoft.NETCore.App\{0}\coreclr.dll" -f $coreRuntimeVersion),
    ("runtime\shared\Microsoft.NETCore.App\{0}\hostpolicy.dll" -f $coreRuntimeVersion),
    ("runtime\shared\Microsoft.WindowsDesktop.App\{0}\Microsoft.WindowsDesktop.App.deps.json" -f $desktopRuntimeVersion)
)
foreach ($relativePath in $requiredFiles) {
    Assert-RequiredFile -Root $publishDirectory -RelativePath $relativePath
}

foreach ($relativePath in @("assets", "ffmpeg", "libs", "locales", "node", "runtime")) {
    Assert-RequiredDirectory -Root $publishDirectory -RelativePath $relativePath
}

$mainExecutable = Join-Path $publishDirectory "TVBox.exe"
Assert-AppHostContract -Path $mainExecutable
$versionInfo = (Get-Item -LiteralPath $mainExecutable).VersionInfo
if ($versionInfo.FileVersion -ne "$Version.0" -or
    $versionInfo.ProductVersion -ne $Version -or
    $versionInfo.ProductName -ne "TVBox for Windows") {
    throw "TVBox.exe 版本元数据不正确: FileVersion=$($versionInfo.FileVersion), ProductVersion=$($versionInfo.ProductVersion), Product=$($versionInfo.ProductName)"
}

if (Test-Path -LiteralPath (Join-Path $libsDirectory "TVBox.exe")) {
    throw "libs 目录不应包含第二个 TVBox.exe"
}
foreach ($hostFile in @("hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "System.Private.CoreLib.dll")) {
    $matches = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Filter $hostFile)
    if ($matches.Count -ne 1 -or
        -not $matches[0].FullName.StartsWith($runtimeDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw "私有 .NET 主机文件位置不正确: $hostFile"
    }
}

$runtimeConfigPath = Join-Path $libsDirectory "TVBox.runtimeconfig.json"
$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.PSObject.Properties.Name -contains "includedFrameworks") {
    throw "TVBox 必须按 framework-dependent 方式发布，不能包含 includedFrameworks"
}
$frameworks = @($runtimeConfig.runtimeOptions.frameworks)
foreach ($frameworkName in @("Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App")) {
    if (-not ($frameworks | Where-Object { $_.name -eq $frameworkName })) {
        throw "TVBox.runtimeconfig.json 缺少框架引用: $frameworkName"
    }
}

$unexpectedLocales = @(Get-ChildItem -LiteralPath $localesDirectory -Force | Where-Object { $_.Name -ne 'winui' })
if ($unexpectedLocales.Count -gt 0) {
    throw "locales 包含未归类项目: $($unexpectedLocales.Name -join ', ')"
}
$unexpectedWinUiCultures = @(Get-ChildItem -LiteralPath $localizedWinUiDirectory -Directory | Where-Object {
    $winUiCultures -notcontains $_.Name
})
if ($unexpectedWinUiCultures.Count -gt 0) {
    throw "locales\winui 包含不支持的语言目录: $($unexpectedWinUiCultures.Name -join ', ')"
}
$misplacedMuiFiles = @(Get-ChildItem -LiteralPath $libsDirectory -Recurse -File -Filter '*.mui')
if ($misplacedMuiFiles.Count -gt 0) {
    throw "libs 不应包含语言资源: $($misplacedMuiFiles.FullName -join ', ')"
}

foreach ($frameworkDirectory in @($coreVersions[0].FullName, $desktopVersions[0].FullName)) {
    $unexpectedSatelliteDirectories = @(
        Get-ChildItem -LiteralPath $frameworkDirectory -Directory | Where-Object {
            $files = @(Get-ChildItem -LiteralPath $_.FullName -Recurse -File)
            $files.Count -gt 0 -and
                @($files | Where-Object { -not $_.Name.EndsWith('.resources.dll', [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0 -and
                $runtimeCultures -notcontains $_.Name
        }
    )
    if ($unexpectedSatelliteDirectories.Count -gt 0) {
        throw "runtime 包含不支持的语言目录: $($unexpectedSatelliteDirectories.Name -join ', ')"
    }
}

Assert-NoDuplicateFilesAcrossTrees -LeftRoot $libsDirectory -RightRoot $runtimeDirectory
Assert-NoDuplicateFilesAcrossTrees -LeftRoot $libsDirectory -RightRoot $localesDirectory

Test-ReleaseContent -Root $publishDirectory

Write-Host "Creating $([System.IO.Path]::GetFileName($zipPath))..."
Compress-Archive -LiteralPath $publishDirectory -DestinationPath $zipPath -CompressionLevel Optimal -Force

$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
$checksumPath = Join-Path $output "SHA256SUMS.txt"
$checksumLine = "$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($zipPath))"
$existing = @()
if (Test-Path -LiteralPath $checksumPath) {
    $zipName = [System.IO.Path]::GetFileName($zipPath)
    $existing = Get-Content -LiteralPath $checksumPath | Where-Object {
        $_ -notmatch ("  " + [regex]::Escape($zipName) + "$")
    }
}
@($existing) + $checksumLine | Set-Content -LiteralPath $checksumPath -Encoding ASCII

if ($ZipOnly) {
    Remove-OutputItem -Path $publishDirectory -Parent $output
}

Write-Host "Release package created: $zipPath"
Write-Host "SHA256: $($hash.Hash.ToLowerInvariant())"
