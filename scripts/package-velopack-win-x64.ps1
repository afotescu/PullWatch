param(
    [Parameter(Mandatory = $true)]
    [string]$PublishPath,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseDir,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$ChecksumFileName,

    [string]$WorkDir,

    [switch]$NoBase,

    [string]$PackId = "PullWatch.Desktop",
    [string]$PackTitle = "PullWatch",
    [string]$MainExe = "PullWatch.exe",
    [string]$IconPath = "PullWatch.App/Assets/favicon.ico",
    [string]$Runtime = "win-x64",
    [string]$ReleaseNotesPath
)

$ErrorActionPreference = "Stop"

function Assert-NativeCommandSucceeded {
    param(
        [string]$FailureMessage
    )

    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Exit code: $LASTEXITCODE."
    }
}

function Resolve-RepoPath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Resolve-ArtifactPath {
    param(
        [string]$Path,
        [string]$Name
    )

    $resolvedPath = Resolve-RepoPath $Path
    if (!$resolvedPath.StartsWith(
            $artifactsRootWithSeparator,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name must resolve under the artifacts directory: $artifactsRoot"
    }

    return $resolvedPath
}

function Test-PathContains {
    param(
        [string]$Container,
        [string]$Candidate
    )

    $containerWithSeparator = $Container.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    return $Candidate.StartsWith(
        $containerWithSeparator,
        [System.StringComparison]::OrdinalIgnoreCase)
}

function ConvertTo-PackageVersion {
    param(
        [string]$Value,
        [string]$FileName
    )

    try {
        return [System.Management.Automation.SemanticVersion]$Value
    } catch {
        throw "Could not read a package version from the work directory file: $FileName"
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$artifactsRootWithSeparator = $artifactsRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$') {
    throw "Version must look like 0.1.0 or 0.1.0-rc.1. Received: $Version"
}

$versionCore = ($Version -split '-', 2)[0]
if ([version]$versionCore -lt [version]"0.0.1") {
    throw "Version must be >= 0.0.1 because Velopack rejects lower package versions. Received: $Version"
}

if ([string]::IsNullOrWhiteSpace($ChecksumFileName) -or
    $ChecksumFileName -ne [System.IO.Path]::GetFileName($ChecksumFileName)) {
    throw "ChecksumFileName must be a file name, not a path."
}

$publishPath = Resolve-ArtifactPath $PublishPath "PublishPath"
$releaseDir = Resolve-ArtifactPath $ReleaseDir "ReleaseDir"

if ([string]::IsNullOrWhiteSpace($WorkDir)) {
    $WorkDir = "$ReleaseDir-work"
}

$workDirectory = Resolve-ArtifactPath $WorkDir "WorkDir"

if ($workDirectory.Equals($releaseDir, [System.StringComparison]::OrdinalIgnoreCase) -or
    (Test-PathContains $workDirectory $releaseDir) -or
    (Test-PathContains $releaseDir $workDirectory)) {
    throw "WorkDir and ReleaseDir must be separate directories. WorkDir: $workDirectory, ReleaseDir: $releaseDir"
}

if ($publishPath.Equals($workDirectory, [System.StringComparison]::OrdinalIgnoreCase) -or
    (Test-PathContains $workDirectory $publishPath) -or
    (Test-PathContains $publishPath $workDirectory)) {
    throw "PublishPath and WorkDir must be separate directories. PublishPath: $publishPath, WorkDir: $workDirectory"
}

if ($publishPath.Equals($releaseDir, [System.StringComparison]::OrdinalIgnoreCase) -or
    (Test-PathContains $releaseDir $publishPath) -or
    (Test-PathContains $publishPath $releaseDir)) {
    throw "PublishPath and ReleaseDir must be separate directories. PublishPath: $publishPath, ReleaseDir: $releaseDir"
}

$resolvedIconPath = Resolve-RepoPath $IconPath
$resolvedReleaseNotesPath = $null
$vpkPath = Join-Path $repoRoot ".tools/vpk.exe"

if ($PSBoundParameters.ContainsKey("ReleaseNotesPath")) {
    if ([string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
        throw "ReleaseNotesPath must not be empty when provided."
    }

    $resolvedReleaseNotesPath = Resolve-RepoPath $ReleaseNotesPath
    if (!(Test-Path -LiteralPath $resolvedReleaseNotesPath -PathType Leaf)) {
        throw "Release notes file does not exist: $resolvedReleaseNotesPath"
    }

    $releaseNotes = Get-Content -LiteralPath $resolvedReleaseNotesPath -Raw
    if ([string]::IsNullOrWhiteSpace($releaseNotes)) {
        throw "Release notes must not be empty: $resolvedReleaseNotesPath"
    }

    if ($releaseNotes.Contains("release-notes-template:")) {
        throw "Replace the template instructions before packaging: $resolvedReleaseNotesPath"
    }

    if ($releaseNotes -match '(?m)^## Commit reference\r?$') {
        throw "Remove the commit reference section before packaging: $resolvedReleaseNotesPath"
    }
}

if (!(Test-Path -LiteralPath $publishPath)) {
    throw "Publish path does not exist: $publishPath"
}

if (!(Test-Path -LiteralPath $resolvedIconPath)) {
    throw "Icon path does not exist: $resolvedIconPath"
}

if (!(Test-Path -LiteralPath $vpkPath)) {
    throw "Velopack CLI was not found: $vpkPath"
}

# The work directory holds the downloaded base package that Velopack needs to
# build a delta, so it is only cleaned here when the caller declared that no
# base exists. Otherwise the caller owns preparing and populating it.
if ($NoBase) {
    if (Test-Path -LiteralPath $workDirectory) {
        Remove-Item -LiteralPath $workDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $workDirectory | Out-Null
} elseif (!(Test-Path -LiteralPath $workDirectory -PathType Container)) {
    throw "Work directory does not exist: $workDirectory. Create it and download the delta base package first, or pass -NoBase."
}

$escapedPackId = [regex]::Escape($PackId)
$escapedVersion = [regex]::Escape($Version)
$setupFileName = "$PackId-win-Setup.exe"
$assetsFileName = "assets.win.json"
$releasesJsonFileName = "releases.win.json"
$releasesFileName = "RELEASES"
$targetFullFileName = "$PackId-$Version-full.nupkg"
$targetDeltaFileName = "$PackId-$Version-delta.nupkg"

$workFilesBeforePack = @(Get-ChildItem -LiteralPath $workDirectory -File)

$conflictingPackages = @(
    $workFilesBeforePack | Where-Object {
        $_.Name -match "^$escapedPackId-$escapedVersion-(full|delta)\.nupkg$"
    }
)
if ($conflictingPackages.Count -gt 0) {
    $conflictNames = $conflictingPackages.Name -join ", "
    throw "The work directory already contains package(s) for version ${Version}: $conflictNames"
}

$targetVersion = [System.Management.Automation.SemanticVersion]$Version
$basePackages = @(
    $workFilesBeforePack | ForEach-Object {
        if ($_.Name -match "^$escapedPackId-(?<version>.+)-full\.nupkg$") {
            [pscustomobject]@{
                Name = $_.Name
                Version = ConvertTo-PackageVersion $Matches["version"] $_.Name
            }
        }
    }
)

$newerBasePackages = @(
    $basePackages | Where-Object {
        $_.Version.CompareTo($targetVersion) -gt 0
    }
)
if ($newerBasePackages.Count -gt 0) {
    $newerSummary = ($newerBasePackages | ForEach-Object { $_.Name }) -join ", "
    throw "The work directory contains package(s) newer than ${Version}: $newerSummary. Velopack cannot pack an older version over them."
}

$eligibleBasePackages = @(
    $basePackages | Where-Object {
        $_.Version.CompareTo($targetVersion) -lt 0
    }
)

if ($NoBase) {
    Write-Host "Packaging without a base package; Velopack will not generate a delta."
} elseif ($eligibleBasePackages.Count -eq 0) {
    throw "No $PackId base package older than $Version was found in $workDirectory. Download the previous release into the work directory, or pass -NoBase to publish a full package without a delta."
} else {
    $baseSummary = ($eligibleBasePackages | ForEach-Object { $_.Name }) -join ", "
    Write-Host "Base package(s) available for delta generation: $baseSummary"
}

if (Test-Path -LiteralPath $releaseDir) {
    Remove-Item -LiteralPath $releaseDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

$vpkArguments = @(
    "pack",
    "--packId",
    $PackId,
    "--packTitle",
    $PackTitle,
    "--packVersion",
    $Version,
    "--packDir",
    $publishPath,
    "--mainExe",
    $MainExe,
    "--outputDir",
    $workDirectory,
    "--icon",
    $resolvedIconPath,
    "--shortcuts",
    "Desktop,StartMenuRoot",
    "--runtime",
    $Runtime,
    "--noPortable"
)

if ($NoBase) {
    $vpkArguments += @(
        "--delta",
        "None"
    )
}

if ($null -ne $resolvedReleaseNotesPath) {
    $vpkArguments += @(
        "--releaseNotes",
        $resolvedReleaseNotesPath
    )
}

& $vpkPath @vpkArguments
Assert-NativeCommandSucceeded "Velopack pack failed."

$assetsFile = Join-Path $workDirectory $assetsFileName
if (!(Test-Path -LiteralPath $assetsFile -PathType Leaf)) {
    throw "Velopack did not produce the asset manifest: $assetsFile"
}

$assets = @(Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json)
if ($assets.Count -eq 0) {
    throw "Velopack produced an empty asset manifest: $assetsFile"
}

foreach ($asset in $assets) {
    if ([string]::IsNullOrWhiteSpace($asset.RelativeFileName) -or
        $asset.RelativeFileName -ne [System.IO.Path]::GetFileName($asset.RelativeFileName)) {
        throw "Asset manifest entries must be plain file names. Received: $($asset.RelativeFileName)"
    }
}

$fullAssets = @($assets | Where-Object { $_.Type -eq "Full" })
if ($fullAssets.Count -ne 1) {
    throw "Velopack should publish exactly one full package for $Version, but the asset manifest lists $($fullAssets.Count)."
}

if ($fullAssets[0].RelativeFileName -ne $targetFullFileName) {
    throw "Velopack published an unexpected full package: $($fullAssets[0].RelativeFileName). Expected: $targetFullFileName"
}

$deltaAssets = @($assets | Where-Object { $_.Type -eq "Delta" })
if ($deltaAssets.Count -gt 1) {
    throw "Velopack should publish at most one delta package for $Version, but the asset manifest lists $($deltaAssets.Count)."
}

if ($deltaAssets.Count -eq 1 -and $deltaAssets[0].RelativeFileName -ne $targetDeltaFileName) {
    throw "Velopack published an unexpected delta package: $($deltaAssets[0].RelativeFileName). Expected: $targetDeltaFileName"
}

if ($eligibleBasePackages.Count -gt 0 -and $deltaAssets.Count -eq 0) {
    $eligibleSummary = ($eligibleBasePackages | ForEach-Object { $_.Name }) -join ", "
    throw "Velopack did not generate a delta package for $Version even though an eligible base was available: $eligibleSummary"
}

$installerAssets = @($assets | Where-Object { $_.Type -eq "Installer" })
if ($installerAssets.Count -ne 1) {
    throw "Velopack should publish exactly one installer, but the asset manifest lists $($installerAssets.Count)."
}

if ($installerAssets[0].RelativeFileName -ne $setupFileName) {
    throw "Velopack published an unexpected installer: $($installerAssets[0].RelativeFileName). Expected: $setupFileName"
}

$releasesJsonFile = Join-Path $workDirectory $releasesJsonFileName
if (!(Test-Path -LiteralPath $releasesJsonFile -PathType Leaf)) {
    throw "Velopack did not produce the release index: $releasesJsonFile"
}

$releaseIndex = Get-Content -LiteralPath $releasesJsonFile -Raw | ConvertFrom-Json
$indexedAssets = @($releaseIndex.Assets)
$indexedTargetAssets = @(
    $indexedAssets | Where-Object {
        $_.PackageId -eq $PackId -and $_.Version -eq $Version
    }
)

if (@($indexedTargetAssets | Where-Object { $_.Type -eq "Full" }).Count -ne 1) {
    throw "The release index does not describe the full package for ${Version}: $releasesJsonFile"
}

if ($deltaAssets.Count -eq 1 -and
    @($indexedTargetAssets | Where-Object { $_.Type -eq "Delta" }).Count -ne 1) {
    throw "The release index does not describe the delta package for ${Version}: $releasesJsonFile"
}

$releasesFile = Join-Path $workDirectory $releasesFileName
if (!(Test-Path -LiteralPath $releasesFile -PathType Leaf)) {
    throw "Velopack did not produce the RELEASES file: $releasesFile"
}

$releasesContent = Get-Content -LiteralPath $releasesFile -Raw
if ([string]::IsNullOrWhiteSpace($releasesContent)) {
    throw "Velopack produced an empty RELEASES file."
}

$stagedFileNames = @($assets | ForEach-Object { $_.RelativeFileName })
$stagedFileNames += @(
    $assetsFileName,
    $releasesJsonFileName,
    $releasesFileName
)

foreach ($stagedFileName in $stagedFileNames) {
    $sourcePath = Join-Path $workDirectory $stagedFileName
    if (!(Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Velopack did not produce an expected release file: $sourcePath"
    }

    Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $releaseDir $stagedFileName) -Force
}

$releaseFiles = @(
    Get-ChildItem -LiteralPath $releaseDir -File | Sort-Object Name
)

$portableFiles = @(
    $releaseFiles | Where-Object {
        $_.Name -match "(?i)(portable|\.zip$)"
    }
)
if ($portableFiles.Count -gt 0) {
    $portableNames = $portableFiles.Name -join ", "
    throw "Velopack produced unexpected portable release file(s): $portableNames"
}

$foreignPackages = @(
    $releaseFiles | Where-Object {
        $_.Name -match "^$escapedPackId-(?<version>.+)-(full|delta)\.nupkg$" -and
        $Matches["version"] -ne $Version
    }
)
if ($foreignPackages.Count -gt 0) {
    $foreignNames = $foreignPackages.Name -join ", "
    throw "The release directory must only contain packages for ${Version}, but found: $foreignNames"
}

$checksumPath = Join-Path $releaseDir $ChecksumFileName
$checksumLines = $releaseFiles | ForEach-Object {
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName
    "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), $_.Name
}
Set-Content -LiteralPath $checksumPath -Value $checksumLines -Encoding ASCII

$fullPackageLength = (Get-Item -LiteralPath (Join-Path $releaseDir $targetFullFileName)).Length
if ($deltaAssets.Count -eq 1) {
    $deltaPackageLength = (Get-Item -LiteralPath (Join-Path $releaseDir $targetDeltaFileName)).Length
    $deltaShare = 100 * $deltaPackageLength / $fullPackageLength
    Write-Host (
        "Delta package {0:N0} bytes, full package {1:N0} bytes ({2:N1}% of full)." -f
        $deltaPackageLength, $fullPackageLength, $deltaShare
    )
} else {
    Write-Host ("No delta package was generated. Full package {0:N0} bytes." -f $fullPackageLength)
}

Write-Host "Packaged Velopack release to $releaseDir"
