[CmdletBinding()]
param(
    [string]$RabbitMqApiBaseUrl = 'http://localhost:15672/api',
    [string]$RabbitMqUserName = 'fbserviceext',
    [string]$RabbitMqPassword = 'DevOnly_RabbitMq2026!',
    [string]$RabbitMqVirtualHost = '/',
    [string[]]$QueueNames = @('fbserviceext.raw.ingress', 'fbserviceext.normalized.events'),
    [int]$SampleIntervalMilliseconds = 1000,
    [int]$DurationSeconds = 60,
    [string]$ArtifactDirectory,
    [string]$Label = 'rabbitmq-monitor',
    [string]$StopFilePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($SampleIntervalMilliseconds -lt 200) {
    throw 'SampleIntervalMilliseconds must be at least 200.'
}

if ($DurationSeconds -lt 1) {
    throw 'DurationSeconds must be at least 1.'
}

$solutionRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $solutionRoot 'artifacts\perf'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifactDir = if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    Join-Path $artifactRoot "$Label-$timestamp"
}
else {
    $ArtifactDirectory
}

New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

$csvPath = Join-Path $artifactDir 'rabbitmq-samples.csv'
$jsonPath = Join-Path $artifactDir 'rabbitmq-samples.json'
$summaryPath = Join-Path $artifactDir 'rabbitmq-summary.json'

$auth = [Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes("${RabbitMqUserName}:${RabbitMqPassword}"))
$headers = @{ Authorization = "Basic $auth" }
$encodedVirtualHost = [System.Uri]::EscapeDataString($RabbitMqVirtualHost)
$deadline = (Get-Date).AddSeconds($DurationSeconds)

$samples = New-Object System.Collections.Generic.List[object]

Write-Host "Starting RabbitMQ monitor..." -ForegroundColor Cyan
Write-Host "  API: $RabbitMqApiBaseUrl"
Write-Host "  Queues: $($QueueNames -join ', ')"
Write-Host "  Interval: ${SampleIntervalMilliseconds}ms"
Write-Host "  Duration: ${DurationSeconds}s"
Write-Host "  Artifacts: $artifactDir"
if (-not [string]::IsNullOrWhiteSpace($StopFilePath)) {
    Write-Host "  Stop file: $StopFilePath"
}

while ((Get-Date) -lt $deadline) {
    if (-not [string]::IsNullOrWhiteSpace($StopFilePath) -and (Test-Path $StopFilePath)) {
        break
    }

    $sampledAtUtc = (Get-Date).ToUniversalTime().ToString('o')

    foreach ($queueName in $QueueNames) {
        $encodedQueue = [System.Uri]::EscapeDataString($queueName)
        $queueUrl = "$RabbitMqApiBaseUrl/queues/$encodedVirtualHost/$encodedQueue"
        $queue = Invoke-RestMethod -Uri $queueUrl -Headers $headers -Method Get

        $messageStats = $queue.message_stats
        $publishRate = if ($null -ne $messageStats.publish_details) { [double]$messageStats.publish_details.rate } else { 0d }
        $deliverRate = if ($null -ne $messageStats.deliver_get_details) { [double]$messageStats.deliver_get_details.rate } else { 0d }
        $ackRate = if ($null -ne $messageStats.ack_details) { [double]$messageStats.ack_details.rate } else { 0d }
        $redeliverRate = if ($null -ne $messageStats.redeliver_details) { [double]$messageStats.redeliver_details.rate } else { 0d }

        $sample = [pscustomobject]@{
            SampledAtUtc = $sampledAtUtc
            Queue = $queue.name
            Messages = [int]$queue.messages
            MessagesReady = [int]$queue.messages_ready
            MessagesUnacknowledged = [int]$queue.messages_unacknowledged
            Consumers = [int]$queue.consumers
            PublishCount = if ($null -ne $messageStats.publish) { [int]$messageStats.publish } else { 0 }
            DeliverGetCount = if ($null -ne $messageStats.deliver_get) { [int]$messageStats.deliver_get } else { 0 }
            AckCount = if ($null -ne $messageStats.ack) { [int]$messageStats.ack } else { 0 }
            RedeliverCount = if ($null -ne $messageStats.redeliver) { [int]$messageStats.redeliver } else { 0 }
            PublishRate = [math]::Round($publishRate, 3)
            DeliverGetRate = [math]::Round($deliverRate, 3)
            AckRate = [math]::Round($ackRate, 3)
            RedeliverRate = [math]::Round($redeliverRate, 3)
            MemoryBytes = if ($null -ne $queue.memory) { [long]$queue.memory } else { 0L }
        }

        $samples.Add($sample)
    }

    Start-Sleep -Milliseconds $SampleIntervalMilliseconds
}

$samples | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
$samples | ConvertTo-Json -Depth 5 | Set-Content -Path $jsonPath -Encoding UTF8

$summary = foreach ($queueName in $QueueNames) {
    $queueSamples = @($samples | Where-Object { $_.Queue -eq $queueName })
    if ($queueSamples.Count -eq 0) {
        continue
    }

    [pscustomobject]@{
        Queue = $queueName
        SampleCount = $queueSamples.Count
        MaxMessages = ($queueSamples | Measure-Object -Property Messages -Maximum).Maximum
        MaxMessagesReady = ($queueSamples | Measure-Object -Property MessagesReady -Maximum).Maximum
        MaxMessagesUnacknowledged = ($queueSamples | Measure-Object -Property MessagesUnacknowledged -Maximum).Maximum
        MaxPublishCount = ($queueSamples | Measure-Object -Property PublishCount -Maximum).Maximum
        MaxDeliverGetCount = ($queueSamples | Measure-Object -Property DeliverGetCount -Maximum).Maximum
        MaxAckCount = ($queueSamples | Measure-Object -Property AckCount -Maximum).Maximum
        MaxRedeliverCount = ($queueSamples | Measure-Object -Property RedeliverCount -Maximum).Maximum
        MaxPublishRate = ($queueSamples | Measure-Object -Property PublishRate -Maximum).Maximum
        MaxDeliverGetRate = ($queueSamples | Measure-Object -Property DeliverGetRate -Maximum).Maximum
        MaxAckRate = ($queueSamples | Measure-Object -Property AckRate -Maximum).Maximum
        MaxRedeliverRate = ($queueSamples | Measure-Object -Property RedeliverRate -Maximum).Maximum
        PeakMemoryBytes = ($queueSamples | Measure-Object -Property MemoryBytes -Maximum).Maximum
        LastMessages = $queueSamples[-1].Messages
        LastMessagesReady = $queueSamples[-1].MessagesReady
        LastMessagesUnacknowledged = $queueSamples[-1].MessagesUnacknowledged
        LastPublishCount = $queueSamples[-1].PublishCount
        LastDeliverGetCount = $queueSamples[-1].DeliverGetCount
        LastAckCount = $queueSamples[-1].AckCount
    }
}

$summary | ConvertTo-Json -Depth 5 | Set-Content -Path $summaryPath -Encoding UTF8

Write-Host ''
Write-Host 'RabbitMQ monitor summary' -ForegroundColor Green
$summary | Format-Table Queue,SampleCount,MaxMessages,MaxMessagesReady,MaxMessagesUnacknowledged,MaxPublishCount,MaxDeliverGetCount,MaxAckCount,LastMessages -AutoSize
Write-Host ''
Write-Host "CSV: $csvPath"
Write-Host "JSON: $jsonPath"
Write-Host "Summary: $summaryPath"
