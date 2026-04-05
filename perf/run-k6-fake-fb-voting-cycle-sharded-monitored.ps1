[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5277',
    [string]$FakeMetaBaseUrl,
    [int]$FakeFbUsersPerShard = 2500,
    [int]$ShardCount = 4,
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
    [int]$MonitorSampleIntervalMilliseconds = 1000,
    [string]$ArtifactPrefix = 'fake-fb-voting-cycle-sharded-monitor',
    [string]$K6ArtifactPrefix = 'fake-fb-voting-cycle-sharded',
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

function Convert-DurationToSeconds {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ($Value -match '^(?<seconds>\d+)s$') { return [int]$Matches.seconds }
    if ($Value -match '^(?<minutes>\d+)m$') { return [int]$Matches.minutes * 60 }
    if ($Value -match '^(?<hours>\d+)h$') { return [int]$Matches.hours * 3600 }

    throw "Unsupported duration format: $Value. Use values like 60s, 10m, or 1h."
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

function Get-MetricPasses {
    param(
        [Parameter(Mandatory = $true)]$Metrics,
        [Parameter(Mandatory = $true)][string]$MetricName
    )

    if ($Metrics.PSObject.Properties.Name -notcontains $MetricName) {
        return 0
    }

    $metric = $Metrics.$MetricName
    if ($metric.PSObject.Properties.Name -contains 'passes') {
        return [double]$metric.passes
    }

    return 0
}

function Get-MetricFails {
    param(
        [Parameter(Mandatory = $true)]$Metrics,
        [Parameter(Mandatory = $true)][string]$MetricName
    )

    if ($Metrics.PSObject.Properties.Name -notcontains $MetricName) {
        return 0
    }

    $metric = $Metrics.$MetricName
    if ($metric.PSObject.Properties.Name -contains 'fails') {
        return [double]$metric.fails
    }

    return 0
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
    param([Parameter(Mandatory = $true)][string]$ConfigurationPath)

    if (-not (Test-Path $ConfigurationPath)) {
        return $null
    }

    $config = Get-Content $ConfigurationPath -Raw -Encoding UTF8 | ConvertFrom-Json
    return $config.MessengerContent
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
function Get-QueueSnapshots {
    param(
        [Parameter(Mandatory = $true)][string]$RabbitMqApiBaseUrl,
        [Parameter(Mandatory = $true)][string]$RabbitMqUserName,
        [Parameter(Mandatory = $true)][string]$RabbitMqPassword,
        [Parameter(Mandatory = $true)][string]$RabbitMqVirtualHost,
        [Parameter(Mandatory = $true)][string[]]$QueueNames,
        [Parameter(Mandatory = $true)][int]$QueueDrainTimeoutSeconds,
        [Parameter(Mandatory = $true)][string]$QueuePath
    )

    $auth = [Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes("${RabbitMqUserName}:${RabbitMqPassword}"))
    $headers = @{ Authorization = "Basic $auth" }
    $deadline = (Get-Date).AddSeconds($QueueDrainTimeoutSeconds)
    $encodedVirtualHost = [System.Uri]::EscapeDataString($RabbitMqVirtualHost)
    $queueSnapshots = @()

    do {
        $queueSnapshots = foreach ($queueName in $QueueNames) {
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

    $queueSnapshots | ConvertTo-Json -Depth 4 | Set-Content -Path $QueuePath -Encoding UTF8

    return [pscustomobject]@{
        Passed = (@($queueSnapshots | Where-Object { $_.Messages -ne 0 -or $_.Ready -ne 0 -or $_.Unacked -ne 0 }).Count -eq 0)
        Queues = $queueSnapshots
    }
}

if ([string]::IsNullOrWhiteSpace($FakeMetaBaseUrl)) {
    $FakeMetaBaseUrl = ($BaseUrl.TrimEnd('/')) + '/fake-meta'
}

$durationSeconds = Convert-DurationToSeconds -Value $Duration
$solutionRoot = Split-Path -Parent $PSScriptRoot
$scenarioPath = Join-Path $PSScriptRoot 'k6\scenarios\fake-fb-voting-cycle.js'
$monitorScript = Join-Path $PSScriptRoot 'monitor-rabbitmq.ps1'
$artifactRoot = Join-Path $solutionRoot 'artifacts\perf'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifactDir = Join-Path $artifactRoot "$K6ArtifactPrefix-$timestamp"
$monitorArtifactDir = Join-Path $artifactRoot "$ArtifactPrefix-$timestamp"
$monitorDir = Join-Path $monitorArtifactDir 'rabbitmq-monitor'
$stopFilePath = Join-Path $monitorDir 'stop.signal'
$shardsDir = Join-Path $artifactDir 'shards'
$queuePath = Join-Path $artifactDir 'queue-drain.json'
$normalizedSummaryPath = Join-Path $artifactDir 'k6-summary.normalized.json'
$normalizedSummaryTextPath = Join-Path $artifactDir 'k6-summary.normalized.txt'
$workerConfigPath = Join-Path $solutionRoot 'src\GameController.FBServiceExt.Worker\appsettings.json'
$totalFakeFbUsers = $FakeFbUsersPerShard * $ShardCount

New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
New-Item -ItemType Directory -Path $monitorDir -Force | Out-Null
New-Item -ItemType Directory -Path $shardsDir -Force | Out-Null

$messengerContentDefaults = Get-MessengerContentDefaults -ConfigurationPath $workerConfigPath
if ($null -ne $messengerContentDefaults) {
    if ([string]::IsNullOrWhiteSpace($VoteAcceptedTextFormat)) { $VoteAcceptedTextFormat = [string]$messengerContentDefaults.VoteAcceptedTextFormat }
    if ([string]::IsNullOrWhiteSpace($CooldownActiveTextFormat)) { $CooldownActiveTextFormat = [string]$messengerContentDefaults.CooldownActiveTextFormat }
    if ([string]::IsNullOrWhiteSpace($VoteConfirmationRejectedText)) { $VoteConfirmationRejectedText = [string]$messengerContentDefaults.VoteConfirmationRejectedText }
    if ([string]::IsNullOrWhiteSpace($VoteConfirmationExpiredText)) { $VoteConfirmationExpiredText = [string]$messengerContentDefaults.VoteConfirmationExpiredText }
}

if ([string]::IsNullOrWhiteSpace($VoteAcceptedTextFormat)) {
    throw 'VoteAcceptedTextFormat could not be resolved. Pass -VoteAcceptedTextFormat or configure MessengerContent:VoteAcceptedTextFormat.'
}

$k6Command = Get-Command $K6Executable -ErrorAction SilentlyContinue
if (-not $k6Command) {
    throw "k6 executable was not found. Install k6 or pass -K6Executable with the full path."
}

Write-Host 'Starting sharded K6 fake Facebook voting cycle test...' -ForegroundColor Cyan
Write-Host "BaseUrl: $BaseUrl"
Write-Host "FakeMetaBaseUrl: $FakeMetaBaseUrl"
Write-Host "ShardCount: $ShardCount"
Write-Host "FakeFbUsersPerShard: $FakeFbUsersPerShard"
Write-Host "TotalFakeFbUsers: $totalFakeFbUsers"
Write-Host "Duration: $Duration"
Write-Host "CooldownSeconds: $CooldownSeconds"
Write-Host "OutboundWaitSeconds: $OutboundWaitSeconds"
Write-Host "Artifacts: $artifactDir"
Write-Host "Monitor artifacts: $monitorDir"

if (-not $SkipReadyCheck) {
    $readyUrl = ($BaseUrl.TrimEnd('/')) + '/health/ready'
    $ready = Invoke-WebRequest -Uri $readyUrl -UseBasicParsing -TimeoutSec 5
    if ($ready.StatusCode -ne 200) {
        throw "Readiness check failed. GET $readyUrl => $($ready.StatusCode)"
    }
}

Invoke-RestMethod -Uri ($FakeMetaBaseUrl.TrimEnd('/') + '/api/messages') -Method Delete | Out-Null

if (Test-Path $stopFilePath) {
    Remove-Item $stopFilePath -Force
}

$monitorJob = Start-Job -ScriptBlock {
    param(
        $ScriptPath,
        $ApiBaseUrl,
        $UserName,
        $Password,
        $VirtualHost,
        $RawQueue,
        $NormalizedQueue,
        $SampleIntervalMs,
        $DurationSec,
        $Dir,
        $StopPath
    )

    & $ScriptPath `
        -RabbitMqApiBaseUrl $ApiBaseUrl `
        -RabbitMqUserName $UserName `
        -RabbitMqPassword $Password `
        -RabbitMqVirtualHost $VirtualHost `
        -QueueNames @($RawQueue, $NormalizedQueue) `
        -SampleIntervalMilliseconds $SampleIntervalMs `
        -DurationSeconds $DurationSec `
        -ArtifactDirectory $Dir `
        -Label 'rabbitmq-monitor' `
        -StopFilePath $StopPath
} -ArgumentList @(
    $monitorScript,
    $RabbitMqApiBaseUrl,
    $RabbitMqUserName,
    $RabbitMqPassword,
    $RabbitMqVirtualHost,
    $RawIngressQueueName,
    $NormalizedEventQueueName,
    $MonitorSampleIntervalMilliseconds,
    ($durationSeconds + $QueueDrainTimeoutSeconds + 30),
    $monitorDir,
    $stopFilePath
)

$shardJobs = @()
$jobResults = @()
try {
    Start-Sleep -Milliseconds 750

    for ($shardIndex = 0; $shardIndex -lt $ShardCount; $shardIndex++) {
        $shardNumber = $shardIndex + 1
        $recipientOffset = $shardIndex * $FakeFbUsersPerShard
        $shardDir = Join-Path $shardsDir ('shard-{0:d2}' -f $shardNumber)
        $summaryPath = Join-Path $shardDir 'k6-summary.json'
        $logPath = Join-Path $shardDir 'k6-output.log'
        $startupJitterSeconds = [string]$StartupJitterSeconds
        New-Item -ItemType Directory -Path $shardDir -Force | Out-Null

        Write-Host ("Starting shard {0}/{1} with recipient offset {2}..." -f $shardNumber, $ShardCount, $recipientOffset)

        $job = Start-Job -ScriptBlock {
            param(
                $ShardNumber,
                $RecipientOffset,
                $K6Path,
                $ScenarioPath,
                $SummaryPath,
                $LogPath,
                $BaseUrl,
                $FakeMetaBaseUrl,
                $FakeFbUsersPerShard,
                $StartupJitterSeconds,
                $Duration,
                $CooldownSeconds,
                $OutboundWaitSeconds,
                $RequestTimeout,
                $MessageText,
                $PageId,
                $AppSecret,
                $VoteAcceptedTextFormat,
                $CooldownActiveTextFormat,
                $VoteConfirmationRejectedText,
                $VoteConfirmationExpiredText
            )

            $env:BASE_URL = $BaseUrl
            $env:FAKE_META_BASE_URL = $FakeMetaBaseUrl
            $env:FAKE_FB_USERS = [string]$FakeFbUsersPerShard
            $env:RECIPIENT_OFFSET = [string]$RecipientOffset
            $env:TEST_DURATION = $Duration
            $env:COOLDOWN_SECONDS = [string]$CooldownSeconds
            $env:OUTBOUND_WAIT_SECONDS = [string]$OutboundWaitSeconds
            $env:REQUEST_TIMEOUT = $RequestTimeout
            $env:MESSAGE_TEXT = $MessageText
            $env:PAGE_ID = $PageId
            $env:REQUIRE_READY = 'false'
            $env:CLEAR_FAKE_META_ON_SETUP = 'false'
            $env:STARTUP_JITTER_SECONDS = [string]$StartupJitterSeconds
            $env:VOTE_ACCEPTED_TEXT_FORMAT = $VoteAcceptedTextFormat
            $env:COOLDOWN_ACTIVE_TEXT_FORMAT = $CooldownActiveTextFormat
            $env:VOTE_CONFIRMATION_REJECTED_TEXT = $VoteConfirmationRejectedText
            $env:VOTE_CONFIRMATION_EXPIRED_TEXT = $VoteConfirmationExpiredText
            if (-not [string]::IsNullOrWhiteSpace($AppSecret)) {
                $env:APP_SECRET = $AppSecret
            }

            & $K6Path run '--summary-export' $SummaryPath $ScenarioPath *> $LogPath
            $exitCode = $LASTEXITCODE

            [pscustomobject]@{
                ShardNumber = $ShardNumber
                RecipientOffset = $RecipientOffset
                SummaryPath = $SummaryPath
                LogPath = $LogPath
                ExitCode = $exitCode
            }
        } -ArgumentList @(
            $shardNumber,
            $recipientOffset,
            $k6Command.Source,
            $scenarioPath,
            $summaryPath,
            $logPath,
            $BaseUrl,
            $FakeMetaBaseUrl,
            $FakeFbUsersPerShard,
            $StartupJitterSeconds,
            $Duration,
            $CooldownSeconds,
            $OutboundWaitSeconds,
            $RequestTimeout,
            $MessageText,
            $PageId,
            $AppSecret,
            $VoteAcceptedTextFormat,
            $CooldownActiveTextFormat,
            $VoteConfirmationRejectedText,
            $VoteConfirmationExpiredText
        )
        $shardJobs += $job
    }

    Wait-Job -Job $shardJobs | Out-Null
    $jobResults = @($shardJobs | ForEach-Object { Receive-Job -Job $_ -Wait -AutoRemoveJob })
}
finally {
    New-Item -ItemType File -Path $stopFilePath -Force | Out-Null

    if ($null -ne $monitorJob) {
        Wait-Job -Job $monitorJob -Timeout 20 | Out-Null
        Receive-Job -Job $monitorJob -Wait -AutoRemoveJob | Out-Host
    }
}

if ($jobResults.Count -ne $ShardCount) {
    throw "Expected $ShardCount shard results but received $($jobResults.Count)."
}

$shardSummaries = @()
foreach ($result in ($jobResults | Sort-Object ShardNumber)) {
    if (-not (Test-Path $result.SummaryPath)) {
        throw "Shard $($result.ShardNumber) did not produce a summary file: $($result.SummaryPath)"
    }

    $summary = Get-Content $result.SummaryPath -Raw | ConvertFrom-Json
    $metrics = $summary.metrics

    $cyclePasses = Get-MetricPasses -Metrics $metrics -MetricName 'fakefb_cycle_success'
    $cycleFails = Get-MetricFails -Metrics $metrics -MetricName 'fakefb_cycle_success'
    $httpFailedPasses = Get-MetricPasses -Metrics $metrics -MetricName 'http_req_failed'
    $httpFailedFails = Get-MetricFails -Metrics $metrics -MetricName 'http_req_failed'
    $checkPasses = Get-MetricPasses -Metrics $metrics -MetricName 'checks'
    $checkFails = Get-MetricFails -Metrics $metrics -MetricName 'checks'

    $shardSummaries += [pscustomobject]@{
        ShardNumber = [int]$result.ShardNumber
        RecipientOffset = [int]$result.RecipientOffset
        ExitCode = [int]$result.ExitCode
        SummaryPath = $result.SummaryPath
        LogPath = $result.LogPath
        HttpRequests = [int](Get-K6MetricNumberOrDefault -Metrics $metrics -MetricName 'http_reqs' -PreferredProperties @('count', 'value'))
        CyclesCompleted = [int](Get-K6MetricNumberOrDefault -Metrics $metrics -MetricName 'fakefb_cycles_completed' -PreferredProperties @('count', 'value'))
        CarouselReceived = [int](Get-K6MetricNumberOrDefault -Metrics $metrics -MetricName 'fakefb_carousel_received' -PreferredProperties @('count', 'value'))
        ConfirmationReceived = [int](Get-K6MetricNumberOrDefault -Metrics $metrics -MetricName 'fakefb_confirmation_received' -PreferredProperties @('count', 'value'))
        AcceptedReceived = [int](Get-K6MetricNumberOrDefault -Metrics $metrics -MetricName 'fakefb_accepted_received' -PreferredProperties @('count', 'value'))
        CooldownTextReceived = [int](Get-K6MetricNumberOrDefault -Metrics $metrics -MetricName 'fakefb_cooldown_text_received' -PreferredProperties @('count', 'value'))
        RejectedTextReceived = [int](Get-K6MetricNumberOrDefault -Metrics $metrics -MetricName 'fakefb_rejected_text_received' -PreferredProperties @('count', 'value'))
        ExpiredTextReceived = [int](Get-K6MetricNumberOrDefault -Metrics $metrics -MetricName 'fakefb_expired_text_received' -PreferredProperties @('count', 'value'))
        OtherTextReceived = [int](Get-K6MetricNumberOrDefault -Metrics $metrics -MetricName 'fakefb_other_text_received' -PreferredProperties @('count', 'value'))
        UnexpectedTextOutcome = [int](Get-K6MetricNumberOrDefault -Metrics $metrics -MetricName 'fakefb_unexpected_text_outcome' -PreferredProperties @('count', 'value'))
        UnexpectedOutboundShape = [int](Get-K6MetricNumberOrDefault -Metrics $metrics -MetricName 'fakefb_unexpected_outbound_shape' -PreferredProperties @('count', 'value'))
        CycleSuccessPasses = [int]$cyclePasses
        CycleSuccessFails = [int]$cycleFails
        HttpReqFailedPasses = [int]$httpFailedPasses
        HttpReqFailedFails = [int]$httpFailedFails
        CheckPasses = [int]$checkPasses
        CheckFails = [int]$checkFails
        CycleSuccessRate = Get-K6MetricNumber -Metric $metrics.fakefb_cycle_success -PreferredProperties @('value', 'rate')
        CycleDurationP95Ms = Get-K6MetricNumber -Metric $metrics.fakefb_cycle_duration_ms -PreferredProperties @('p(95)')
        OutboundWaitP95Ms = Get-K6MetricNumberOrDefault -Metrics $metrics -MetricName 'fakefb_outbound_wait_ms' -PreferredProperties @('p(95)')
        HttpReqFailedRate = Get-K6MetricNumber -Metric $metrics.http_req_failed -PreferredProperties @('value', 'rate')
        ChecksRate = Get-K6MetricNumber -Metric $metrics.checks -PreferredProperties @('rate')
    }
}
$totalCyclePasses = [double]($shardSummaries | Measure-Object -Property CycleSuccessPasses -Sum).Sum
$totalCycleFails = [double]($shardSummaries | Measure-Object -Property CycleSuccessFails -Sum).Sum
$totalHttpFailedPasses = [double]($shardSummaries | Measure-Object -Property HttpReqFailedPasses -Sum).Sum
$totalHttpFailedFails = [double]($shardSummaries | Measure-Object -Property HttpReqFailedFails -Sum).Sum
$totalCheckPasses = [double]($shardSummaries | Measure-Object -Property CheckPasses -Sum).Sum
$totalCheckFails = [double]($shardSummaries | Measure-Object -Property CheckFails -Sum).Sum

$aggregateCycleSuccessRate = if (($totalCyclePasses + $totalCycleFails) -gt 0) { $totalCyclePasses / ($totalCyclePasses + $totalCycleFails) } else { 0 }
$aggregateHttpReqFailedRate = if (($totalHttpFailedPasses + $totalHttpFailedFails) -gt 0) { $totalHttpFailedPasses / ($totalHttpFailedPasses + $totalHttpFailedFails) } else { 0 }
$aggregateChecksRate = if (($totalCheckPasses + $totalCheckFails) -gt 0) { $totalCheckPasses / ($totalCheckPasses + $totalCheckFails) } else { 0 }

$totalHttpRequests = [int]($shardSummaries | Measure-Object -Property HttpRequests -Sum).Sum
$totalCyclesCompleted = [int]($shardSummaries | Measure-Object -Property CyclesCompleted -Sum).Sum
$totalCarouselReceived = [int]($shardSummaries | Measure-Object -Property CarouselReceived -Sum).Sum
$totalConfirmationReceived = [int]($shardSummaries | Measure-Object -Property ConfirmationReceived -Sum).Sum

$totalAcceptedReceived = [int]($shardSummaries | Measure-Object -Property AcceptedReceived -Sum).Sum
$totalCooldownTextReceived = [int]($shardSummaries | Measure-Object -Property CooldownTextReceived -Sum).Sum
$totalRejectedTextReceived = [int]($shardSummaries | Measure-Object -Property RejectedTextReceived -Sum).Sum
$totalExpiredTextReceived = [int]($shardSummaries | Measure-Object -Property ExpiredTextReceived -Sum).Sum
$totalOtherTextReceived = [int]($shardSummaries | Measure-Object -Property OtherTextReceived -Sum).Sum
$totalUnexpectedTextOutcome = [int]($shardSummaries | Measure-Object -Property UnexpectedTextOutcome -Sum).Sum
$totalUnexpectedOutboundShape = [int]($shardSummaries | Measure-Object -Property UnexpectedOutboundShape -Sum).Sum
$worstShardCycleP95 = [double](($shardSummaries | Measure-Object -Property CycleDurationP95Ms -Maximum).Maximum)
$worstShardOutboundWaitP95 = [double](($shardSummaries | Measure-Object -Property OutboundWaitP95Ms -Maximum).Maximum)
$allShardsPassed = @($shardSummaries | Where-Object { $_.ExitCode -ne 0 }).Count -eq 0

$queueSnapshots = @()
$queueCheckPassed = $true
if (-not $SkipQueueDrainCheck) {
    $queueResult = Get-QueueSnapshots `
        -RabbitMqApiBaseUrl $RabbitMqApiBaseUrl `
        -RabbitMqUserName $RabbitMqUserName `
        -RabbitMqPassword $RabbitMqPassword `
        -RabbitMqVirtualHost $RabbitMqVirtualHost `
        -QueueNames @($RawIngressQueueName, $NormalizedEventQueueName) `
        -QueueDrainTimeoutSeconds $QueueDrainTimeoutSeconds `
        -QueuePath $queuePath

    $queueSnapshots = $queueResult.Queues
    $queueCheckPassed = [bool]$queueResult.Passed

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
        $databaseCheckPassed = ($voteCounts.AcceptedVotesCount -eq $totalCyclesCompleted) -and ($voteCounts.AcceptedVotesCount -eq $totalAcceptedReceived)
    }
    catch {
        $databaseError = $_.Exception.Message
        $databaseCheckPassed = $false
    }
}

$thresholdResults = @(
    (New-ThresholdResult -Name 'all shards exited cleanly' -Actual $(if ($allShardsPassed) { 1 } else { 0 }) -ActualDisplay $(if ($allShardsPassed) { 'true' } else { 'false' }) -Target '= true' -Passed $allShardsPassed),
    (New-ThresholdResult -Name 'aggregate fakefb_cycle_success rate' -Actual $aggregateCycleSuccessRate -ActualDisplay ("{0:P2}" -f $aggregateCycleSuccessRate) -Target '> 99.00%' -Passed ($aggregateCycleSuccessRate -gt 0.99)),
    (New-ThresholdResult -Name 'worst shard fakefb_cycle_duration_ms p95' -Actual $worstShardCycleP95 -ActualDisplay ("{0:N2} ms" -f $worstShardCycleP95) -Target "< $CycleP95Ms ms" -Passed ($worstShardCycleP95 -lt $CycleP95Ms)),
    (New-ThresholdResult -Name 'aggregate http_req_failed rate' -Actual $aggregateHttpReqFailedRate -ActualDisplay ("{0:P2}" -f $aggregateHttpReqFailedRate) -Target '< 1.00%' -Passed ($aggregateHttpReqFailedRate -lt 0.01)),
    (New-ThresholdResult -Name 'aggregate checks rate' -Actual $aggregateChecksRate -ActualDisplay ("{0:P2}" -f $aggregateChecksRate) -Target '> 99.00%' -Passed ($aggregateChecksRate -gt 0.99)),
    (New-ThresholdResult -Name 'aggregate unexpected outbound shape' -Actual $totalUnexpectedOutboundShape -ActualDisplay ("{0}" -f $totalUnexpectedOutboundShape) -Target '= 0' -Passed ($totalUnexpectedOutboundShape -eq 0)),
    (New-ThresholdResult -Name 'aggregate unexpected final text outcome' -Actual $totalUnexpectedTextOutcome -ActualDisplay ("{0}" -f $totalUnexpectedTextOutcome) -Target '= 0' -Passed ($totalUnexpectedTextOutcome -eq 0))
)

if (-not $databaseCheckSkipped) {
    $actualDisplay = if ($databaseQuerySucceeded) { "{0}" -f $voteCounts.AcceptedVotesCount } else { 'query failed' }
    $actualValue = if ($databaseQuerySucceeded) { [double]$voteCounts.AcceptedVotesCount } else { -1 }
    $targetDisplay = "= $totalCyclesCompleted"
    $thresholdResults += New-ThresholdResult -Name 'accepted votes persisted in SQL' -Actual $actualValue -ActualDisplay $actualDisplay -Target $targetDisplay -Passed ([bool]$databaseCheckPassed)
}

Write-Host ''
Write-Host 'Sharded K6 fake Facebook summary' -ForegroundColor Green
Write-Host ("  total_fake_fb_users: {0}" -f $totalFakeFbUsers)
Write-Host ("  total_http_reqs: {0}" -f $totalHttpRequests)
Write-Host ("  total_cycles_completed: {0}" -f $totalCyclesCompleted)
Write-Host ("  aggregate_cycle_success rate: {0:P2}" -f $aggregateCycleSuccessRate)
Write-Host ("  worst_shard_cycle_duration_ms p95: {0} ms" -f [math]::Round($worstShardCycleP95, 2))
Write-Host ("  worst_shard_outbound_wait_ms p95: {0} ms" -f [math]::Round($worstShardOutboundWaitP95, 2))
Write-Host ("  aggregate_http_req_failed rate: {0:P2}" -f $aggregateHttpReqFailedRate)
Write-Host ("  total_carousel_received: {0}" -f $totalCarouselReceived)
Write-Host ("  total_confirmation_received: {0}" -f $totalConfirmationReceived)
Write-Host ("  total_accepted_received: {0}" -f $totalAcceptedReceived)
Write-Host ("  total_cooldown_text_received: {0}" -f $totalCooldownTextReceived)
Write-Host ("  total_rejected_text_received: {0}" -f $totalRejectedTextReceived)
Write-Host ("  total_expired_text_received: {0}" -f $totalExpiredTextReceived)
Write-Host ("  total_other_text_received: {0}" -f $totalOtherTextReceived)
Write-Host ("  total_unexpected_text_outcome: {0}" -f $totalUnexpectedTextOutcome)
Write-Host ("  total_unexpected_outbound_shape: {0}" -f $totalUnexpectedOutboundShape)
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

$overallPassed = ($allShardsPassed -and ($SkipQueueDrainCheck -or $queueCheckPassed) -and ($databaseCheckSkipped -or [bool]$databaseCheckPassed))
$normalizedSummary = [pscustomobject]@{
    GeneratedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    Runner = [pscustomobject]@{
        BaseUrl = $BaseUrl
        FakeMetaBaseUrl = $FakeMetaBaseUrl
        ShardCount = $ShardCount
        FakeFbUsersPerShard = $FakeFbUsersPerShard
        TotalFakeFbUsers = $totalFakeFbUsers
        Duration = $Duration
        CooldownSeconds = $CooldownSeconds
        OutboundWaitSeconds = $OutboundWaitSeconds
        StartupJitterSeconds = $StartupJitterSeconds
        CycleP95TargetMs = $CycleP95Ms
        RequestTimeout = $RequestTimeout
        MessageText = $MessageText
        ArtifactPrefix = $K6ArtifactPrefix
    }
    Verdict = [pscustomobject]@{
        K6Passed = $allShardsPassed
        QueueDrainPassed = if ($SkipQueueDrainCheck) { $null } else { $queueCheckPassed }
        DbAcceptedVotesPassed = if ($databaseCheckSkipped) { $null } else { [bool]$databaseCheckPassed }
        OverallPassed = $overallPassed
    }
    Metrics = [pscustomobject]@{
        TotalHttpRequests = $totalHttpRequests
        TotalCyclesCompleted = $totalCyclesCompleted
        AggregateCycleSuccessRate = [math]::Round($aggregateCycleSuccessRate, 6)
        WorstShardCycleDurationP95Ms = [math]::Round($worstShardCycleP95, 4)
        WorstShardOutboundWaitP95Ms = [math]::Round($worstShardOutboundWaitP95, 4)
        AggregateHttpReqFailedRate = [math]::Round($aggregateHttpReqFailedRate, 6)
        AggregateChecksRate = [math]::Round($aggregateChecksRate, 6)
        TotalCarouselReceived = $totalCarouselReceived
        TotalConfirmationReceived = $totalConfirmationReceived
        TotalAcceptedReceived = $totalAcceptedReceived
        TotalCooldownTextReceived = $totalCooldownTextReceived
        TotalRejectedTextReceived = $totalRejectedTextReceived
        TotalExpiredTextReceived = $totalExpiredTextReceived
        TotalOtherTextReceived = $totalOtherTextReceived
        TotalUnexpectedTextOutcome = $totalUnexpectedTextOutcome
        TotalUnexpectedOutboundShape = $totalUnexpectedOutboundShape
    }
    Thresholds = $thresholdResults
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
    Shards = $shardSummaries
    RawArtifacts = [pscustomobject]@{
        ArtifactDirectory = $artifactDir
        MonitorDirectory = $monitorDir
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

foreach ($threshold in $thresholdResults) {
    $normalizedSummaryLines += ('- {0}: actual {1}, target {2}, passed={3}' -f $threshold.Name, $threshold.ActualDisplay, $threshold.Target, $threshold.Passed)
}

$normalizedSummaryLines += ''
$normalizedSummaryLines += ('Shard count: {0}' -f $ShardCount)
$normalizedSummaryLines += ('Fake FB users per shard: {0}' -f $FakeFbUsersPerShard)
$normalizedSummaryLines += ('Total fake FB users: {0}' -f $totalFakeFbUsers)
$normalizedSummaryLines += ('Total cycles completed: {0}' -f $totalCyclesCompleted)
$normalizedSummaryLines += ('Total carousel received: {0}' -f $totalCarouselReceived)
$normalizedSummaryLines += ('Total confirmation received: {0}' -f $totalConfirmationReceived)
$normalizedSummaryLines += ('Total accepted received: {0}' -f $totalAcceptedReceived)
$normalizedSummaryLines += ('Total cooldown text received: {0}' -f $totalCooldownTextReceived)
$normalizedSummaryLines += ('Total rejected text received: {0}' -f $totalRejectedTextReceived)
$normalizedSummaryLines += ('Total expired text received: {0}' -f $totalExpiredTextReceived)
$normalizedSummaryLines += ('Total other text received: {0}' -f $totalOtherTextReceived)
$normalizedSummaryLines += ('Total unexpected text outcome: {0}' -f $totalUnexpectedTextOutcome)
$normalizedSummaryLines += ('Total unexpected outbound shape: {0}' -f $totalUnexpectedOutboundShape)
$normalizedSummaryLines += ('Worst shard cycle p95: {0:N2} ms' -f $worstShardCycleP95)
$normalizedSummaryLines += ('Worst shard outbound wait p95: {0:N2} ms' -f $worstShardOutboundWaitP95)

$normalizedSummaryLines += ''
$normalizedSummaryLines += 'Shards:'
foreach ($shard in $shardSummaries) {
    $normalizedSummaryLines += ('- Shard {0}: exit={1}, offset={2}, http_reqs={3}, cycles={4}, accepted={5}, unexpected_shape={6}, summary={7}' -f $shard.ShardNumber, $shard.ExitCode, $shard.RecipientOffset, $shard.HttpRequests, $shard.CyclesCompleted, $shard.AcceptedReceived, $shard.UnexpectedOutboundShape, $shard.SummaryPath)
}

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
$normalizedSummaryLines += ('Aggregate artifact directory: {0}' -f $artifactDir)
$normalizedSummaryLines += ('Monitor directory: {0}' -f $monitorDir)
$normalizedSummaryLines += ('Normalized summary: {0}' -f $normalizedSummaryPath)

$normalizedSummaryLines | Set-Content -Path $normalizedSummaryTextPath -Encoding UTF8

if (-not $allShardsPassed) {
    throw "One or more K6 shards failed. See $normalizedSummaryPath and per-shard summaries under $artifactDir."
}

if (-not $SkipQueueDrainCheck -and -not $queueCheckPassed) {
    throw "Queue drain check failed. See $normalizedSummaryPath and $queuePath for details."
}

if (-not $databaseCheckSkipped -and -not [bool]$databaseCheckPassed) {
    throw "AcceptedVotes verification failed. See $normalizedSummaryPath for DB counts and verdict details."
}

Write-Host ''
Write-Host 'Sharded fake Facebook voting cycle test passed.' -ForegroundColor Green
Write-Host "Normalized summary: $normalizedSummaryPath"
Write-Host "Normalized text summary: $normalizedSummaryTextPath"
if (-not $SkipQueueDrainCheck) {
    Write-Host "Queue snapshot: $queuePath"
}
Write-Host "Monitor summary: $(Join-Path $monitorDir 'rabbitmq-summary.json')"


