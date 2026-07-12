param(
    [string]$Remote = "origin",
    [switch]$SkipFetch
)

$ErrorActionPreference = "Stop"

& "$PSScriptRoot/new-release-tag.ps1" `
    -Remote $Remote `
    -SkipFetch:$SkipFetch `
    -PrepareReleaseNotes
