param(
    [string]$OutputPath = "artifacts/publish/win-x64",
    [string]$VCRedistPath = "$env:USERPROFILE\Downloads\VC_redist.x64.exe"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "PullWatch.App/PullWatch.App.csproj"
$publishPath = Join-Path $repoRoot $OutputPath

if (Test-Path -LiteralPath $publishPath) {
    Remove-Item -LiteralPath $publishPath -Recurse -Force
}

dotnet publish $projectPath `
    -p:PublishProfile=win-x64 `
    -o $publishPath

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

Run PullWatch.exe.

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
Write-Host "Total size: $totalMiB MiB ($totalBytes bytes)"
