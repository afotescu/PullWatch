param(
    [string]$OutputPath = "artifacts/publish/win-x64"
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

dotnet publish $projectPath `
    -p:PublishProfile=win-x64 `
    -o $publishPath

$totalBytes = (Get-ChildItem -LiteralPath $publishPath -Recurse -File | Measure-Object -Property Length -Sum).Sum
$totalMiB = [Math]::Round($totalBytes / 1MB, 2)

Write-Host "Published to $publishPath"
Write-Host "Total size: $totalMiB MiB ($totalBytes bytes)"
