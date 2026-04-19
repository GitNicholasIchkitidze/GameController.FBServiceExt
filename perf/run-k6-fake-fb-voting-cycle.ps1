[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5277',
    [string]$FakeMetaBaseUrl,
    [int]$FakeFbUsers = 200,
    [string]$Duration = '10m',
    [int]$CooldownSeconds = 60,
    [int]$OutboundWaitSeconds = 10,
    [int]$StartupJitterSeconds = 30,
    [int]$CycleP95Ms = 120000,
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
    [int]$QueueDrainTimeoutSeconds = 180,
    [string]$ArtifactPrefix = 'fake-fb-voting-cycle',
    [string]$SqlConnectionString,
    [string]$VoteAcceptedTextFormat,
    [string]$CooldownActiveTextFormat,
    [string]$VoteConfirmationRejectedText,
    [string]$VoteConfirmationExpiredText,
    [switch]$SkipReadyCheck,
    [switch]$SkipQueueDrainCheck
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

    if (($PreferredProperties -contains 'rate') -and
        ($Metric.PSObject.Properties.Name -contains 'passes') -and
        ($Metric.PSObject.Properties.Name -contains 'fails')) {
        $passes = [double]$Metric.passes
        $fails = [double]$Metric.fails
        $total = $passes + $fails
        if ($total -le 0) {
            return 0
        }

        return $passes / $total
    }

    throw "Metric did not contain any of the expected properties: $($PreferredProperties -join ', ')"
}

function Get-K6MetricNumberOrDefault {
    param(
        [Parameter(Mandatory = $true)]$Metrics,
        [Parameter(Mandatory = $true)][string]$MetricName,
        [Parameter(Mandatory = $true)][string[]]$PreferredProperties,
        [double]$DefaultValue = 0
    )

    if ($Metrics.PSObject.Properties.Name -notcontains $MetricName) {
        return $DefaultValue
    }

    return Get-K6MetricNumber -Metric $Metrics.$MetricName -PreferredProperties $PreferredProperties
}

function New-ThresholdResult {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][double]$Actual,
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][bool]$Passed,
        [Parameter(Mandatory = $true)][string]$ActualDisplay
    )

    [pscustomobject]@{
        Name = $Name
        Actual = $Actual
        ActualDisplay = $ActualDisplay
        Target = $Target
        Passed = $Passed
    }
}

function Get-MessengerContentDefaults {
    param([Parameter(Mandatory = $true)][string[]]$ConfigurationPaths)

    foreach ($configurationPath in $ConfigurationPaths) {
        if (-not (Test-Path $configurationPath)) {
            continue
        }

        $config = Get-Content $configurationPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -ne $config.PSObject.Properties['MessengerContent']) {
            return $config.MessengerContent
        }
    }

    return $null
}

function Get-SqlVoteCounts {
    param([Parameter(Mandatory = $true)][string]$ConnectionString)

    $connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = 'SELECT COUNT(*) AS NormalizedEventsCount FROM dbo.NormalizedEvents; SELECT COUNT(*) AS AcceptedVotesCount FROM dbo.AcceptedVotes;'
        $reader = $command.ExecuteReader()

        $normalizedEventsCount = 0
        $acceptedVotesCount = 0

        if ($reader.Read()) {
            $normalizedEventsCount = [int]$reader['NormalizedEventsCount']
        }

        if ($reader.NextResult() -and $reader.Read()) {
            $acceptedVotesCount = [int]$reader['AcceptedVotesCount']
        }

        $reader.Close()

        return [pscustomobject]@{
            NormalizedEventsCount = $normalizedEventsCount
            AcceptedVotesCount = $acceptedVotesCount
        }
    }
    finally {
        $connection.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($FakeMetaBaseUrl)) {
    $FakeMetaBaseUrl = ($BaseUrl.TrimEnd('/')) + '/fake-meta'
}

$solutionRoot = Split-Path -Parent $PSScriptRoot
$scenarioPath = Join-Path $PSScriptRoot 'k6\scenarios\fake-fb-voting-cycle.js'
$artifactRoot = Join-Path $solutionRoot 'artifacts\perf'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifactDir = Join-Path $artifactRoot "$ArtifactPrefix-$timestamp"
$summaryPath = Join-Path $artifactDir 'k6-summary.json'
$normalizedSummaryPath = Join-Path $artifactDir 'k6-summary.normalized.json'
$normalizedSummaryTextPath = Join-Path $artifactDir 'k6-summary.normalized.txt'
$queuePath = Join-Path $artifactDir 'queue-drain.json'
$messengerContentConfigPaths = @(
    (Join-Path $solutionRoot 'appsettings.Shared.json'),
    (Join-Path $solutionRoot 'appsettings.Shared.PerformanceFakeFb.json'),
    (Join-Path $solutionRoot 'src\GameController.FBServiceExt.Worker\appsettings.json'),
    (Join-Path $solutionRoot 'src\GameController.FBServiceExt.Worker\appsettings.PerformanceFakeFb.json')
)

New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

$messengerContentDefaults = Get-MessengerContentDefaults -ConfigurationPaths $messengerContentConfigPaths
if ($null -ne $messengerContentDefaults) {
    if ([string]::IsNullOrWhiteSpace($VoteAcceptedTextFormat)) { $VoteAcceptedTextFormat = [string]$messengerContentDefaults.VoteAcceptedTextFormat }
    if ([string]::IsNullOrWhiteSpace($CooldownActiveTextFormat)) { $CooldownActiveTextFormat = [string]$messengerContentDefaults.CooldownActiveTextFormat }
    if ([string]::IsNullOrWhiteSpace($VoteConfirmationRejectedText) -and $null -ne $messengerContentDefaults.PSObject.Properties['VoteConfirmationRejectedText']) { $VoteConfirmationRejectedText = [string]$messengerContentDefaults.VoteConfirmationRejectedText }
    if ([string]::IsNullOrWhiteSpace($VoteConfirmationExpiredText) -and $null -ne $messengerContentDefaults.PSObject.Properties['VoteConfirmationExpiredText']) { $VoteConfirmationExpiredText = [string]$messengerContentDefaults.VoteConfirmationExpiredText }
}

if ([string]::IsNullOrWhiteSpace($VoteAcceptedTextFormat)) {
    throw 'VoteAcceptedTextFormat could not be resolved. Pass -VoteAcceptedTextFormat or configure MessengerContent:VoteAcceptedTextFormat.'
}

$k6Command = Get-Command $K6Executable -ErrorAction SilentlyContinue
if (-not $k6Command) {
    throw "k6 executable was not found. Install k6 or pass -K6Executable with the full path."
}

Write-Host 'Starting K6 fake Facebook voting cycle test...' -ForegroundColor Cyan
Write-Host "BaseUrl: $BaseUrl"
Write-Host "FakeMetaBaseUrl: $FakeMetaBaseUrl"
Write-Host "FakeFbUsers: $FakeFbUsers"
Write-Host "Duration: $Duration"
Write-Host "CooldownSeconds: $CooldownSeconds"
Write-Host "OutboundWaitSeconds: $OutboundWaitSeconds"
Write-Host "Artifacts: $artifactDir"

$k6Args = @(
    'run',
    '--summary-export', $summaryPath,
    '-e', "BASE_URL=$BaseUrl",
    '-e', "FAKE_META_BASE_URL=$FakeMetaBaseUrl",
    '-e', "FAKE_FB_USERS=$FakeFbUsers",
    '-e', "TEST_DURATION=$Duration",
    '-e', "COOLDOWN_SECONDS=$CooldownSeconds",
    '-e', "OUTBOUND_WAIT_SECONDS=$OutboundWaitSeconds",
    '-e', "STARTUP_JITTER_SECONDS=$StartupJitterSeconds",
    '-e', "REQUEST_TIMEOUT=$RequestTimeout",
    '-e', "MESSAGE_TEXT=$MessageText",
    '-e', "PAGE_ID=$PageId",
    '-e', "REQUIRE_READY=$([bool](-not $SkipReadyCheck))",
    '-e', "VOTE_ACCEPTED_TEXT_FORMAT=$VoteAcceptedTextFormat",
    '-e', "COOLDOWN_ACTIVE_TEXT_FORMAT=$CooldownActiveTextFormat",
    '-e', "VOTE_CONFIRMATION_REJECTED_TEXT=$VoteConfirmationRejectedText",
    '-e', "VOTE_CONFIRMATION_EXPIRED_TEXT=$VoteConfirmationExpiredText",
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

$summary = Get-Content $summaryPath -Raw | ConvertFrom-Json
$failedRate = Get-K6MetricNumber -Metric $summary.metrics.http_req_failed -PreferredProperties @('value', 'rate')
$cycleSuccessRate = Get-K6MetricNumber -Metric $summary.metrics.fakefb_cycle_success -PreferredProperties @('value', 'rate')
$cycleP95 = Get-K6MetricNumber -Metric $summary.metrics.fakefb_cycle_duration_ms -PreferredProperties @('p(95)')
$checksRate = Get-K6MetricNumber -Metric $summary.metrics.checks -PreferredProperties @('rate')
$httpReqCount = [int](Get-K6MetricNumber -Metric $summary.metrics.http_reqs -PreferredProperties @('count', 'value'))
$cyclesCompleted = [int](Get-K6MetricNumberOrDefault -Metrics $summary.metrics -MetricName 'fakefb_cycles_completed' -PreferredProperties @('count', 'value'))
$carouselReceived = [int](Get-K6MetricNumberOrDefault -Metrics $summary.metrics -MetricName 'fakefb_carousel_received' -PreferredProperties @('count', 'value'))
$confirmationReceived = [int](Get-K6MetricNumberOrDefault -Metrics $summary.metrics -MetricName 'fakefb_confirmation_received' -PreferredProperties @('count', 'value'))
$acceptedReceived = [int](Get-K6MetricNumberOrDefault -Metrics $summary.metrics -MetricName 'fakefb_accepted_received' -PreferredProperties @('count', 'value'))
$cooldownTextReceived = [int](Get-K6MetricNumberOrDefault -Metrics $summary.metrics -MetricName 'fakefb_cooldown_text_received' -PreferredProperties @('count', 'value'))
$rejectedTextReceived = [int](Get-K6MetricNumberOrDefault -Metrics $summary.metrics -MetricName 'fakefb_rejected_text_received' -PreferredProperties @('count', 'value'))
$expiredTextReceived = [int](Get-K6MetricNumberOrDefault -Metrics $summary.metrics -MetricName 'fakefb_expired_text_received' -PreferredProperties @('count', 'value'))
$otherTextReceived = [int](Get-K6MetricNumberOrDefault -Metrics $summary.metrics -MetricName 'fakefb_other_text_received' -PreferredProperties @('count', 'value'))
$unexpectedTextOutcomeCount = [int](Get-K6MetricNumberOrDefault -Metrics $summary.metrics -MetricName 'fakefb_unexpected_text_outcome' -PreferredProperties @('count', 'value'))
$wrongShapeCount = [int](Get-K6MetricNumberOrDefault -Metrics $summary.metrics -MetricName 'fakefb_unexpected_outbound_shape' -PreferredProperties @('count', 'value'))
$outboundWaitP95 = Get-K6MetricNumberOrDefault -Metrics $summary.metrics -MetricName 'fakefb_outbound_wait_ms' -PreferredProperties @('p(95)')

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

$databaseCheckSkipped = [string]::IsNullOrWhiteSpace($SqlConnectionString)
$databaseQuerySucceeded = $false
$databaseCheckPassed = $null
$databaseError = $null
$voteCounts = $null

if (-not $databaseCheckSkipped) {
    try {
        $voteCounts = Get-SqlVoteCounts -ConnectionString $SqlConnectionString
        $databaseQuerySucceeded = $true
        $databaseCheckPassed = ($voteCounts.AcceptedVotesCount -eq $cyclesCompleted) -and ($voteCounts.AcceptedVotesCount -eq $acceptedReceived)
    }
    catch {
        $databaseError = $_.Exception.Message
        $databaseCheckPassed = $false
    }
}

$k6ThresholdResults = @(
    (New-ThresholdResult -Name 'fakefb_cycle_success rate' -Actual $cycleSuccessRate -ActualDisplay ("{0:P2}" -f $cycleSuccessRate) -Target '> 99.00%' -Passed ($cycleSuccessRate -gt 0.99)),
    (New-ThresholdResult -Name 'fakefb_cycle_duration_ms p95' -Actual $cycleP95 -ActualDisplay ("{0:N2} ms" -f $cycleP95) -Target "< $CycleP95Ms ms" -Passed ($cycleP95 -lt $CycleP95Ms)),
    (New-ThresholdResult -Name 'http_req_failed rate' -Actual $failedRate -ActualDisplay ("{0:P2}" -f $failedRate) -Target '< 1.00%' -Passed ($failedRate -lt 0.01)),
    (New-ThresholdResult -Name 'checks rate' -Actual $checksRate -ActualDisplay ("{0:P2}" -f $checksRate) -Target '> 99.00%' -Passed ($checksRate -gt 0.99)),
    (New-ThresholdResult -Name 'unexpected outbound shape' -Actual $wrongShapeCount -ActualDisplay ("{0}" -f $wrongShapeCount) -Target '= 0' -Passed ($wrongShapeCount -eq 0)),
    (New-ThresholdResult -Name 'unexpected final text outcome' -Actual $unexpectedTextOutcomeCount -ActualDisplay ("{0}" -f $unexpectedTextOutcomeCount) -Target '= 0' -Passed ($unexpectedTextOutcomeCount -eq 0))
)

if (-not $databaseCheckSkipped) {
    $actualDisplay = if ($databaseQuerySucceeded) { "{0}" -f $voteCounts.AcceptedVotesCount } else { 'query failed' }
    $actualValue = if ($databaseQuerySucceeded) { [double]$voteCounts.AcceptedVotesCount } else { -1 }
    $targetDisplay = "= $cyclesCompleted"
    $k6ThresholdResults += New-ThresholdResult -Name 'accepted votes persisted in SQL' -Actual $actualValue -ActualDisplay $actualDisplay -Target $targetDisplay -Passed ([bool]$databaseCheckPassed)
}

Write-Host ''
Write-Host 'K6 fake Facebook summary' -ForegroundColor Green
Write-Host ("  http_reqs: {0}" -f $httpReqCount)
Write-Host ("  cycles_completed: {0}" -f $cyclesCompleted)
Write-Host ("  fakefb_cycle_success rate: {0:P2}" -f $cycleSuccessRate)
Write-Host ("  fakefb_cycle_duration_ms p95: {0} ms" -f [math]::Round($cycleP95, 2))
Write-Host ("  fakefb_outbound_wait_ms p95: {0} ms" -f [math]::Round($outboundWaitP95, 2))
Write-Host ("  http_req_failed rate: {0:P2}" -f $failedRate)
Write-Host ("  carousel_received: {0}" -f $carouselReceived)
Write-Host ("  confirmation_received: {0}" -f $confirmationReceived)
Write-Host ("  accepted_received: {0}" -f $acceptedReceived)
Write-Host ("  cooldown_text_received: {0}" -f $cooldownTextReceived)
Write-Host ("  rejected_text_received: {0}" -f $rejectedTextReceived)
Write-Host ("  expired_text_received: {0}" -f $expiredTextReceived)
Write-Host ("  other_text_received: {0}" -f $otherTextReceived)
Write-Host ("  unexpected_text_outcome: {0}" -f $unexpectedTextOutcomeCount)
Write-Host ("  unexpected_outbound_shape: {0}" -f $wrongShapeCount)
if ($databaseCheckSkipped) {
    Write-Host '  sql_vote_counts: skipped'
}
elseif ($databaseQuerySucceeded) {
    Write-Host ("  sql.normalized_events: {0}" -f $voteCounts.NormalizedEventsCount)
    Write-Host ("  sql.accepted_votes: {0}" -f $voteCounts.AcceptedVotesCount)
}
else {
    Write-Host ("  sql_vote_counts: failed ({0})" -f $databaseError)
}

$overallPassed = (($k6ExitCode -eq 0) -and ($SkipQueueDrainCheck -or $queueCheckPassed) -and ($databaseCheckSkipped -or [bool]$databaseCheckPassed))

$normalizedSummary = [pscustomobject]@{
    GeneratedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    Runner = [pscustomobject]@{
        BaseUrl = $BaseUrl
        FakeMetaBaseUrl = $FakeMetaBaseUrl
        FakeFbUsers = $FakeFbUsers
        Duration = $Duration
        CooldownSeconds = $CooldownSeconds
        OutboundWaitSeconds = $OutboundWaitSeconds
        StartupJitterSeconds = $StartupJitterSeconds
        CycleP95TargetMs = $CycleP95Ms
        RequestTimeout = $RequestTimeout
        MessageText = $MessageText
        ArtifactPrefix = $ArtifactPrefix
    }
    Verdict = [pscustomobject]@{
        K6Passed = ($k6ExitCode -eq 0)
        QueueDrainPassed = ($SkipQueueDrainCheck -or $queueCheckPassed)
        DbAcceptedVotesPassed = if ($databaseCheckSkipped) { $null } else { [bool]$databaseCheckPassed }
        OverallPassed = $overallPassed
    }
    Metrics = [pscustomobject]@{
        HttpRequests = $httpReqCount
        CyclesCompleted = $cyclesCompleted
        CycleSuccessRate = $cycleSuccessRate
        CycleDurationP95Ms = [math]::Round($cycleP95, 4)
        OutboundWaitP95Ms = [math]::Round($outboundWaitP95, 4)
        HttpReqFailedRate = $failedRate
        ChecksRate = $checksRate
        CarouselReceived = $carouselReceived
        ConfirmationReceived = $confirmationReceived
        AcceptedReceived = $acceptedReceived
        CooldownTextReceived = $cooldownTextReceived
        RejectedTextReceived = $rejectedTextReceived
        ExpiredTextReceived = $expiredTextReceived
        OtherTextReceived = $otherTextReceived
        UnexpectedTextOutcome = $unexpectedTextOutcomeCount
        UnexpectedOutboundShape = $wrongShapeCount
    }
    Thresholds = $k6ThresholdResults
    QueueDrain = if ($SkipQueueDrainCheck) {
        [pscustomobject]@{ Skipped = $true; Passed = $null; Queues = @() }
    }
    else {
        [pscustomobject]@{ Skipped = $false; Passed = $queueCheckPassed; Queues = $queueSnapshots }
    }
    Database = if ($databaseCheckSkipped) {
        [pscustomobject]@{ Skipped = $true; QuerySucceeded = $null; Passed = $null; AcceptedVotesCount = $null; NormalizedEventsCount = $null; Error = $null }
    }
    else {
        [pscustomobject]@{
            Skipped = $false
            QuerySucceeded = $databaseQuerySucceeded
            Passed = [bool]$databaseCheckPassed
            AcceptedVotesCount = if ($databaseQuerySucceeded) { $voteCounts.AcceptedVotesCount } else { $null }
            NormalizedEventsCount = if ($databaseQuerySucceeded) { $voteCounts.NormalizedEventsCount } else { $null }
            Error = $databaseError
        }
    }
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
    ('DB AcceptedVotes Passed: {0}' -f $(if ($databaseCheckSkipped) { 'Skipped' } else { $normalizedSummary.Verdict.DbAcceptedVotesPassed })),
    '',
    'Thresholds:'
)

foreach ($threshold in $k6ThresholdResults) {
    $normalizedSummaryLines += ('- {0}: actual {1}, target {2}, passed={3}' -f $threshold.Name, $threshold.ActualDisplay, $threshold.Target, $threshold.Passed)
}

$normalizedSummaryLines += ''
$normalizedSummaryLines += ('Cycles completed: {0}' -f $cyclesCompleted)
$normalizedSummaryLines += ('Carousel received: {0}' -f $carouselReceived)
$normalizedSummaryLines += ('Confirmation received: {0}' -f $confirmationReceived)
$normalizedSummaryLines += ('Accepted received: {0}' -f $acceptedReceived)
$normalizedSummaryLines += ('Cooldown text received: {0}' -f $cooldownTextReceived)
$normalizedSummaryLines += ('Rejected text received: {0}' -f $rejectedTextReceived)
$normalizedSummaryLines += ('Expired text received: {0}' -f $expiredTextReceived)
$normalizedSummaryLines += ('Other text received: {0}' -f $otherTextReceived)
$normalizedSummaryLines += ('Unexpected text outcome: {0}' -f $unexpectedTextOutcomeCount)
$normalizedSummaryLines += ('Unexpected outbound shape: {0}' -f $wrongShapeCount)

if (-not $databaseCheckSkipped) {
    $normalizedSummaryLines += ''
    $normalizedSummaryLines += 'Database:'
    if ($databaseQuerySucceeded) {
        $normalizedSummaryLines += ('- NormalizedEvents count: {0}' -f $voteCounts.NormalizedEventsCount)
        $normalizedSummaryLines += ('- AcceptedVotes count: {0}' -f $voteCounts.AcceptedVotesCount)
    }
    else {
        $normalizedSummaryLines += ('- Query failed: {0}' -f $databaseError)
    }
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

if (-not $databaseCheckSkipped -and -not [bool]$databaseCheckPassed) {
    throw "AcceptedVotes verification failed. See $normalizedSummaryPath for DB counts and verdict details."
}

Write-Host ''
Write-Host 'Fake Facebook voting cycle test passed.' -ForegroundColor Green
Write-Host "Summary: $summaryPath"
Write-Host "Normalized summary: $normalizedSummaryPath"
Write-Host "Normalized text summary: $normalizedSummaryTextPath"
if (-not $SkipQueueDrainCheck) {
    Write-Host "Queue snapshot: $queuePath"
}


