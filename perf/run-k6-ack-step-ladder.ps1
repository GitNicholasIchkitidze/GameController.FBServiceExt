[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:5278',
    [int[]]$Steps = @(50, 100, 150, 200, 250),
    [int]$EventsPerRequest = 1,
    [string]$Duration = '20s',
    [int]$AckP95Ms = 250,
    [int]$AckP99Ms = 500,
    [int]$PreAllocatedVUs = 300,
    [int]$MaxVUs = 1500,
    [string]$RequestTimeout = '10s',
    [string]$MessageText = 'GET_STARTED',
    [string]$PageId = 'PAGE_ID_PERF',
    [string]$AppSecret,
    [switch]$SkipReadyCheck,
    [switch]$SkipQueueDrainCheck,
    [switch]$StopOnFailure = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-K6MetricNumber {
    param(
        [Parameter(Mandatory = $true)]$Metric,
        [Parameter(Mandatory = $true)][string[]]$PreferredProperties
    )

    foreach ($name in $PreferredProperties) {
        if ($Metric.PSObject.Properties.Name -contains $name) {
            return [double]$Metric.$name
        }
    }

    return [double]::NaN
}

$runnerPath = Join-Path $PSScriptRoot 'run-k6-ack-250rps.ps1'
$solutionRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $solutionRoot 'artifacts\perf'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportDir = Join-Path $artifactRoot "ack-step-ladder-$timestamp"
$reportJsonPath = Join-Path $reportDir 'ladder-summary.json'
$reportCsvPath = Join-Path $reportDir 'ladder-summary.csv'

New-Item -ItemType Directory -Path $reportDir -Force | Out-Null

$results = New-Object System.Collections.Generic.List[object]

foreach ($step in $Steps) {
    Write-Host ''
    Write-Host ("=== Step {0} msg/sec ===" -f $step) -ForegroundColor Cyan

    $beforeNames = @(Get-ChildItem $artifactRoot -Directory -Filter 'ack-250rps-*' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    $stepPassed = $true
    $errorMessage = $null

    try {
        $invokeParams = @{
            BaseUrl = $BaseUrl
            TargetMessagesPerSecond = $step
            EventsPerRequest = $EventsPerRequest
            Duration = $Duration
            AckP95Ms = $AckP95Ms
            AckP99Ms = $AckP99Ms
            PreAllocatedVUs = $PreAllocatedVUs
            MaxVUs = $MaxVUs
            RequestTimeout = $RequestTimeout
            MessageText = $MessageText
            PageId = $PageId
        }

        if ($PSBoundParameters.ContainsKey('AppSecret') -and -not [string]::IsNullOrWhiteSpace($AppSecret)) {
            $invokeParams.AppSecret = $AppSecret
        }

        if ($SkipReadyCheck) {
            $invokeParams.SkipReadyCheck = $true
        }

        if ($SkipQueueDrainCheck) {
            $invokeParams.SkipQueueDrainCheck = $true
        }

        & $runnerPath @invokeParams
    }
    catch {
        $stepPassed = $false
        $errorMessage = $_.Exception.Message
        Write-Warning $errorMessage
    }

    $artifactDir = Get-ChildItem $artifactRoot -Directory -Filter 'ack-250rps-*' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notin $beforeNames } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $artifactDir) {
        $artifactDir = Get-ChildItem $artifactRoot -Directory -Filter 'ack-250rps-*' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
    }

    $summaryPath = if ($artifactDir) { Join-Path $artifactDir.FullName 'k6-summary.json' } else { $null }
    $queuePath = if ($artifactDir) { Join-Path $artifactDir.FullName 'queue-drain.json' } else { $null }

    $p95 = [double]::NaN
    $p99 = [double]::NaN
    $failedRate = [double]::NaN
    $status200Rate = [double]::NaN
    $droppedIterations = [double]::NaN
    $httpReqRate = [double]::NaN
    $queueMessages = $null

    if ($summaryPath -and (Test-Path $summaryPath)) {
        $summary = Get-Content $summaryPath -Raw | ConvertFrom-Json
        $p95 = Get-K6MetricNumber -Metric $summary.metrics.http_req_duration -PreferredProperties @('p(95)')
        $p99 = Get-K6MetricNumber -Metric $summary.metrics.http_req_duration -PreferredProperties @('p(99)')
        $failedRate = Get-K6MetricNumber -Metric $summary.metrics.http_req_failed -PreferredProperties @('value', 'rate')
        $status200Rate = Get-K6MetricNumber -Metric $summary.metrics.webhook_status_200 -PreferredProperties @('value', 'rate')
        $droppedIterations = Get-K6MetricNumber -Metric $summary.metrics.dropped_iterations -PreferredProperties @('count', 'value')
        $httpReqRate = Get-K6MetricNumber -Metric $summary.metrics.http_reqs -PreferredProperties @('rate', 'value')
    }

    if ($queuePath -and (Test-Path $queuePath)) {
        $queueSnapshot = Get-Content $queuePath -Raw | ConvertFrom-Json
        $queueMessages = ($queueSnapshot | Measure-Object -Property Messages -Sum).Sum
    }

    $result = [pscustomobject]@{
        TimestampUtc = (Get-Date).ToUniversalTime().ToString('o')
        TargetMessagesPerSecond = $step
        Passed = $stepPassed
        AckP95Ms = [math]::Round($p95, 2)
        AckP99Ms = [math]::Round($p99, 2)
        Status200Rate = [math]::Round($status200Rate, 6)
        FailedRate = [math]::Round($failedRate, 6)
        DroppedIterations = [int]$droppedIterations
        EffectiveHttpReqRate = [math]::Round($httpReqRate, 2)
        QueueMessagesAfterDrain = $queueMessages
        ArtifactDir = if ($artifactDir) { $artifactDir.FullName } else { $null }
        Error = $errorMessage
    }

    $results.Add($result)
    $result | Format-List

    if (-not $stepPassed -and $StopOnFailure) {
        break
    }
}

$results | ConvertTo-Json -Depth 5 | Set-Content -Path $reportJsonPath -Encoding UTF8
$results | Export-Csv -Path $reportCsvPath -NoTypeInformation -Encoding UTF8

Write-Host ''
Write-Host 'Ladder summary' -ForegroundColor Green
$results | Format-Table TargetMessagesPerSecond,Passed,AckP95Ms,AckP99Ms,Status200Rate,FailedRate,DroppedIterations,EffectiveHttpReqRate,QueueMessagesAfterDrain -AutoSize
Write-Host ''
Write-Host "JSON: $reportJsonPath"
Write-Host "CSV:  $reportCsvPath"
