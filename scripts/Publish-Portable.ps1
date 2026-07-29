[CmdletBinding()]
param(
    [ValidateSet("x64")]
    [string]$Architecture = "x64",

    [ValidatePattern("^\d+\.\d+\.\d+$")]
    [string]$Version = "1.0.2",

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

function Test-ReleaseContent {
    param([Parameter(Mandatory = $true)][string]$Root)

    $allowedRootItems = @("app", "TVBox.exe", "README.md", "THIRD-PARTY-NOTICES.md")
    $unexpectedRootItems = Get-ChildItem -LiteralPath $Root -Force | Where-Object {
        $allowedRootItems -notcontains $_.Name
    }
    if ($unexpectedRootItems) {
        throw "发布目录顶层包含未归类项目: $($unexpectedRootItems.Name -join ', ')"
    }

    $forbiddenNames = @(
        "prefs.json",
        "configs.json",
        "history.json",
        "keep.json",
        "app.log",
        "wexfnwconfig.json"
    )
    $forbiddenRootDirectories = @("cache", "js", "live", "wall", "restore", "local", "node")

    $badFiles = Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object {
        $forbiddenNames -contains $_.Name.ToLowerInvariant()
    }
    if ($badFiles) {
        $relative = $badFiles | ForEach-Object { $_.FullName.Substring($Root.Length).TrimStart('\', '/') }
        throw "发布输出包含用户数据或本地源文件:`n$($relative -join "`n")"
    }

    $runtimeRoot = Join-Path $Root "app"
    $badDirectories = @(
        Get-ChildItem -LiteralPath $Root -Directory | Where-Object {
            $forbiddenRootDirectories -contains $_.Name.ToLowerInvariant()
        }
        Get-ChildItem -LiteralPath $runtimeRoot -Directory | Where-Object {
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

function Get-FrameworkCompiler {
    $candidates = @(
        (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
        (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
    )
    $compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $compiler) { throw "未找到 Windows .NET Framework C# 编译器，无法生成 TVBox 启动器。" }
    return $compiler
}

function Build-Launcher {
    param(
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$BuildDirectory,
        [Parameter(Mandatory = $true)][string]$ProductVersion
    )

    $source = Join-Path $repoRoot "launcher\TVBoxLauncher.cs"
    $icon = Join-Path $repoRoot "windows\TVBox.Windows\Assets\icon.ico"
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "找不到启动器源码: $source" }
    if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) { throw "找不到启动器图标: $icon" }

    New-Item -ItemType Directory -Path $BuildDirectory -Force | Out-Null
    $assemblyInfo = Join-Path $BuildDirectory "LauncherAssemblyInfo.cs"
    $attributes = @"
using System.Reflection;
[assembly: AssemblyTitle("TVBox")]
[assembly: AssemblyProduct("TVBox for Windows")]
[assembly: AssemblyCompany("TVBox Windows contributors")]
[assembly: AssemblyDescription("TVBox for Windows launcher")]
[assembly: AssemblyVersion("$ProductVersion.0")]
[assembly: AssemblyFileVersion("$ProductVersion.0")]
"@
    [System.IO.File]::WriteAllText($assemblyInfo, $attributes, [System.Text.UTF8Encoding]::new($false))

    $compiler = Get-FrameworkCompiler
    & $compiler /nologo /target:winexe /optimize+ /platform:x64 `
        "/win32icon:$icon" "/out:$Destination" $source $assemblyInfo
    if ($LASTEXITCODE -ne 0) { throw "TVBox 启动器编译失败，退出代码: $LASTEXITCODE" }
}

$repoRoot = Get-FullPath (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "windows\TVBox.Windows\TVBox.Windows.csproj"
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "找不到项目文件: $project"
}

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
$appDirectory = Assert-ChildPath -Path (Join-Path $publishDirectory "app") -Parent $publishDirectory
$launcherBuildDirectory = Assert-ChildPath -Path (Join-Path $output ".launcher-$Version") -Parent $output
$zipPath = Assert-ChildPath -Path (Join-Path $output "$packageName.zip") -Parent $output

Remove-OutputItem -Path $publishDirectory -Parent $output
Remove-OutputItem -Path $launcherBuildDirectory -Parent $output
Remove-OutputItem -Path $zipPath -Parent $output
New-Item -ItemType Directory -Force -Path $appDirectory | Out-Null

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw "未找到 dotnet。请安装 .NET 9 SDK。" }

Write-Host "Publishing $packageName..."
Push-Location $repoRoot
try {
    & $dotnet.Source publish $project `
        -c Release `
        "-p:Platform=$platform" `
        -r $rid `
        --self-contained true `
        -warnaserror `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        -o $appDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败，退出代码: $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

Build-Launcher `
    -Destination (Join-Path $publishDirectory "TVBox.exe") `
    -BuildDirectory $launcherBuildDirectory `
    -ProductVersion $Version
Remove-OutputItem -Path $launcherBuildDirectory -Parent $output

$readme = Join-Path $repoRoot "README.md"
if (Test-Path -LiteralPath $readme -PathType Leaf) {
    Copy-Item -LiteralPath $readme -Destination (Join-Path $publishDirectory "README.md") -Force
}
$notices = Join-Path $repoRoot "THIRD-PARTY-NOTICES.md"
if (Test-Path -LiteralPath $notices -PathType Leaf) {
    Copy-Item -LiteralPath $notices -Destination (Join-Path $publishDirectory "THIRD-PARTY-NOTICES.md") -Force
}

$requiredFiles = @(
    "TVBox.exe",
    "README.md",
    "THIRD-PARTY-NOTICES.md",
    "app\TVBox.exe",
    "app\TVBox.dll",
    "app\TVBox.deps.json",
    "app\TVBox.runtimeconfig.json",
    "app\THIRD-PARTY-NOTICES.md",
    "app\Flyleaf.FFmpeg.Bindings.dll",
    "app\Assets\icon.ico",
    "app\Assets\node\node.exe",
    "app\Assets\node\LICENSE.txt",
    "app\Assets\node\SOURCE.txt",
    "app\Assets\js\lib\cat.js",
    "app\ffmpeg\avcodec-62.dll",
    "app\ffmpeg\avdevice-62.dll",
    "app\ffmpeg\avfilter-11.dll",
    "app\ffmpeg\avformat-62.dll",
    "app\ffmpeg\avutil-60.dll",
    "app\ffmpeg\LICENSE.txt",
    "app\ffmpeg\SOURCE.txt",
    "app\ffmpeg\swresample-6.dll",
    "app\ffmpeg\swscale-9.dll"
)
foreach ($relativePath in $requiredFiles) {
    Assert-RequiredFile -Root $publishDirectory -RelativePath $relativePath
}

Test-ReleaseContent -Root $publishDirectory

Write-Host "Creating $([System.IO.Path]::GetFileName($zipPath))..."
Compress-Archive -LiteralPath $publishDirectory -DestinationPath $zipPath -CompressionLevel Optimal -Force

$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
$checksumPath = Join-Path $output "SHA256SUMS.txt"
$checksumLine = "$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($zipPath))"
$existing = @()
if (Test-Path -LiteralPath $checksumPath) {
    $zipName = [System.IO.Path]::GetFileName($zipPath)
    $currentVersionPattern = "  TVBox-(?:Setup-)?x64-$([regex]::Escape($Version))\.(?:zip|msi)$"
    $existing = Get-Content -LiteralPath $checksumPath | Where-Object {
        $_ -match $currentVersionPattern -and $_ -notmatch ("  " + [regex]::Escape($zipName) + "$")
    }
}
@($existing) + $checksumLine | Set-Content -LiteralPath $checksumPath -Encoding ASCII

if ($ZipOnly) {
    Remove-OutputItem -Path $publishDirectory -Parent $output
}

Write-Host "Release package created: $zipPath"
Write-Host "SHA256: $($hash.Hash.ToLowerInvariant())"
