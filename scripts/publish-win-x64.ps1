param(
    [string]$OutputPath = "artifacts/publish/win-x64",
    [string]$VCRedistPath = "$env:USERPROFILE\Downloads\VC_redist.x64.exe"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "PullWatch.App/PullWatch.App.csproj"
$publishPath = Join-Path $repoRoot $OutputPath
$resolvedPublishPath = [System.IO.Path]::GetFullPath($publishPath)

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

dotnet publish $projectPath `
    -p:PublishProfile=win-x64 `
    -o $publishPath

$expectedExe = Join-Path $publishPath "PullWatch.exe"
$oldExe = Join-Path $publishPath "PullWatch.App.exe"

if (!(Test-Path -LiteralPath $expectedExe)) {
    throw "Publish did not produce the expected executable: $expectedExe"
}

if (Test-Path -LiteralPath $oldExe) {
    throw "Stale executable found in publish output: $oldExe"
}

$vcRedistFileName = "VC_redist.x64.exe"
$vcRedistUrl = "https://aka.ms/vc14/vc_redist.x64.exe"

if (Test-Path -LiteralPath $VCRedistPath) {
    Copy-Item `
        -LiteralPath $VCRedistPath `
        -Destination (Join-Path $publishPath $vcRedistFileName) `
        -Force
    Write-Host "Included $vcRedistFileName"
}
else {
    Write-Warning "VC++ Redistributable was not found at $VCRedistPath"
    Write-Warning "Download it from $vcRedistUrl and place it next to PullWatch.exe before sharing the build."
}

$readmePath = Join-Path $publishPath "README.txt"
@"
PullWatch portable test build

Run PullWatch.exe. Keep ScreenRecorderLib.dll in the same folder as PullWatch.exe.

Screen recording requires:
- Windows 8 or newer
- Windows Media Foundation
- Microsoft Visual C++ Redistributable 2015-2022 x64

If recording cannot start because the Visual C++ Redistributable is missing,
run the included $vcRedistFileName once, then restart PullWatch.

If you do not want to run the included installer, download the official Microsoft
installer instead:
$vcRedistUrl
"@ | Set-Content -LiteralPath $readmePath -Encoding UTF8

$totalBytes = (Get-ChildItem -LiteralPath $publishPath -Recurse -File | Measure-Object -Property Length -Sum).Sum
$totalMiB = [Math]::Round($totalBytes / 1MB, 2)

Write-Host "Published to $publishPath"
Write-Host "Executable: $expectedExe"
Write-Host "Total size: $totalMiB MiB ($totalBytes bytes)"
