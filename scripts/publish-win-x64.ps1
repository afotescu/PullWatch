param(
    [string]$OutputPath = "artifacts/publish/win-x64",
    [string]$Version
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "PullWatch.App/PullWatch.App.csproj"
$publishPath = Join-Path $repoRoot $OutputPath
$resolvedPublishPath = [System.IO.Path]::GetFullPath($publishPath)
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$artifactsRootWithSeparator = $artifactsRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (!$resolvedPublishPath.StartsWith(
        $artifactsRootWithSeparator,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must resolve under the artifacts directory: $artifactsRoot"
}

$lockingProcesses = Get-Process |
    Where-Object {
        try {
            $_.Path -and
            [System.IO.Path]::GetFullPath($_.Path).StartsWith(
                $resolvedPublishPath,
                [System.StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $false
        }
    } |
    Select-Object Id, ProcessName, Path

if ($lockingProcesses) {
    Write-Host "Close these running processes before publishing:"
    $lockingProcesses | Format-Table -AutoSize
    throw "The publish directory is in use: $resolvedPublishPath"
}

if (Test-Path -LiteralPath $publishPath) {
    Remove-Item -LiteralPath $publishPath -Recurse -Force
}

dotnet clean $projectPath `
    -c Release `
    -p:Platform=x64

$publishProperties = @("-p:PublishProfile=win-x64")

if (![string]::IsNullOrWhiteSpace($Version)) {
    $publishProperties += "-p:Version=$Version"
    $publishProperties += "-p:InformationalVersion=$Version"
}

dotnet publish $projectPath @publishProperties -o $publishPath

$expectedExe = Join-Path $publishPath "PullWatch.exe"
$oldExe = Join-Path $publishPath "PullWatch.App.exe"

if (!(Test-Path -LiteralPath $expectedExe)) {
    throw "Publish did not produce the expected executable: $expectedExe"
}

if (Test-Path -LiteralPath $oldExe) {
    throw "Stale executable found in publish output: $oldExe"
}

$vcRedistUrl = "https://aka.ms/vc14/vc_redist.x64.exe"

$readmePath = Join-Path $publishPath "README.txt"
@"
PullWatch portable test build

Run PullWatch.exe. Keep ScreenRecorderLib.dll in the same folder as PullWatch.exe.

Screen recording requires:
- Windows 8 or newer
- Windows Media Foundation
- Microsoft Visual C++ Redistributable 2015-2022 x64

If recording cannot start because the Visual C++ Redistributable is missing,
download and install the official Microsoft installer, then restart PullWatch:
$vcRedistUrl
"@ | Set-Content -LiteralPath $readmePath -Encoding UTF8

$totalBytes = (Get-ChildItem -LiteralPath $publishPath -Recurse -File | Measure-Object -Property Length -Sum).Sum
$totalMiB = [Math]::Round($totalBytes / 1MB, 2)

Write-Host "Published to $publishPath"
Write-Host "Executable: $expectedExe"
Write-Host "Total size: $totalMiB MiB ($totalBytes bytes)"
