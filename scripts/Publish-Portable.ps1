[CmdletBinding()]
param(
    [ValidateSet("x64")]
    [string]$Architecture = "x64",

    [ValidatePattern("^[0-9A-Za-z][0-9A-Za-z._-]*$")]
    [string]$Version = "1.0.1",

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

    $badDirectories = Get-ChildItem -LiteralPath $Root -Directory | Where-Object {
        $forbiddenRootDirectories -contains $_.Name.ToLowerInvariant()
    }
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
$zipPath = Assert-ChildPath -Path (Join-Path $output "$packageName.zip") -Parent $output

Remove-OutputItem -Path $publishDirectory -Parent $output
Remove-OutputItem -Path $zipPath -Parent $output
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

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
        -o $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败，退出代码: $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$requiredFiles = @(
    "TVBox.exe",
    "TVBox.dll",
    "TVBox.deps.json",
    "TVBox.runtimeconfig.json",
    "THIRD-PARTY-NOTICES.md",
    "Assets\node\node.exe",
    "Assets\node\LICENSE.txt",
    "Assets\node\SOURCE.txt",
    "Assets\js\lib\cat.js",
    "ffmpeg\avcodec-61.dll",
    "ffmpeg\avformat-61.dll",
    "ffmpeg\avutil-59.dll",
    "ffmpeg\LICENSE.txt",
    "ffmpeg\SOURCE.txt",
    "ffmpeg\swresample-5.dll",
    "ffmpeg\swscale-8.dll"
)
foreach ($relativePath in $requiredFiles) {
    Assert-RequiredFile -Root $publishDirectory -RelativePath $relativePath
}

$readme = Join-Path $repoRoot "README.md"
if (Test-Path -LiteralPath $readme -PathType Leaf) {
    Copy-Item -LiteralPath $readme -Destination (Join-Path $publishDirectory "README.md") -Force
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
