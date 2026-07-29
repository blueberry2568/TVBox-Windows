[CmdletBinding()]
param(
    [string] $Project = '',

    [Parameter(Mandatory = $true)]
    [string] $Destination,

    [ValidateSet('win-x64')]
    [string] $RuntimeIdentifier = 'win-x64',

    [switch] $SkipRestore,

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    return [IO.Path]::GetFullPath($Path)
}

function Assert-SafeDestination {
    param([Parameter(Mandatory = $true)][string] $Path)

    $fullPath = Get-FullPath $Path
    $root = [IO.Path]::GetPathRoot($fullPath).TrimEnd('\', '/')
    if ($fullPath.TrimEnd('\', '/') -eq $root) {
        throw "Refusing to use a drive root as the private .NET destination: $fullPath"
    }

    return $fullPath
}

function Get-RuntimePack {
    param(
        [Parameter(Mandatory = $true)] $Assets,
        [Parameter(Mandatory = $true)][string] $PackageId
    )

    $libraries = @(
        $Assets.libraries.PSObject.Properties | Where-Object {
            $_.Name.StartsWith("$PackageId/", [StringComparison]::OrdinalIgnoreCase)
        }
    )

    $packagePath = ''
    $packageVersion = ''
    if ($libraries.Count -eq 1) {
        $packagePath = [string] $libraries[0].Value.path
        $packageVersion = $libraries[0].Name.Substring($PackageId.Length + 1)
    }
    elseif ($libraries.Count -gt 1) {
        throw "Expected one restored $PackageId package, found $($libraries.Count)."
    }
    else {
        # Runtime packs are PackageDownload entries, so NuGet does not normally
        # list them in the top-level libraries object.
        $downloads = @(
            $Assets.project.frameworks.PSObject.Properties.Value |
                ForEach-Object { $_.downloadDependencies } |
                Where-Object { $_.name -eq $PackageId }
        )
        $versions = @($downloads | ForEach-Object { ([string] $_.version).Trim('[', ']').Split(',')[0].Trim() } | Select-Object -Unique)
        if ($versions.Count -ne 1 -or [string]::IsNullOrWhiteSpace($versions[0])) {
            throw "Expected one restored $PackageId PackageDownload version, found $($versions -join ', ')."
        }

        $packageVersion = $versions[0]
        $packagePath = "$($PackageId.ToLowerInvariant())/$packageVersion"
    }

    foreach ($folder in $Assets.packageFolders.PSObject.Properties) {
        $candidate = Join-Path ([string] $folder.Name) $packagePath
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            return [pscustomobject]@{
                Id      = $PackageId
                Version = $packageVersion
                Path    = Get-FullPath $candidate
            }
        }
    }

    throw "Could not locate restored package contents for $PackageId/$packageVersion."
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string] $Source,
        [Parameter(Mandatory = $true)][string] $Target,
        [string[]] $ExcludedFileNames = @()
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Runtime pack directory is missing: $Source"
    }

    New-Item -ItemType Directory -Path $Target -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        if (-not $_.PSIsContainer -and $ExcludedFileNames -contains $_.Name) {
            return
        }

        Copy-Item -LiteralPath $_.FullName -Destination $Target -Recurse -Force
    }
}

function Assert-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $RelativePath
    )

    $path = Join-Path $Root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Private .NET root is incomplete; missing $RelativePath"
    }
}

$repoRoot = Get-FullPath (Join-Path $PSScriptRoot '..')
if ([string]::IsNullOrWhiteSpace($Project)) {
    $Project = Join-Path $repoRoot 'windows\TVBox.Windows\TVBox.Windows.csproj'
}
elseif (-not [IO.Path]::IsPathRooted($Project)) {
    $Project = Join-Path $repoRoot $Project
}
$Project = Get-FullPath $Project

if (-not (Test-Path -LiteralPath $Project -PathType Leaf)) {
    throw "Project file does not exist: $Project"
}

$destinationRoot = Assert-SafeDestination $Destination
if (Test-Path -LiteralPath $destinationRoot) {
    $existingItems = @(Get-ChildItem -LiteralPath $destinationRoot -Force)
    if ($existingItems.Count -gt 0 -and -not $Force) {
        throw "Private .NET destination is not empty. Pass -Force to replace it: $destinationRoot"
    }
    if ($Force) {
        Remove-Item -LiteralPath $destinationRoot -Recurse -Force
    }
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw 'The .NET SDK is required to restore the private runtime packs.'
}

if (-not $SkipRestore) {
    & $dotnet.Source restore $Project --runtime $RuntimeIdentifier '-p:SelfContained=true'
    if ($LASTEXITCODE -ne 0) {
        throw "Runtime-pack restore failed with exit code $LASTEXITCODE."
    }
}

$assetsFile = Join-Path ([IO.Path]::GetDirectoryName($Project)) 'obj\project.assets.json'
if (-not (Test-Path -LiteralPath $assetsFile -PathType Leaf)) {
    throw "NuGet assets file does not exist: $assetsFile"
}

$assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json
$corePack = Get-RuntimePack -Assets $assets -PackageId "Microsoft.NETCore.App.Runtime.$RuntimeIdentifier"
$desktopPack = Get-RuntimePack -Assets $assets -PackageId "Microsoft.WindowsDesktop.App.Runtime.$RuntimeIdentifier"
if ($corePack.Version -ne $desktopPack.Version) {
    throw "The private runtime packs must use the same patch: Core=$($corePack.Version), Desktop=$($desktopPack.Version)."
}

$frameworkVersion = $corePack.Version
$coreLib = Join-Path $corePack.Path 'runtimes\win-x64\lib\net9.0'
$coreNative = Join-Path $corePack.Path 'runtimes\win-x64\native'
$desktopLib = Join-Path $desktopPack.Path 'runtimes\win-x64\lib\net9.0'
$desktopNative = Join-Path $desktopPack.Path 'runtimes\win-x64\native'

$hostFxrDirectory = Join-Path $destinationRoot "host\fxr\$frameworkVersion"
$coreFrameworkDirectory = Join-Path $destinationRoot "shared\Microsoft.NETCore.App\$frameworkVersion"
$desktopFrameworkDirectory = Join-Path $destinationRoot "shared\Microsoft.WindowsDesktop.App\$frameworkVersion"

New-Item -ItemType Directory -Path $hostFxrDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $coreNative 'hostfxr.dll') -Destination $hostFxrDirectory -Force

Copy-DirectoryContents -Source $coreLib -Target $coreFrameworkDirectory
Copy-DirectoryContents -Source $coreNative -Target $coreFrameworkDirectory -ExcludedFileNames @('hostfxr.dll')
Copy-Item -LiteralPath (Join-Path $corePack.Path 'Microsoft.NETCore.App.versions.txt') `
    -Destination (Join-Path $coreFrameworkDirectory '.version') -Force

Copy-DirectoryContents -Source $desktopLib -Target $desktopFrameworkDirectory
Copy-DirectoryContents -Source $desktopNative -Target $desktopFrameworkDirectory

$coreLicense = Join-Path $corePack.Path 'LICENSE.TXT'
if (Test-Path -LiteralPath $coreLicense -PathType Leaf) {
    Copy-Item -LiteralPath $coreLicense -Destination (Join-Path $destinationRoot 'LICENSE.txt') -Force
}
$desktopLicense = Join-Path $desktopPack.Path 'LICENSE'
if (Test-Path -LiteralPath $desktopLicense -PathType Leaf) {
    Copy-Item -LiteralPath $desktopLicense -Destination (Join-Path $destinationRoot 'LICENSE.WindowsDesktop.txt') -Force
}
$notices = Join-Path $corePack.Path 'THIRD-PARTY-NOTICES.TXT'
if (Test-Path -LiteralPath $notices -PathType Leaf) {
    Copy-Item -LiteralPath $notices -Destination (Join-Path $destinationRoot 'THIRD-PARTY-NOTICES.txt') -Force
}

@(
    "host\fxr\$frameworkVersion\hostfxr.dll",
    "shared\Microsoft.NETCore.App\$frameworkVersion\coreclr.dll",
    "shared\Microsoft.NETCore.App\$frameworkVersion\hostpolicy.dll",
    "shared\Microsoft.NETCore.App\$frameworkVersion\System.Private.CoreLib.dll",
    "shared\Microsoft.NETCore.App\$frameworkVersion\Microsoft.NETCore.App.deps.json",
    "shared\Microsoft.NETCore.App\$frameworkVersion\.version",
    "shared\Microsoft.WindowsDesktop.App\$frameworkVersion\PresentationFramework.dll",
    "shared\Microsoft.WindowsDesktop.App\$frameworkVersion\System.Windows.Forms.dll",
    "shared\Microsoft.WindowsDesktop.App\$frameworkVersion\Microsoft.WindowsDesktop.App.deps.json"
) | ForEach-Object {
    Assert-RequiredFile -Root $destinationRoot -RelativePath $_
}

if (Test-Path -LiteralPath (Join-Path $coreFrameworkDirectory 'hostfxr.dll')) {
    throw 'hostfxr.dll must live under runtime\host\fxr, not under a shared framework.'
}

Write-Host "Private .NET runtime $frameworkVersion assembled at $destinationRoot"
