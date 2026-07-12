param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseDir
)

$ErrorActionPreference = "Stop"

$vlcVersion = "3.0.23"
$vlcSourceUrl = "https://download.videolan.org/pub/videolan/vlc/$vlcVersion/vlc-$vlcVersion.tar.xz"
$vlcSourceSha256 = "e891cae6aa3ccda69bf94173d5105cbc55c7a7d9b1d21b9b21666e69eff3e7e0"
$libVlcSharpVersion = "3.10.0"
$libVlcSharpCommit = "59d70e96026229e7c232ce5074ecefbf6f8959b6"
$libVlcNugetVersion = "3.0.23.1"
$libVlcNugetCommit = "042f49a49609b2da7aeea0c94e51f809cf2e1575"

function Assert-NativeCommandSucceeded {
    param([string]$FailureMessage)

    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Exit code: $LASTEXITCODE."
    }
}

function Get-NormalizedFullPath {
    param([string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $trimmedPath = $fullPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $trimmedPath.Replace(
        [System.IO.Path]::AltDirectorySeparatorChar,
        [System.IO.Path]::DirectorySeparatorChar)
}

function Resolve-ArtifactPath {
    param(
        [string]$Path,
        [string]$Name
    )

    $resolvedPath = if ([System.IO.Path]::IsPathRooted($Path)) {
        Get-NormalizedFullPath $Path
    } else {
        Get-NormalizedFullPath (Join-Path $repoRoot $Path)
    }

    if (!$resolvedPath.StartsWith(
            $artifactsRootWithSeparator,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name must resolve under the artifacts directory: $artifactsRoot"
    }

    $resolvedPath
}

function Add-VerifiedDownload {
    param(
        [string]$Uri,
        [string]$ExpectedSha256,
        [string]$DestinationPath
    )

    Invoke-WebRequest -Uri $Uri -OutFile $DestinationPath
    $actualSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $DestinationPath).Hash
    if (!$actualSha256.Equals($ExpectedSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Source archive SHA256 mismatch for $Uri. Expected $ExpectedSha256, but was $actualSha256."
    }
}

function Add-GitSourceArchive {
    param(
        [string]$RepositoryUrl,
        [string]$Commit,
        [string]$DestinationPath,
        [string]$CheckoutName
    )

    $checkoutPath = Join-Path $stagingRoot $CheckoutName
    & git clone --filter=blob:none --no-checkout $RepositoryUrl $checkoutPath
    Assert-NativeCommandSucceeded "Could not clone $RepositoryUrl."

    & git -C $checkoutPath fetch --depth 1 origin $Commit
    Assert-NativeCommandSucceeded "Could not fetch $Commit from $RepositoryUrl."

    $resolvedCommitOutput = & git -C $checkoutPath rev-parse FETCH_HEAD
    Assert-NativeCommandSucceeded "Could not resolve $Commit from $RepositoryUrl."
    $resolvedCommit = $resolvedCommitOutput.Trim()
    if (!$resolvedCommit.Equals($Commit, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Expected source commit $Commit, but resolved $resolvedCommit from $RepositoryUrl."
    }

    & git -C $checkoutPath archive --format=zip --output=$DestinationPath FETCH_HEAD
    Assert-NativeCommandSucceeded "Could not archive $Commit from $RepositoryUrl."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Get-NormalizedFullPath (Join-Path $repoRoot "artifacts")
$artifactsRootWithSeparator = $artifactsRoot + [System.IO.Path]::DirectorySeparatorChar
$releaseDir = Resolve-ArtifactPath $ReleaseDir "ReleaseDir"
$sourceManifestPath = Join-Path $repoRoot "SOURCE.LibVLC.txt"
$stagingRoot = Resolve-ArtifactPath (
    Join-Path "artifacts/videolan-source-staging" ([Guid]::NewGuid().ToString("N"))
) "SourceStagingPath"

if (!(Test-Path -LiteralPath $releaseDir -PathType Container)) {
    throw "Release directory does not exist: $releaseDir"
}

if (!(Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)) {
    throw "VideoLAN source manifest does not exist: $sourceManifestPath"
}

New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

try {
    $vlcSourcePath = Join-Path $releaseDir "vlc-$vlcVersion.tar.xz"
    Add-VerifiedDownload $vlcSourceUrl $vlcSourceSha256 $vlcSourcePath

    $libVlcSharpSourcePath = Join-Path $releaseDir "LibVLCSharp-$libVlcSharpVersion-source.zip"
    Add-GitSourceArchive `
        "https://code.videolan.org/videolan/LibVLCSharp.git" `
        $libVlcSharpCommit `
        $libVlcSharpSourcePath `
        "LibVLCSharp"

    $libVlcNugetSourcePath = Join-Path $releaseDir "libvlc-nuget-$libVlcNugetVersion-packaging-source.zip"
    Add-GitSourceArchive `
        "https://code.videolan.org/videolan/libvlc-nuget.git" `
        $libVlcNugetCommit `
        $libVlcNugetSourcePath `
        "libvlc-nuget"

    $releaseManifestPath = Join-Path $releaseDir "VideoLAN-SOURCE.txt"
    Copy-Item -LiteralPath $sourceManifestPath -Destination $releaseManifestPath

    $sourceFiles = @(
        Get-Item -LiteralPath $vlcSourcePath
        Get-Item -LiteralPath $libVlcSharpSourcePath
        Get-Item -LiteralPath $libVlcNugetSourcePath
        Get-Item -LiteralPath $releaseManifestPath
    ) | Sort-Object Name
    $checksumLines = $sourceFiles | ForEach-Object {
        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName
        "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), $_.Name
    }
    Set-Content `
        -LiteralPath (Join-Path $releaseDir "VideoLAN-source-assets.sha256") `
        -Value $checksumLines `
        -Encoding ASCII
} finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

Write-Host "Added VideoLAN source assets to $releaseDir"
