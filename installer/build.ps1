[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '1.0.10',

    [ValidateSet('Release')]
    [string] $Configuration = 'Release',

    [string] $PublishDirectory = '',

    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$portableScript = Join-Path $repoRoot 'scripts\Publish-Portable.ps1'
$installerProject = Join-Path $PSScriptRoot 'TVBox.Installer.wixproj'
$artifactsRoot = Join-Path $repoRoot 'artifacts'
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $publishDir = Join-Path $artifactsRoot "TVBox-x64-$Version"
}
elseif ([IO.Path]::IsPathRooted($PublishDirectory)) {
    $publishDir = [IO.Path]::GetFullPath($PublishDirectory)
}
else {
    $publishDir = [IO.Path]::GetFullPath((Join-Path $repoRoot $PublishDirectory))
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Get-ProductCode {
    param([Parameter(Mandatory)][string] $ProductVersion)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes("TVBox.Windows.x64/$ProductVersion"))
    }
    finally {
        $sha256.Dispose()
    }

    $hex = (($hash[0..15] | ForEach-Object { $_.ToString('x2') }) -join '')
    $hex = $hex.Substring(0, 12) + '5' + $hex.Substring(13)
    $variant = (([Convert]::ToInt32($hex.Substring(16, 1), 16) -band 3) -bor 8).ToString('x')
    $hex = $hex.Substring(0, 16) + $variant + $hex.Substring(17)

    return ('{0}-{1}-{2}-{3}-{4}' -f
        $hex.Substring(0, 8),
        $hex.Substring(8, 4),
        $hex.Substring(12, 4),
        $hex.Substring(16, 4),
        $hex.Substring(20, 12)).ToUpperInvariant()
}

function Assert-AppHostContract {
    param([Parameter(Mandatory)][string] $Path)

    $binaryText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($Path))
    foreach ($expected in @('libs\TVBox.dll', '..\runtime')) {
        if ($binaryText.IndexOf($expected, [StringComparison]::Ordinal) -lt 0) {
            throw "TVBox.exe apphost is missing embedded path: $expected"
        }
    }
}

function Assert-NoDuplicateFilesAcrossTrees {
    param(
        [Parameter(Mandatory)][string] $LeftRoot,
        [Parameter(Mandatory)][string] $RightRoot
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
                $duplicates.Add("$($leftFile.FullName) <=> $($rightFile.FullName) [$leftHash]")
            }
        }
    }

    if ($duplicates.Count -gt 0) {
        throw "Libs and runtime contain duplicate files with matching names and SHA256 hashes:`n$($duplicates -join "`n")"
    }
}

if (-not $SkipPublish) {
    if (-not (Test-Path -LiteralPath $portableScript -PathType Leaf)) {
        throw "Portable publish script is missing: $portableScript"
    }
    $expectedDirectoryName = "TVBox-x64-$Version"
    if ([IO.Path]::GetFileName($publishDir.TrimEnd('\', '/')) -ne $expectedDirectoryName) {
        throw "PublishDirectory must end with $expectedDirectoryName unless -SkipPublish is used."
    }
    & $portableScript -Architecture x64 -Version $Version -OutputRoot ([IO.Path]::GetDirectoryName($publishDir))
}

$mainExecutable = Join-Path $publishDir 'TVBox.exe'
if (-not (Test-Path -LiteralPath $mainExecutable -PathType Leaf)) {
    throw "Publish output is missing TVBox.exe: $mainExecutable"
}
Assert-AppHostContract -Path $mainExecutable

@(
    'LICENSE',
    'README.md',
    'THIRD-PARTY-NOTICES.md',
    'assets\icons\icon.ico',
    'assets\icons\icon.png',
    'assets\icons\icon-title.png',
    'assets\js\lib\cat.js',
    'node\node.exe',
    'node\catpaw-bootstrap.js',
    'node\LICENSE.txt',
    'node\SOURCE.txt',
    'ffmpeg\avcodec-62.dll',
    'ffmpeg\avdevice-62.dll',
    'ffmpeg\avfilter-11.dll',
    'ffmpeg\avformat-62.dll',
    'ffmpeg\avutil-60.dll',
    'ffmpeg\LICENSE.txt',
    'ffmpeg\SOURCE.txt',
    'ffmpeg\swresample-6.dll',
    'ffmpeg\swscale-9.dll',
    'libs\TVBox.dll',
    'libs\TVBox.deps.json',
    'libs\TVBox.runtimeconfig.json',
    'libs\Flyleaf.FFmpeg.Bindings.dll',
    'locales\winui\Microsoft.ui.xaml.dll',
    'locales\winui\Microsoft.UI.Xaml.Phone.dll',
    'locales\winui\zh-CN\Microsoft.ui.xaml.dll.mui',
    'locales\winui\zh-TW\Microsoft.ui.xaml.dll.mui'
) | ForEach-Object {
    $requiredPath = Join-Path $publishDir $_
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Publish output is missing required runtime file: $_"
    }
}

@('assets', 'ffmpeg', 'libs', 'locales', 'node', 'runtime') | ForEach-Object {
    $requiredPath = Join-Path $publishDir $_
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Container)) {
        throw "Publish output is missing required directory: $_"
    }
}

$hostFxrFiles = @(Get-ChildItem -LiteralPath (Join-Path $publishDir 'runtime\host\fxr') -Filter hostfxr.dll -Recurse -File)
if ($hostFxrFiles.Count -ne 1) {
    throw "Private runtime must contain exactly one hostfxr.dll; found $($hostFxrFiles.Count)."
}
$coreFrameworkRoot = Join-Path $publishDir 'runtime\shared\Microsoft.NETCore.App'
$coreVersions = @(Get-ChildItem -LiteralPath $coreFrameworkRoot -Directory)
if ($coreVersions.Count -ne 1 -or
    -not (Test-Path -LiteralPath (Join-Path $coreVersions[0].FullName '.version') -PathType Leaf)) {
    throw 'Private Microsoft.NETCore.App layout is invalid.'
}
$desktopFrameworkRoot = Join-Path $publishDir 'runtime\shared\Microsoft.WindowsDesktop.App'
$desktopVersions = @(Get-ChildItem -LiteralPath $desktopFrameworkRoot -Directory)
if ($desktopVersions.Count -ne 1 -or
    -not (Test-Path -LiteralPath (Join-Path $desktopVersions[0].FullName 'Microsoft.WindowsDesktop.App.deps.json') -PathType Leaf)) {
    throw 'Private Microsoft.WindowsDesktop.App layout is invalid.'
}

$versionInfo = (Get-Item -LiteralPath $mainExecutable).VersionInfo
if ($versionInfo.FileVersion -ne "$Version.0" -or
    $versionInfo.ProductVersion -ne $Version -or
    $versionInfo.ProductName -ne 'TVBox for Windows') {
    throw "TVBox.exe has incorrect version metadata: FileVersion=$($versionInfo.FileVersion), ProductVersion=$($versionInfo.ProductVersion), Product=$($versionInfo.ProductName)"
}

if (Test-Path -LiteralPath (Join-Path $publishDir 'libs\TVBox.exe')) {
    throw 'Libs directory must not contain a second TVBox.exe.'
}
$runtimeRoot = Join-Path $publishDir 'runtime'
foreach ($hostFile in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll')) {
    $matches = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File -Filter $hostFile)
    if ($matches.Count -ne 1 -or
        -not $matches[0].FullName.StartsWith($runtimeRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Private .NET host file is misplaced or duplicated: $hostFile"
    }
}

$runtimeConfig = Get-Content -LiteralPath (Join-Path $publishDir 'libs\TVBox.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.PSObject.Properties.Name -contains 'includedFrameworks') {
    throw 'TVBox must be framework-dependent and use the packaged private runtime.'
}
$frameworks = @($runtimeConfig.runtimeOptions.frameworks)
foreach ($frameworkName in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
    if (-not ($frameworks | Where-Object { $_.name -eq $frameworkName })) {
        throw "TVBox.runtimeconfig.json is missing framework: $frameworkName"
    }
}

$supportedWinUiCultures = @('en-us', 'ja-JP', 'ko-KR', 'zh-CN', 'zh-TW')
$localesRoot = Join-Path $publishDir 'locales'
$localizedWinUiRoot = Join-Path $localesRoot 'winui'
$unexpectedLocales = @(Get-ChildItem -LiteralPath $localesRoot -Force | Where-Object { $_.Name -ne 'winui' })
if ($unexpectedLocales.Count -gt 0) {
    throw "Locales contains unclassified items: $($unexpectedLocales.Name -join ', ')"
}
$unexpectedWinUiCultures = @(Get-ChildItem -LiteralPath $localizedWinUiRoot -Directory | Where-Object {
    $supportedWinUiCultures -notcontains $_.Name
})
if ($unexpectedWinUiCultures.Count -gt 0) {
    throw "Locales contains unsupported WinUI culture directories: $($unexpectedWinUiCultures.Name -join ', ')"
}
$misplacedMuiFiles = @(Get-ChildItem -LiteralPath (Join-Path $publishDir 'libs') -Recurse -File -Filter '*.mui')
if ($misplacedMuiFiles.Count -gt 0) {
    throw "Libs contains misplaced language resources: $($misplacedMuiFiles.FullName -join ', ')"
}

$supportedRuntimeCultures = @('ja', 'ko', 'zh-Hans', 'zh-Hant')
foreach ($frameworkDirectory in @($coreVersions[0].FullName, $desktopVersions[0].FullName)) {
    $unexpectedSatelliteDirectories = @(
        Get-ChildItem -LiteralPath $frameworkDirectory -Directory | Where-Object {
            $files = @(Get-ChildItem -LiteralPath $_.FullName -Recurse -File)
            $files.Count -gt 0 -and
                @($files | Where-Object { -not $_.Name.EndsWith('.resources.dll', [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0 -and
                $supportedRuntimeCultures -notcontains $_.Name
        }
    )
    if ($unexpectedSatelliteDirectories.Count -gt 0) {
        throw "Private runtime contains unsupported culture directories: $($unexpectedSatelliteDirectories.Name -join ', ')"
    }
}

Assert-NoDuplicateFilesAcrossTrees `
    -LeftRoot (Join-Path $publishDir 'libs') `
    -RightRoot (Join-Path $publishDir 'runtime')
Assert-NoDuplicateFilesAcrossTrees `
    -LeftRoot (Join-Path $publishDir 'libs') `
    -RightRoot $localesRoot

# Release packages must never contain machine-local preferences or test data.
$allowedRootItems = @(
    'TVBox.exe',
    'assets',
    'ffmpeg',
    'libs',
    'locales',
    'node',
    'runtime',
    'LICENSE',
    'README.md',
    'THIRD-PARTY-NOTICES.md'
)
$unexpectedRootItems = @(
    Get-ChildItem -LiteralPath $publishDir -Force | Where-Object {
        $allowedRootItems -notcontains $_.Name
    }
)
if ($unexpectedRootItems.Count -gt 0) {
    throw "Release root contains unclassified items: $($unexpectedRootItems.Name -join ', ')"
}

$blockedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
@(
    'prefs.json',
    'preferences.json',
    'settings.json',
    'configs.json',
    'wexfnwconfig.json',
    'setting_config.json',
    'history.json',
    'keep.json',
    'favorites.json',
    'sources.json',
    'cookies.json',
    'credentials.json',
    'tokens.json',
    'app.log'
) | ForEach-Object { [void] $blockedNames.Add($_) }

$blockedExtensions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
@('.pdb', '.log', '.tmp', '.bak', '.dmp') | ForEach-Object { [void] $blockedExtensions.Add($_) }

$blockedFiles = @(
    Get-ChildItem -LiteralPath $publishDir -Recurse -File | Where-Object {
        $blockedNames.Contains($_.Name) -or $blockedExtensions.Contains($_.Extension)
    }
)

if ($blockedFiles.Count -gt 0) {
    $relativePaths = $blockedFiles | ForEach-Object {
        $_.FullName.Substring($publishDir.TrimEnd([IO.Path]::DirectorySeparatorChar).Length + 1)
    }
    throw "Release validation found user-data or transient files:`n$($relativePaths -join [Environment]::NewLine)"
}

$blockedRootDirectories = @('cache', 'js', 'live', 'wall', 'restore', 'local')
$unexpectedDirectories = @(
    Get-ChildItem -LiteralPath $publishDir -Directory | Where-Object {
        $blockedRootDirectories -contains $_.Name.ToLowerInvariant()
    }
)
if ($unexpectedDirectories.Count -gt 0) {
    throw "Release validation found runtime user-data directories: $($unexpectedDirectories.Name -join ', ')"
}

$textExtensions = @('.config', '.json', '.js', '.md', '.txt', '.xml', '.yaml', '.yml')
$sensitivePatterns = [ordered]@{
    'local Windows user path' = '(?i)[A-Z]:\\Users\\[^\r\n`"'']+'
    'credential-bearing URL' = '(?i)https?://[^\s/@:]+:[^\s/@]+@'
    'private key' = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
    'GitHub token' = '(?i)\b(?:ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b'
}
foreach ($file in Get-ChildItem -LiteralPath $publishDir -Recurse -File) {
    if ($textExtensions -notcontains $file.Extension.ToLowerInvariant() -or $file.Length -gt 10MB) {
        continue
    }

    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($entry in $sensitivePatterns.GetEnumerator()) {
        if ($content -match $entry.Value) {
            $relativePath = $file.FullName.Substring($publishDir.TrimEnd([IO.Path]::DirectorySeparatorChar).Length + 1)
            throw "Release validation found $($entry.Key): $relativePath"
        }
    }
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
$installer = Join-Path $artifactsRoot "TVBox-Setup-x64-$Version.msi"
$productCode = Get-ProductCode -ProductVersion $Version
if (Test-Path -LiteralPath $installer) {
    Remove-Item -LiteralPath $installer -Force
}

Invoke-DotNet -Arguments @(
    'build', $installerProject,
    '--configuration', $Configuration,
    '--nologo',
    '--no-incremental',
    '--warnaserror',
    '-p:Platform=x64',
    "-p:ProductVersion=$Version",
    "-p:ProductCode=$productCode",
    "-p:PublishDir=$publishDir",
    "-p:OutputPath=$artifactsRoot"
)

if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw "WiX completed without producing the expected installer: $installer"
}

$installerFile = Get-Item -LiteralPath $installer
$hash = Get-FileHash -LiteralPath $installer -Algorithm SHA256
$checksumPath = Join-Path $artifactsRoot 'SHA256SUMS.txt'
$checksumLine = "$($hash.Hash.ToLowerInvariant())  $($installerFile.Name)"
$existingChecksums = @()
if (Test-Path -LiteralPath $checksumPath -PathType Leaf) {
    $existingChecksums = Get-Content -LiteralPath $checksumPath | Where-Object {
        $_ -notmatch ('  ' + [regex]::Escape($installerFile.Name) + '$')
    }
}
@($existingChecksums) + $checksumLine | Set-Content -LiteralPath $checksumPath -Encoding ASCII

Write-Host "Installer: $($installerFile.FullName)"
Write-Host "Size:      $($installerFile.Length) bytes"
Write-Host "SHA256:    $($hash.Hash)"
