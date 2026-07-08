param(
    [string]$Remote = "origin",
    [switch]$SkipFetch
)

$ErrorActionPreference = "Stop"

function Invoke-Git {
    & git @args
    if ($LASTEXITCODE -ne 0) {
        throw "git $($args -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Invoke-GitOutput {
    $output = & git @args
    if ($LASTEXITCODE -ne 0) {
        throw "git $($args -join ' ') failed with exit code $LASTEXITCODE."
    }

    return @($output)
}

function Read-YesNo {
    param(
        [string]$Prompt,
        [bool]$Default
    )

    $suffix = if ($Default) { "[Y/n]" } else { "[y/N]" }
    while ($true) {
        $answer = Read-Host "$Prompt $suffix"
        if ($null -eq $answer) {
            throw "No input was provided."
        }

        if ([string]::IsNullOrWhiteSpace($answer)) {
            return $Default
        }

        switch -Regex ($answer.Trim()) {
            "^(y|yes)$" { return $true }
            "^(n|no)$" { return $false }
            default { Write-Host "Please enter y or n." }
        }
    }
}

function Read-Value {
    param(
        [string]$Prompt,
        [string]$Default
    )

    $answer = Read-Host "$Prompt [$Default]"
    if ($null -eq $answer) {
        throw "No input was provided."
    }

    if ([string]::IsNullOrWhiteSpace($answer)) {
        return $Default
    }

    return $answer.Trim()
}

function Test-ReleaseVersion {
    param([string]$Version)

    return $Version -match '^\d+\.\d+\.\d+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$'
}

function ConvertTo-ReleaseTag {
    param([string]$VersionOrTag)

    $value = $VersionOrTag.Trim()
    if ($value.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        $value = $value.Substring(1)
    }

    if (!(Test-ReleaseVersion $value)) {
        throw "Release version must look like 1.0.0 or 1.0.0-rc.1. Received: $VersionOrTag"
    }

    return "v$value"
}

function Get-StableReleaseTags {
    param([string[]]$Tags)

    $stableTags = foreach ($tag in $Tags) {
        if ($tag -match '^v(\d+)\.(\d+)\.(\d+)$') {
            [pscustomobject]@{
                Tag     = $tag
                Version = [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])"
                Major   = [int]$Matches[1]
                Minor   = [int]$Matches[2]
                Patch   = [int]$Matches[3]
            }
        }
    }

    return @($stableTags | Sort-Object Version)
}

function Get-PrereleaseTags {
    param([string[]]$Tags)

    $prereleaseTags = foreach ($tag in $Tags) {
        if ($tag -match '^v(\d+\.\d+\.\d+)-([0-9A-Za-z]+([.-][0-9A-Za-z]+)*)$') {
            [pscustomObject]@{
                Tag         = $tag
                BaseVersion = [version]$Matches[1]
                Suffix      = $Matches[2]
            }
        }
    }

    return @($prereleaseTags | Sort-Object BaseVersion, Suffix, Tag)
}

function Format-Tag {
    param($TagInfo)

    if ($null -eq $TagInfo) {
        return "(none)"
    }

    return $TagInfo.Tag
}

function Add-Version {
    param(
        [version]$Version,
        [string]$Part
    )

    switch ($Part) {
        "patch" { return "$($Version.Major).$($Version.Minor).$($Version.Build + 1)" }
        "minor" { return "$($Version.Major).$($Version.Minor + 1).0" }
        "major" { return "$($Version.Major + 1).0.0" }
        default { throw "Unsupported version part: $Part" }
    }
}

function Get-NextPrereleaseTag {
    param(
        [string[]]$Tags,
        [string]$DefaultBaseVersion
    )

    $baseVersion = Read-Value "Target base version, without v or suffix" $DefaultBaseVersion
    $baseTag = ConvertTo-ReleaseTag $baseVersion
    $baseVersion = $baseTag.Substring(1)

    $label = Read-Value "Prerelease label, without numeric suffix" "rc"
    if ($label -notmatch '^[0-9A-Za-z]+([.-][0-9A-Za-z]+)*$') {
        throw "Prerelease label must contain only letters, numbers, dots, and hyphens. Received: $label"
    }

    $escapedBase = [regex]::Escape($baseVersion)
    $escapedLabel = [regex]::Escape($label)
    $existingNumbers = foreach ($tag in $Tags) {
        if ($tag -match "^v$escapedBase-$escapedLabel\.(\d+)$") {
            [int]$Matches[1]
        }
    }

    $nextNumber = 1
    if ($existingNumbers) {
        $nextNumber = (@($existingNumbers) | Measure-Object -Maximum).Maximum + 1
    }

    return "v$baseVersion-$label.$nextNumber"
}

function Select-ReleaseTag {
    param(
        [string[]]$Tags,
        $LatestStable,
        $LatestPrerelease
    )

    $stableVersion = if ($LatestStable) { $LatestStable.Version } else { [version]"0.0.0" }
    $nextPatch = Add-Version $stableVersion "patch"
    $nextMinor = Add-Version $stableVersion "minor"
    $nextMajor = Add-Version $stableVersion "major"
    $defaultPrereleaseBase = $nextPatch

    if ($LatestPrerelease -and $LatestPrerelease.BaseVersion -ge [version]$nextPatch) {
        $defaultPrereleaseBase = $LatestPrerelease.BaseVersion.ToString()
    }

    Write-Host ""
    Write-Host "Choose a tag:"
    Write-Host "  1. Patch release       -> v$nextPatch"
    Write-Host "  2. Minor release       -> v$nextMinor"
    Write-Host "  3. Major release       -> v$nextMajor"
    Write-Host "  4. Next prerelease     -> choose base and label"
    Write-Host "  5. Custom version/tag"

    while ($true) {
        $choice = Read-Host "Selection"
        if ($null -eq $choice) {
            throw "No selection was provided."
        }

        switch ($choice.Trim()) {
            "1" { return ConvertTo-ReleaseTag $nextPatch }
            "2" { return ConvertTo-ReleaseTag $nextMinor }
            "3" { return ConvertTo-ReleaseTag $nextMajor }
            "4" { return Get-NextPrereleaseTag $Tags $defaultPrereleaseBase }
            "5" {
                $custom = Read-Host "Version or tag, for example 1.0.0-rc.1 or v1.0.0"
                if ($null -eq $custom) {
                    throw "No custom version was provided."
                }

                return ConvertTo-ReleaseTag $custom
            }
            default { Write-Host "Please choose 1, 2, 3, 4, or 5." }
        }
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    Invoke-Git rev-parse --is-inside-work-tree | Out-Null

    $dirtyLines = Invoke-GitOutput status --porcelain
    if ($dirtyLines.Count -gt 0) {
        Write-Host "Working tree changes:"
        $dirtyLines | ForEach-Object { Write-Host "  $_" }
        throw "Working tree must be clean before creating a release tag."
    }

    if (!$SkipFetch -and (Read-YesNo "Fetch tags from $Remote first?" $true)) {
        Invoke-Git fetch --tags --prune-tags $Remote
    }

    $branch = (Invoke-GitOutput branch --show-current | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($branch)) {
        $branch = "(detached HEAD)"
    }

    $commitHash = (Invoke-GitOutput rev-parse --short HEAD | Select-Object -First 1)
    $commitSubject = (Invoke-GitOutput log -1 --pretty=%s | Select-Object -First 1)
    $tags = Invoke-GitOutput tag --list
    $stableTags = Get-StableReleaseTags $tags
    $prereleaseTags = Get-PrereleaseTags $tags
    $latestStable = $stableTags | Select-Object -Last 1
    $latestPrerelease = $prereleaseTags | Select-Object -Last 1

    Write-Host ""
    Write-Host "Branch:             $branch"
    Write-Host "Commit:             $commitHash $commitSubject"
    Write-Host "Latest stable:      $(Format-Tag $latestStable)"
    Write-Host "Latest prerelease:  $(Format-Tag $latestPrerelease)"

    $tag = Select-ReleaseTag $tags $latestStable $latestPrerelease
    if ($tags -contains $tag) {
        throw "Tag already exists locally: $tag"
    }

    Write-Host ""
    Write-Host "Tag to create: $tag"
    Write-Host "Target commit: $commitHash $commitSubject"
    if (!(Read-YesNo "Create annotated tag $tag at HEAD?" $false)) {
        Write-Host "Canceled."
        exit 0
    }

    Invoke-Git tag -a $tag -m "PullWatch $tag"
    Write-Host "Created tag $tag."

    if (Read-YesNo "Push $tag to $Remote and trigger the release workflow?" $false) {
        Invoke-Git push $Remote $tag
        Write-Host "Pushed $tag to $Remote."
    } else {
        Write-Host "Tag $tag was created locally but not pushed."
        Write-Host "Push later with: git push $Remote $tag"
    }
} finally {
    Pop-Location
}
