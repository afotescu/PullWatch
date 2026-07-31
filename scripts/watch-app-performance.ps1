#Requires -Version 7.0

<#
.SYNOPSIS
Shows a live performance dashboard for PullWatch.

.DESCRIPTION
Samples CPU, GPU video encode/decode, working set, and private memory, then
updates a dashboard in place. By default, child processes are included so work
performed by tools such as FFmpeg is counted as part of PullWatch.

CPU is normalized to the computer's total logical-processor capacity. GPU video
encode and decode each report the busiest matching engine used by the monitored
processes because Windows exposes GPU utilization as separate per-engine
counters. Sampling runs every two seconds until Q, Escape, Ctrl+C, or the
PullWatch process exits.

.EXAMPLE
pwsh ./scripts/watch-app-performance.ps1

Waits for PullWatch to start and monitors PullWatch plus its child processes.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$processName = "PullWatch"
$refreshIntervalSeconds = 2
$script:isInteractive = ![Console]::IsOutputRedirected
$script:childProcessDiscoveryEnabled = $true
$script:childProcessDiscoveryWarning = $null
$script:gpuSamplingEnabled = $true
$script:gpuSamplingWarning = $null

function Find-PullWatchProcess {
    $matchingProcesses = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
    if ($matchingProcesses.Count -gt 1) {
        $matchingIds = ($matchingProcesses.Id | Sort-Object) -join ", "
        throw "More than one PullWatch process is running (IDs: $matchingIds). Close the extra instances and retry."
    }

    return $matchingProcesses | Select-Object -First 1
}

function Test-ExitRequested {
    if (!$script:isInteractive) {
        return $false
    }

    try {
        while ([Console]::KeyAvailable) {
            $key = [Console]::ReadKey($true)
            if ($key.Key -in @([ConsoleKey]::Q, [ConsoleKey]::Escape)) {
                return $true
            }
        }
    } catch {
        # Some PowerShell hosts do not expose a readable console. Ctrl+C still works.
    }

    return $false
}

function Write-WaitingScreen {
    $content = @(
        "PullWatch performance dashboard"
        ""
        "Waiting for PullWatch.exe to start..."
        ""
        "Press Q, Escape, or Ctrl+C to stop."
    ) -join [Environment]::NewLine

    if ($script:isInteractive) {
        [Console]::Write("`e[H`e[J$content")
    } else {
        Write-Output $content
    }
}

function Get-MonitoredProcessIds {
    param(
        [int]$MainProcessId
    )

    if (!$script:childProcessDiscoveryEnabled) {
        return ,$MainProcessId
    }

    try {
        $processEntries = Get-Process
    } catch {
        $script:childProcessDiscoveryEnabled = $false
        $script:childProcessDiscoveryWarning =
            "Child-process discovery is unavailable; showing the main process only. $($_.Exception.Message)"
        return ,$MainProcessId
    }

    $childrenByParent = @{}
    foreach ($entry in $processEntries) {
        $parentProcess = $null
        try {
            $parentProcess = $entry.Parent
            if (!$parentProcess) {
                continue
            }

            $parentId = $parentProcess.Id
            if (!$childrenByParent.ContainsKey($parentId)) {
                $childrenByParent[$parentId] = [System.Collections.Generic.List[int]]::new()
            }

            $childrenByParent[$parentId].Add($entry.Id)
        } catch {
            # Some system processes do not expose relationship information.
        } finally {
            if ($parentProcess) {
                $parentProcess.Dispose()
            }
            $entry.Dispose()
        }
    }

    $result = [System.Collections.Generic.List[int]]::new()
    $pending = [System.Collections.Generic.Queue[int]]::new()
    $seen = [System.Collections.Generic.HashSet[int]]::new()
    $pending.Enqueue($MainProcessId)

    while ($pending.Count -gt 0) {
        $currentId = $pending.Dequeue()
        if (!$seen.Add($currentId)) {
            continue
        }

        $result.Add($currentId)
        if ($childrenByParent.ContainsKey($currentId)) {
            foreach ($childId in $childrenByParent[$currentId]) {
                $pending.Enqueue($childId)
            }
        }
    }

    return $result.ToArray()
}

function Get-ProcessSnapshot {
    param(
        [int[]]$ProcessIds
    )

    $cpuSecondsById = @{}
    $workingSetBytes = [long]0
    $privateMemoryBytes = [long]0
    $runningProcessIds = [System.Collections.Generic.List[int]]::new()

    foreach ($currentProcessId in $ProcessIds) {
        $process = Get-Process -Id $currentProcessId -ErrorAction SilentlyContinue
        if (!$process) {
            continue
        }

        try {
            $process.Refresh()
            $cpuSecondsById[$currentProcessId] = $process.TotalProcessorTime.TotalSeconds
            $workingSetBytes += $process.WorkingSet64
            $privateMemoryBytes += $process.PrivateMemorySize64
            $runningProcessIds.Add($currentProcessId)
        } catch {
            # A process can exit between discovery and reading its metrics.
        } finally {
            $process.Dispose()
        }
    }

    return [pscustomobject]@{
        CpuSecondsById = $cpuSecondsById
        WorkingSetMiB = $workingSetBytes / 1MB
        PrivateMemoryMiB = $privateMemoryBytes / 1MB
        ProcessIds = $runningProcessIds.ToArray()
    }
}

function Get-CpuPercentage {
    param(
        [hashtable]$PreviousCpuSecondsById,
        [hashtable]$CurrentCpuSecondsById,
        [double]$ElapsedSeconds
    )

    if ($ElapsedSeconds -le 0) {
        return $null
    }

    $usedCpuSeconds = [double]0
    foreach ($entry in $CurrentCpuSecondsById.GetEnumerator()) {
        if (!$PreviousCpuSecondsById.ContainsKey($entry.Key)) {
            continue
        }

        $processDelta = [double]$entry.Value - [double]$PreviousCpuSecondsById[$entry.Key]
        if ($processDelta -gt 0) {
            $usedCpuSeconds += $processDelta
        }
    }

    $capacitySeconds = $ElapsedSeconds * [Environment]::ProcessorCount
    return [Math]::Clamp(($usedCpuSeconds / $capacitySeconds) * 100, 0, 100)
}

function Get-GpuVideoEnginePercentages {
    param(
        [int[]]$ProcessIds
    )

    if (!$script:gpuSamplingEnabled) {
        return [pscustomobject]@{
            VideoEncode = $null
            VideoDecode = $null
        }
    }

    try {
        $counterSample = Get-Counter `
            -Counter "\GPU Engine(*)\Utilization Percentage" `
            -ErrorAction Stop
    } catch {
        $script:gpuSamplingEnabled = $false
        $script:gpuSamplingWarning = "GPU counters are unavailable. $($_.Exception.Message)"
        return [pscustomobject]@{
            VideoEncode = $null
            VideoDecode = $null
        }
    }

    $monitoredIds = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($currentProcessId in $ProcessIds) {
        [void]$monitoredIds.Add($currentProcessId)
    }

    $maximumUtilizationByEngineType = @{
        videoencode = [double]0
        videodecode = [double]0
    }

    foreach ($sample in $counterSample.CounterSamples) {
        $processIdMatch = [regex]::Match(
            $sample.InstanceName,
            "(?:^|_)pid_(\d+)(?:_|$)",
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (
            !$processIdMatch.Success -or
            !$monitoredIds.Contains([int]$processIdMatch.Groups[1].Value)
        ) {
            continue
        }

        $engineTypeMatch = [regex]::Match(
            $sample.InstanceName,
            "(?:^|_)engtype_(videoencode|videodecode)$",
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (!$engineTypeMatch.Success) {
            continue
        }

        $value = [double]$sample.CookedValue
        if (![double]::IsNaN($value) -and ![double]::IsInfinity($value)) {
            $engineType = $engineTypeMatch.Groups[1].Value.ToLowerInvariant()
            $maximumUtilizationByEngineType[$engineType] = [Math]::Max(
                $maximumUtilizationByEngineType[$engineType],
                $value)
        }
    }

    return [pscustomobject]@{
        VideoEncode = [Math]::Clamp($maximumUtilizationByEngineType.videoencode, 0, 100)
        VideoDecode = [Math]::Clamp($maximumUtilizationByEngineType.videodecode, 0, 100)
    }
}

function New-Statistic {
    return @{
        Count = 0
        Sum = [double]0
        Maximum = $null
    }
}

function Add-StatisticSample {
    param(
        [hashtable]$Statistic,
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value) {
        return
    }

    $numericValue = [double]$Value
    $Statistic.Count++
    $Statistic.Sum += $numericValue
    if ($null -eq $Statistic.Maximum -or $numericValue -gt $Statistic.Maximum) {
        $Statistic.Maximum = $numericValue
    }
}

function Get-StatisticAverage {
    param(
        [hashtable]$Statistic
    )

    if ($Statistic.Count -eq 0) {
        return $null
    }

    return $Statistic.Sum / $Statistic.Count
}

function Format-MetricValue {
    param(
        [AllowNull()]
        [object]$Value,
        [string]$Unit
    )

    if ($null -eq $Value) {
        return "N/A"
    }

    return "{0:N1} {1}" -f [double]$Value, $Unit
}

function Format-MetricRow {
    param(
        [string]$Name,
        [AllowNull()]
        [object]$Current,
        [hashtable]$Statistic,
        [string]$Unit
    )

    $average = Get-StatisticAverage $Statistic
    return "{0,-27}{1,14}{2,14}{3,14}" -f `
        $Name,
        (Format-MetricValue $Current $Unit),
        (Format-MetricValue $average $Unit),
        (Format-MetricValue $Statistic.Maximum $Unit)
}

function Write-Dashboard {
    param(
        [System.Diagnostics.Process]$MainProcess,
        [pscustomobject]$Snapshot,
        [double]$MonitoringSeconds,
        [double]$SampleIntervalSeconds,
        [int]$SampleCount,
        [AllowNull()]
        [object]$CpuPercentage,
        [pscustomobject]$GpuVideoEnginePercentages,
        [hashtable]$Statistics
    )

    $processScope = if (!$script:childProcessDiscoveryEnabled) {
        "main process only"
    } else {
        "process tree ($($Snapshot.ProcessIds.Count) active)"
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("PullWatch performance dashboard")
    $lines.Add(("=" * 69))
    $lines.Add("Target: $($MainProcess.ProcessName).exe (PID $($MainProcess.Id)), $processScope")
    $lines.Add(
        "Updated: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")  " +
        "| Sample interval: $("{0:N2}" -f $SampleIntervalSeconds)s  " +
        "| Elapsed: $([TimeSpan]::FromSeconds($MonitoringSeconds).ToString("hh\:mm\:ss"))  " +
        "| Samples: $SampleCount")
    $lines.Add("")
    $lines.Add(("{0,-27}{1,14}{2,14}{3,14}" -f "Metric", "Current", "Average", "Maximum"))
    $lines.Add(("-" * 69))
    $lines.Add((Format-MetricRow "CPU (machine capacity)" $CpuPercentage $Statistics.Cpu "%"))
    $lines.Add(
        (Format-MetricRow `
                "GPU video encode" `
                $GpuVideoEnginePercentages.VideoEncode `
                $Statistics.GpuVideoEncode `
                "%")
    )
    $lines.Add(
        (Format-MetricRow `
                "GPU video decode" `
                $GpuVideoEnginePercentages.VideoDecode `
                $Statistics.GpuVideoDecode `
                "%")
    )
    $lines.Add(
        (Format-MetricRow `
                "Working set (RAM)" `
                $Snapshot.WorkingSetMiB `
                $Statistics.WorkingSet `
                "MiB")
    )
    $lines.Add(
        (Format-MetricRow `
                "Private memory" `
                $Snapshot.PrivateMemoryMiB `
                $Statistics.PrivateMemory `
                "MiB")
    )
    $lines.Add("")
    $lines.Add("CPU is normalized across $([Environment]::ProcessorCount) logical processors.")
    $lines.Add("GPU values show the busiest matching engine in the monitored process tree.")
    if ($script:childProcessDiscoveryEnabled) {
        $lines.Add("Memory is summed across the process tree; shared working-set pages may overlap.")
    }
    if ($script:childProcessDiscoveryWarning) {
        $lines.Add($script:childProcessDiscoveryWarning)
    }
    if ($script:gpuSamplingWarning) {
        $lines.Add($script:gpuSamplingWarning)
    }
    $lines.Add("Press Q, Escape, or Ctrl+C to stop.")

    $content = [string]::Join([Environment]::NewLine, $lines)
    if ($script:isInteractive) {
        [Console]::Write("`e[H`e[J$content")
    } else {
        Write-Output $content
        Write-Output ""
    }
}

$mainProcess = $null
$exitMessage = "Performance dashboard stopped."

try {
    if ($script:isInteractive) {
        [Console]::Write("`e[?25l`e[2J`e[H")
    }

    while (!$mainProcess) {
        $mainProcess = Find-PullWatchProcess
        if ($mainProcess) {
            break
        }

        Write-WaitingScreen
        if (Test-ExitRequested) {
            return
        }

        Start-Sleep -Seconds $refreshIntervalSeconds
    }

    $mainProcessId = $mainProcess.Id
    $processIds = @(Get-MonitoredProcessIds $mainProcessId)
    $previousSnapshot = Get-ProcessSnapshot $processIds
    $sampleClock = [System.Diagnostics.Stopwatch]::StartNew()
    $previousSampleTime = $sampleClock.Elapsed.TotalSeconds
    $nextSampleTime = $previousSampleTime + $refreshIntervalSeconds
    $statistics = @{
        Cpu = New-Statistic
        GpuVideoEncode = New-Statistic
        GpuVideoDecode = New-Statistic
        WorkingSet = New-Statistic
        PrivateMemory = New-Statistic
    }
    $sampleCount = 0

    while ($true) {
        $secondsUntilNextSample = $nextSampleTime - $sampleClock.Elapsed.TotalSeconds
        if ($secondsUntilNextSample -gt 0) {
            Start-Sleep -Seconds $secondsUntilNextSample
        }

        if (Test-ExitRequested) {
            break
        }

        $runningMainProcess = Get-Process -Id $mainProcessId -ErrorAction SilentlyContinue
        if (!$runningMainProcess) {
            $exitMessage = "$($mainProcess.ProcessName).exe (PID $mainProcessId) exited."
            break
        }
        $runningMainProcess.Dispose()

        $processIds = @(Get-MonitoredProcessIds $mainProcessId)
        $snapshot = Get-ProcessSnapshot $processIds
        $currentSampleTime = $sampleClock.Elapsed.TotalSeconds
        $elapsedSincePreviousSample = $currentSampleTime - $previousSampleTime

        $cpuPercentage = Get-CpuPercentage `
            $previousSnapshot.CpuSecondsById `
            $snapshot.CpuSecondsById `
            $elapsedSincePreviousSample
        $gpuVideoEnginePercentages = Get-GpuVideoEnginePercentages $snapshot.ProcessIds

        Add-StatisticSample $statistics.Cpu $cpuPercentage
        Add-StatisticSample $statistics.GpuVideoEncode $gpuVideoEnginePercentages.VideoEncode
        Add-StatisticSample $statistics.GpuVideoDecode $gpuVideoEnginePercentages.VideoDecode
        Add-StatisticSample $statistics.WorkingSet $snapshot.WorkingSetMiB
        Add-StatisticSample $statistics.PrivateMemory $snapshot.PrivateMemoryMiB
        $sampleCount++

        Write-Dashboard `
            $mainProcess `
            $snapshot `
            $currentSampleTime `
            $elapsedSincePreviousSample `
            $sampleCount `
            $cpuPercentage `
            $gpuVideoEnginePercentages `
            $statistics

        $previousSnapshot = $snapshot
        $previousSampleTime = $currentSampleTime
        $nextSampleTime = $currentSampleTime + $refreshIntervalSeconds
    }
} finally {
    if ($mainProcess) {
        $mainProcess.Dispose()
    }

    if ($script:isInteractive) {
        [Console]::Write("`e[?25h`e[0m$([Environment]::NewLine)")
    }
}

Write-Host $exitMessage
