[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5277',
    [int]$TargetMessagesPerSecond = 400,
    [int]$EventsPerRequest = 1,
    [string]$Duration = '300s',
    [int]$AckP95Ms = 300,
    [int]$AckP99Ms = 600,
    [int]$PreAllocatedVUs = 500,
    [int]$MaxVUs = 2500,
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
    [string]$ArtifactPrefix = 'ack-stress-monitored',
    [string]$K6ArtifactPrefix = 'ack-250rps',
    [switch]$SkipReadyCheck,
    [switch]$SkipQueueDrainCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Convert-DurationToSeconds {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Value -match '^(?<seconds>\d+)s$') {
        return [int]$Matches.seconds
    }

    if ($Value -match '^(?<minutes>\d+)m$') {
        return [int]$Matches.minutes * 60
    }

    if ($Value -match '^(?<hours>\d+)h$') {
        return [int]$Matches.hours * 3600
    }

    throw "Unsupported duration format: $Value. Use values like 60s, 10m, or 1h."
}

$durationSeconds = Convert-DurationToSeconds -Value $Duration
$solutionRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $solutionRoot 'artifacts\perf'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifactDir = Join-Path $artifactRoot "$ArtifactPrefix-$timestamp"
$monitorDir = Join-Path $artifactDir 'rabbitmq-monitor'
$stopFilePath = Join-Path $monitorDir 'stop.signal'

New-Item -ItemType Directory -Path $monitorDir -Force | Out-Null

$monitorScript = Join-Path $PSScriptRoot 'monitor-rabbitmq.ps1'
$runnerScript = Join-Path $PSScriptRoot 'run-k6-ack-250rps.ps1'

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

try {
    Start-Sleep -Milliseconds 750

    $runnerParams = @{
        BaseUrl = $BaseUrl
        TargetMessagesPerSecond = $TargetMessagesPerSecond
        EventsPerRequest = $EventsPerRequest
        Duration = $Duration
        AckP95Ms = $AckP95Ms
        AckP99Ms = $AckP99Ms
        PreAllocatedVUs = $PreAllocatedVUs
        MaxVUs = $MaxVUs
        RequestTimeout = $RequestTimeout
        MessageText = $MessageText
        PageId = $PageId
        K6Executable = $K6Executable
        RabbitMqApiBaseUrl = $RabbitMqApiBaseUrl
        RabbitMqUserName = $RabbitMqUserName
        RabbitMqPassword = $RabbitMqPassword
        RabbitMqVirtualHost = $RabbitMqVirtualHost
        RawIngressQueueName = $RawIngressQueueName
        NormalizedEventQueueName = $NormalizedEventQueueName
        QueueDrainTimeoutSeconds = $QueueDrainTimeoutSeconds
        ArtifactPrefix = $K6ArtifactPrefix
    }

    if ($PSBoundParameters.ContainsKey('AppSecret') -and -not [string]::IsNullOrWhiteSpace($AppSecret)) {
        $runnerParams.AppSecret = $AppSecret
    }

    if ($SkipReadyCheck) {
        $runnerParams.SkipReadyCheck = $true
    }

    if ($SkipQueueDrainCheck) {
        $runnerParams.SkipQueueDrainCheck = $true
    }

    & $runnerScript @runnerParams
}
finally {
    New-Item -ItemType File -Path $stopFilePath -Force | Out-Null

    if ($null -ne $monitorJob) {
        Wait-Job -Job $monitorJob -Timeout 20 | Out-Null
        Receive-Job -Job $monitorJob -Wait -AutoRemoveJob | Out-Host
    }
}

Write-Host ''
Write-Host 'RabbitMQ monitoring artifacts' -ForegroundColor Green
Write-Host "  Monitor directory: $monitorDir"
Write-Host "  CSV: $(Join-Path $monitorDir 'rabbitmq-samples.csv')"
Write-Host "  JSON: $(Join-Path $monitorDir 'rabbitmq-samples.json')"
Write-Host "  Summary: $(Join-Path $monitorDir 'rabbitmq-summary.json')"
