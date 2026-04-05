[CmdletBinding()]
param(
    [int[]]$WorkerCounts = @(1, 2, 3, 4),
    [int[]]$TotalUserCounts = @(1000, 2000, 3000, 4000, 5000),
    [string]$Duration = '300s',
    [int]$CooldownSeconds = 60,
    [int]$OutboundWaitSeconds = 10,
    [int]$StartupJitterSeconds = 30,
    [int]$CycleP95Ms = 120000,
    [int]$UsersPerShardCap = 1000,
    [string]$BatchPath = '.\perf\run-fake-fb-voting-cycle.bat'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FirstNonNullValue {
    param([Parameter(Mandatory = $true)][object[]]$Candidates)

    foreach ($candidate in $Candidates) {
        if ($null -ne $candidate) {
            return $candidate
        }
    }

    return $null
}

function Get-LatestArtifactSummary {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactsRoot,
        [Parameter(Mandatory = $true)][datetime]$StartedAfter,
        [Parameter(Mandatory = $true)][bool]$Sharded
    )

    $pattern = if ($Sharded) { 'fake-fb-voting-cycle-sharded-*' } else { 'fake-fb-voting-cycle-*' }
    $dirs = Get-ChildItem $ArtifactsRoot -Directory |
        Where-Object { $_.Name -like $pattern -and $_.Name -notlike '*monitor*' -and $_.LastWriteTime -ge $StartedAfter } |
        Sort-Object LastWriteTime -Descending

    foreach ($dir in $dirs) {
        $summaryPath = Join-Path $dir.FullName 'k6-summary.normalized.json'
        if (Test-Path $summaryPath) {
            return [pscustomobject]@{
                ArtifactDirectory = $dir.FullName
                SummaryPath = $summaryPath
                Summary = Get-Content $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
            }
        }
    }

    return $null
}

function Write-CapacityReport {
    param(
        [Parameter(Mandatory = $true)][System.Collections.Generic.List[object]]$Results,
        [Parameter(Mandatory = $true)][int[]]$WorkerCounts,
        [Parameter(Mandatory = $true)][string]$ReportJsonPath,
        [Parameter(Mandatory = $true)][string]$ReportTextPath
    )

    $Results | ConvertTo-Json -Depth 6 | Set-Content -Path $ReportJsonPath -Encoding UTF8

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('Capacity Matrix Report') | Out-Null
    $lines.Add("GeneratedAtUtc: $([DateTime]::UtcNow.ToString('o'))") | Out-Null
    $lines.Add('') | Out-Null

    foreach ($workerCount in $WorkerCounts) {
        $lines.Add(("Worker count: {0}" -f $workerCount)) | Out-Null
        foreach ($row in ($Results | Where-Object { $_.WorkerCount -eq $workerCount } | Sort-Object TargetUsers)) {
            $lines.Add(("- Users={0}, shards={1}x{2}, exit={3}, overall={4}, k6={5}, db={6}, queue={7}, cycles={8}, acceptedVotes={9}, normalizedEvents={10}, httpFailed={11}, cycleSuccess={12}, unexpectedShape={13}, summary={14}, note={15}" -f
                $row.TargetUsers,
                $row.ShardCount,
                $row.UsersPerShard,
                $row.ExitCode,
                $row.OverallPassed,
                $row.K6Passed,
                $row.DbAcceptedVotesPassed,
                $row.QueueDrainPassed,
                $row.CyclesCompleted,
                $row.AcceptedVotesCount,
                $row.NormalizedEventsCount,
                $row.HttpReqFailedRate,
                $row.CycleSuccessRate,
                $row.UnexpectedOutboundShape,
                $row.SummaryPath,
                $row.Note)) | Out-Null
        }
        $lines.Add('') | Out-Null
    }

    $lines.Add(("JSON: {0}" -f $ReportJsonPath)) | Out-Null
    $lines | Set-Content -Path $ReportTextPath -Encoding UTF8
}

$solutionRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $solutionRoot 'artifacts\perf'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportDir = Join-Path $artifactsRoot "fake-fb-capacity-matrix-$timestamp"
$reportJsonPath = Join-Path $reportDir 'capacity-matrix.json'
$reportTextPath = Join-Path $reportDir 'capacity-matrix.txt'
$batchFullPath = Join-Path $solutionRoot $BatchPath.TrimStart('.','\')
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null

$results = New-Object System.Collections.Generic.List[object]

foreach ($workerCount in $WorkerCounts) {
    foreach ($totalUsers in $TotalUserCounts) {
        $shardCount = [Math]::Max(1, [int][Math]::Ceiling($totalUsers / [double]$UsersPerShardCap))
        $usersPerShard = [int][Math]::Ceiling($totalUsers / [double]$shardCount)
        $effectiveUsers = $usersPerShard * $shardCount
        $isSharded = $shardCount -gt 1
        $startedAt = Get-Date

        Write-Host ''
        Write-Host ('Running matrix case: workers={0}, targetUsers={1}, shardCount={2}, usersPerShard={3}, effectiveUsers={4}, duration={5}' -f $workerCount, $totalUsers, $shardCount, $usersPerShard, $effectiveUsers, $Duration) -ForegroundColor Cyan

        $commandArgs = '"{0}" {1} {2} {3} {4} {5} {6} {7} {8}' -f $batchFullPath, $usersPerShard, $Duration, $CooldownSeconds, $OutboundWaitSeconds, $CycleP95Ms, $workerCount, $shardCount, $StartupJitterSeconds
        $process = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', $commandArgs) -NoNewWindow -Wait -PassThru
        $exitCode = $process.ExitCode

        $artifact = Get-LatestArtifactSummary -ArtifactsRoot $artifactsRoot -StartedAfter $startedAt.AddSeconds(-2) -Sharded $isSharded
        if ($null -eq $artifact) {
            $results.Add([pscustomobject]@{
                WorkerCount = $workerCount
                TargetUsers = $totalUsers
                EffectiveUsers = $effectiveUsers
                ShardCount = $shardCount
                UsersPerShard = $usersPerShard
                ExitCode = $exitCode
                OverallPassed = $false
                K6Passed = $false
                QueueDrainPassed = $null
                DbAcceptedVotesPassed = $null
                CyclesCompleted = $null
                AcceptedVotesCount = $null
                NormalizedEventsCount = $null
                HttpReqFailedRate = $null
                CycleSuccessRate = $null
                UnexpectedOutboundShape = $null
                ArtifactDirectory = $null
                SummaryPath = $null
                Note = 'No normalized summary was produced.'
            }) | Out-Null

            Write-CapacityReport -Results $results -WorkerCounts $WorkerCounts -ReportJsonPath $reportJsonPath -ReportTextPath $reportTextPath
            continue
        }

        $summary = $artifact.Summary
        $results.Add([pscustomobject]@{
            WorkerCount = $workerCount
            TargetUsers = $totalUsers
            EffectiveUsers = $effectiveUsers
            ShardCount = $shardCount
            UsersPerShard = $usersPerShard
            ExitCode = $exitCode
            OverallPassed = [bool]$summary.Verdict.OverallPassed
            K6Passed = [bool]$summary.Verdict.K6Passed
            QueueDrainPassed = $summary.Verdict.QueueDrainPassed
            DbAcceptedVotesPassed = $summary.Verdict.DbAcceptedVotesPassed
            CyclesCompleted = Get-FirstNonNullValue -Candidates @($summary.Metrics.TotalCyclesCompleted, $summary.Metrics.CyclesCompleted)
            AcceptedVotesCount = $summary.Database.AcceptedVotesCount
            NormalizedEventsCount = $summary.Database.NormalizedEventsCount
            HttpReqFailedRate = Get-FirstNonNullValue -Candidates @($summary.Metrics.AggregateHttpReqFailedRate, $summary.Metrics.HttpReqFailedRate)
            CycleSuccessRate = Get-FirstNonNullValue -Candidates @($summary.Metrics.AggregateCycleSuccessRate, $summary.Metrics.CycleSuccessRate)
            UnexpectedOutboundShape = Get-FirstNonNullValue -Candidates @($summary.Metrics.TotalUnexpectedOutboundShape, $summary.Metrics.UnexpectedOutboundShape)
            ArtifactDirectory = $artifact.ArtifactDirectory
            SummaryPath = $artifact.SummaryPath
            Note = $null
        }) | Out-Null

        Write-CapacityReport -Results $results -WorkerCounts $WorkerCounts -ReportJsonPath $reportJsonPath -ReportTextPath $reportTextPath
    }
}

Write-Host ''
Write-Host 'Capacity matrix completed.' -ForegroundColor Green
Write-Host "JSON: $reportJsonPath"
Write-Host "Text: $reportTextPath"
