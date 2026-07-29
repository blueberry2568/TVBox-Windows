[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '1.0.2',

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

@(
    'README.md',
    'THIRD-PARTY-NOTICES.md',
    'app\TVBox.exe',
    'app\TVBox.dll',
    'app\TVBox.deps.json',
    'app\TVBox.runtimeconfig.json',
    'app\THIRD-PARTY-NOTICES.md',
    'app\Flyleaf.FFmpeg.Bindings.dll',
    'app\Assets\icon.ico',
    'app\Assets\node\node.exe',
    'app\Assets\node\LICENSE.txt',
    'app\Assets\node\SOURCE.txt',
    'app\Assets\js\lib\cat.js',
    'app\ffmpeg\avcodec-62.dll',
    'app\ffmpeg\avdevice-62.dll',
    'app\ffmpeg\avfilter-11.dll',
    'app\ffmpeg\avformat-62.dll',
    'app\ffmpeg\avutil-60.dll',
    'app\ffmpeg\LICENSE.txt',
    'app\ffmpeg\SOURCE.txt',
    'app\ffmpeg\swresample-6.dll',
    'app\ffmpeg\swscale-9.dll'
) | ForEach-Object {
    $requiredPath = Join-Path $publishDir $_
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Publish output is missing required runtime file: $_"
    }
}

# Release packages must never contain machine-local preferences or test data.
$allowedRootItems = @('app', 'TVBox.exe', 'README.md', 'THIRD-PARTY-NOTICES.md')
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

$blockedRootDirectories = @('cache', 'js', 'live', 'wall', 'restore', 'local', 'node')
$runtimeRoot = Join-Path $publishDir 'app'
$unexpectedDirectories = @(
    Get-ChildItem -LiteralPath $publishDir -Directory | Where-Object {
        $blockedRootDirectories -contains $_.Name.ToLowerInvariant()
    }
    Get-ChildItem -LiteralPath $runtimeRoot -Directory | Where-Object {
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
    $currentVersionPattern = '  TVBox-(?:Setup-)?x64-' + [regex]::Escape($Version) + '\.(?:zip|msi)$'
    $existingChecksums = Get-Content -LiteralPath $checksumPath | Where-Object {
        $_ -match $currentVersionPattern -and $_ -notmatch ('  ' + [regex]::Escape($installerFile.Name) + '$')
    }
}
@($existingChecksums) + $checksumLine | Set-Content -LiteralPath $checksumPath -Encoding ASCII

Write-Host "Installer: $($installerFile.FullName)"
Write-Host "Size:      $($installerFile.Length) bytes"
Write-Host "SHA256:    $($hash.Hash)"
