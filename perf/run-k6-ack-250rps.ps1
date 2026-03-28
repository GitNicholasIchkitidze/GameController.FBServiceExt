[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5277',
    [int]$TargetMessagesPerSecond = 250,
    [int]$EventsPerRequest = 1,
    [string]$Duration = '60s',
    [int]$AckP95Ms = 250,
    [int]$AckP99Ms = 500,
    [int]$PreAllocatedVUs = 300,
    [int]$MaxVUs = 1500,
    [string]$RequestTimeout = '10s',
    [string]$MessageText = 'GET_STARTED',
    [string]$PageId = 'PAGE_ID_PERF',
    [string]$AppSecret,
    [string]$K6Executable = 'k6',
    [string]$RabbitMqApiBaseUrl = 'http://localhost:15672/api',
    [string]$RabbitMqUserName = 'fbserviceext',
    [string]$RabbitMqPassword = 'DevOnly_RabbitMq2026!',
    [string]$RabbitMqVirtualHost = '/',
    [string]$RawIngressQueueName = 'fbserviceext.raw.ingress',
    [string]$NormalizedEventQueueName = 'fbserviceext.normalized.events',
    [int]$QueueDrainTimeoutSeconds = 90,
    [switch]$SkipReadyCheck,
    [switch]$SkipQueueDrainCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$solutionRoot = Split-Path -Parent $PSScriptRoot
$scenarioPath = Join-Path $PSScriptRoot 'k6\scenarios\ack-250rps.js'
$artifactRoot = Join-Path $solutionRoot 'artifacts\perf'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifactDir = Join-Path $artifactRoot "ack-250rps-$timestamp"
$summaryPath = Join-Path $artifactDir 'k6-summary.json'
$normalizedSummaryPath = Join-Path $artifactDir 'k6-summary.normalized.json'
$normalizedSummaryTextPath = Join-Path $artifactDir 'k6-summary.normalized.txt'
$queuePath = Join-Path $artifactDir 'queue-drain.json'

New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

$k6Command = Get-Command $K6Executable -ErrorAction SilentlyContinue
if (-not $k6Command) {
    throw "k6 executable was not found. Install k6 or pass -K6Executable with the full path."
}

Write-Host "Starting K6 webhook ACK test..." -ForegroundColor Cyan
Write-Host "BaseUrl: $BaseUrl"
Write-Host "TargetMessagesPerSecond: $TargetMessagesPerSecond"
Write-Host "EventsPerRequest: $EventsPerRequest"
Write-Host "Duration: $Duration"
Write-Host "Artifacts: $artifactDir"

$k6Args = @(
    'run',
    '--summary-export', $summaryPath,
    '-e', "BASE_URL=$BaseUrl",
    '-e', "TARGET_MESSAGES_PER_SEC=$TargetMessagesPerSecond",
    '-e', "EVENTS_PER_REQUEST=$EventsPerRequest",
    '-e', "TEST_DURATION=$Duration",
    '-e', "ACK_P95_MS=$AckP95Ms",
    '-e', "ACK_P99_MS=$AckP99Ms",
    '-e', "PRE_ALLOCATED_VUS=$PreAllocatedVUs",
    '-e', "MAX_VUS=$MaxVUs",
    '-e', "REQUEST_TIMEOUT=$RequestTimeout",
    '-e', "MESSAGE_TEXT=$MessageText",
    '-e', "PAGE_ID=$PageId",
    '-e', "REQUIRE_READY=$([bool](-not $SkipReadyCheck))",
    $scenarioPath
)

if ($PSBoundParameters.ContainsKey('AppSecret') -and -not [string]::IsNullOrWhiteSpace($AppSecret)) {
    $k6Args = $k6Args[0..($k6Args.Length - 2)] + @('-e', "APP_SECRET=$AppSecret", $scenarioPath)
}

& $k6Command.Source @k6Args
$k6ExitCode = $LASTEXITCODE

if (-not (Test-Path $summaryPath)) {
    throw "K6 did not produce a summary file: $summaryPath"
}

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

    throw "Metric did not contain any of the expected properties: $($PreferredProperties -join ', ')"
}

function New-ThresholdResult {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [double]$Actual,
        [Parameter(Mandatory = $true)]
        [string]$Target,
        [Parameter(Mandatory = $true)]
        [bool]$Passed,
        [Parameter(Mandatory = $true)]
        [string]$ActualDisplay
    )

    [pscustomobject]@{
        Name = $Name
        Actual = $Actual
        ActualDisplay = $ActualDisplay
        Target = $Target
        Passed = $Passed
    }
}

$summary = Get-Content $summaryPath -Raw | ConvertFrom-Json
$durationMetric = $summary.metrics.http_req_duration
$ackMetric = $summary.metrics.webhook_ack_duration
$failedRate = Get-K6MetricNumber -Metric $summary.metrics.http_req_failed -PreferredProperties @('value', 'rate')
$droppedIterations = [int](Get-K6MetricNumber -Metric $summary.metrics.dropped_iterations -PreferredProperties @('count', 'value'))
$status200Rate = Get-K6MetricNumber -Metric $summary.metrics.webhook_status_200 -PreferredProperties @('value', 'rate')
$sentMessages = [int](Get-K6MetricNumber -Metric $summary.metrics.webhook_messages_sent -PreferredProperties @('count', 'value'))
$httpReqCount = [int](Get-K6MetricNumber -Metric $summary.metrics.http_reqs -PreferredProperties @('count', 'value'))
$httpReqP95 = Get-K6MetricNumber -Metric $durationMetric -PreferredProperties @('p(95)')
$httpReqP99 = Get-K6MetricNumber -Metric $durationMetric -PreferredProperties @('p(99)')
$ackP95 = Get-K6MetricNumber -Metric $ackMetric -PreferredProperties @('p(95)')
$ackP99 = Get-K6MetricNumber -Metric $ackMetric -PreferredProperties @('p(99)')

$k6ThresholdResults = @(
    (New-ThresholdResult -Name 'http_req_duration p95' -Actual $httpReqP95 -ActualDisplay ("{0:N2} ms" -f $httpReqP95) -Target "< $AckP95Ms ms" -Passed ($httpReqP95 -lt $AckP95Ms)),
    (New-ThresholdResult -Name 'http_req_duration p99' -Actual $httpReqP99 -ActualDisplay ("{0:N2} ms" -f $httpReqP99) -Target "< $AckP99Ms ms" -Passed ($httpReqP99 -lt $AckP99Ms)),
    (New-ThresholdResult -Name 'webhook_ack_duration p95' -Actual $ackP95 -ActualDisplay ("{0:N2} ms" -f $ackP95) -Target "< $AckP95Ms ms" -Passed ($ackP95 -lt $AckP95Ms)),
    (New-ThresholdResult -Name 'webhook_ack_duration p99' -Actual $ackP99 -ActualDisplay ("{0:N2} ms" -f $ackP99) -Target "< $AckP99Ms ms" -Passed ($ackP99 -lt $AckP99Ms)),
    (New-ThresholdResult -Name 'http_req_failed rate' -Actual $failedRate -ActualDisplay ("{0:P2}" -f $failedRate) -Target '< 1.00%' -Passed ($failedRate -lt 0.01)),
    (New-ThresholdResult -Name 'webhook_status_200 rate' -Actual $status200Rate -ActualDisplay ("{0:P2}" -f $status200Rate) -Target '> 99.00%' -Passed ($status200Rate -gt 0.99)),
    (New-ThresholdResult -Name 'dropped_iterations' -Actual $droppedIterations -ActualDisplay ("{0}" -f $droppedIterations) -Target '= 0' -Passed ($droppedIterations -eq 0))
)

Write-Host ''
Write-Host 'K6 summary' -ForegroundColor Green
Write-Host ("  http_req_duration p95: {0} ms" -f [math]::Round($httpReqP95, 2))
Write-Host ("  http_req_duration p99: {0} ms" -f [math]::Round($httpReqP99, 2))
Write-Host ("  http_req_failed rate: {0:P2}" -f $failedRate)
Write-Host ("  webhook_status_200 rate: {0:P2}" -f $status200Rate)
Write-Host ("  dropped_iterations: {0}" -f $droppedIterations)
Write-Host ("  webhook_messages_sent: {0}" -f $sentMessages)

$queueSnapshots = @()
$queueCheckPassed = $true

if (-not $SkipQueueDrainCheck) {
    $auth = [Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes("${RabbitMqUserName}:${RabbitMqPassword}"))
    $headers = @{ Authorization = "Basic $auth" }
    $deadline = (Get-Date).AddSeconds($QueueDrainTimeoutSeconds)
    $encodedVirtualHost = [System.Uri]::EscapeDataString($RabbitMqVirtualHost)
    $queueNames = @($RawIngressQueueName, $NormalizedEventQueueName)

    do {
        $queueSnapshots = foreach ($queueName in $queueNames) {
            $encodedQueue = [System.Uri]::EscapeDataString($queueName)
            $queueUrl = "$RabbitMqApiBaseUrl/queues/$encodedVirtualHost/$encodedQueue"
            $queue = Invoke-RestMethod -Uri $queueUrl -Headers $headers -Method Get
            [pscustomobject]@{
                Queue = $queue.name
                Messages = [int]$queue.messages
                Ready = [int]$queue.messages_ready
                Unacked = [int]$queue.messages_unacknowledged
                Consumers = [int]$queue.consumers
                CheckedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
            }
        }

        $allDrained = @($queueSnapshots | Where-Object { $_.Messages -ne 0 -or $_.Ready -ne 0 -or $_.Unacked -ne 0 }).Count -eq 0
        if ($allDrained) {
            break
        }

        Start-Sleep -Seconds 2
    }
    while ((Get-Date) -lt $deadline)

    $queueSnapshots | ConvertTo-Json -Depth 4 | Set-Content -Path $queuePath -Encoding UTF8
    $queueCheckPassed = @($queueSnapshots | Where-Object { $_.Messages -ne 0 -or $_.Ready -ne 0 -or $_.Unacked -ne 0 }).Count -eq 0

    Write-Host ''
    Write-Host 'Queue drain check' -ForegroundColor Green
    $queueSnapshots | ForEach-Object {
        Write-Host ("  {0}: messages={1}, ready={2}, unacked={3}, consumers={4}" -f $_.Queue, $_.Messages, $_.Ready, $_.Unacked, $_.Consumers)
    }
}

$normalizedSummary = [pscustomobject]@{
    GeneratedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    Runner = [pscustomobject]@{
        BaseUrl = $BaseUrl
        TargetMessagesPerSecond = $TargetMessagesPerSecond
        EventsPerRequest = $EventsPerRequest
        Duration = $Duration
        AckP95TargetMs = $AckP95Ms
        AckP99TargetMs = $AckP99Ms
        RequestTimeout = $RequestTimeout
        MessageText = $MessageText
    }
    Verdict = [pscustomobject]@{
        K6Passed = ($k6ExitCode -eq 0)
        QueueDrainPassed = ($SkipQueueDrainCheck -or $queueCheckPassed)
        OverallPassed = (($k6ExitCode -eq 0) -and ($SkipQueueDrainCheck -or $queueCheckPassed))
    }
    Metrics = [pscustomobject]@{
        HttpRequests = $httpReqCount
        WebhookMessagesSent = $sentMessages
        HttpReqDurationP95Ms = [math]::Round($httpReqP95, 4)
        HttpReqDurationP99Ms = [math]::Round($httpReqP99, 4)
        WebhookAckDurationP95Ms = [math]::Round($ackP95, 4)
        WebhookAckDurationP99Ms = [math]::Round($ackP99, 4)
        HttpReqFailedRate = $failedRate
        WebhookStatus200Rate = $status200Rate
        DroppedIterations = $droppedIterations
    }
    Thresholds = $k6ThresholdResults
    QueueDrain = if ($SkipQueueDrainCheck) {
        [pscustomobject]@{
            Skipped = $true
            Passed = $null
            Queues = @()
        }
    }
    else {
        [pscustomobject]@{
            Skipped = $false
            Passed = $queueCheckPassed
            Queues = $queueSnapshots
        }
    }
    Notes = @(
        'This file is the normalized verdict for the run.',
        'Prefer this file over raw k6-summary.json when reading pass/fail status.',
        'The raw k6-summary.json thresholds object can serialize as false even when the actual metric values pass.'
    )
    RawArtifacts = [pscustomobject]@{
        K6SummaryJson = $summaryPath
        QueueDrainJson = if ($SkipQueueDrainCheck) { $null } else { $queuePath }
    }
}

$normalizedSummary | ConvertTo-Json -Depth 8 | Set-Content -Path $normalizedSummaryPath -Encoding UTF8

$normalizedSummaryLines = @(
    ('Verdict: {0}' -f $(if ($normalizedSummary.Verdict.OverallPassed) { 'PASS' } else { 'FAIL' })),
    ('K6 Passed: {0}' -f $normalizedSummary.Verdict.K6Passed),
    ('Queue Drain Passed: {0}' -f $(if ($SkipQueueDrainCheck) { 'Skipped' } else { $normalizedSummary.Verdict.QueueDrainPassed })),
    '',
    'Thresholds:'
)

foreach ($threshold in $k6ThresholdResults) {
    $normalizedSummaryLines += ('- {0}: actual {1}, target {2}, passed={3}' -f $threshold.Name, $threshold.ActualDisplay, $threshold.Target, $threshold.Passed)
}

if (-not $SkipQueueDrainCheck) {
    $normalizedSummaryLines += ''
    $normalizedSummaryLines += 'Queue drain:'
    foreach ($queue in $queueSnapshots) {
        $normalizedSummaryLines += ('- {0}: messages={1}, ready={2}, unacked={3}, consumers={4}' -f $queue.Queue, $queue.Messages, $queue.Ready, $queue.Unacked, $queue.Consumers)
    }
}

$normalizedSummaryLines += ''
$normalizedSummaryLines += ('Raw K6 summary: {0}' -f $summaryPath)
$normalizedSummaryLines += ('Normalized summary: {0}' -f $normalizedSummaryPath)

$normalizedSummaryLines | Set-Content -Path $normalizedSummaryTextPath -Encoding UTF8

if ($k6ExitCode -ne 0) {
    throw "K6 thresholds failed. See $normalizedSummaryPath for the normalized verdict and $summaryPath for raw K6 output."
}

if (-not $SkipQueueDrainCheck -and -not $queueCheckPassed) {
    throw "Queue drain check failed. Queues did not return to zero within $QueueDrainTimeoutSeconds seconds. See $normalizedSummaryPath and $queuePath for details."
}

Write-Host ''
Write-Host 'ACK performance test passed.' -ForegroundColor Green
Write-Host "Summary: $summaryPath"
Write-Host "Normalized summary: $normalizedSummaryPath"
Write-Host "Normalized text summary: $normalizedSummaryTextPath"
if (-not $SkipQueueDrainCheck) {
    Write-Host "Queue snapshot: $queuePath"
}
