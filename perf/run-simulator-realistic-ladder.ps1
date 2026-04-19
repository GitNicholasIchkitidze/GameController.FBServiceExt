[CmdletBinding()]
param(
    [int[]]$Targets = @(100, 250, 500),
    [int]$Workers = 8,
    [int]$DurationSeconds = 300,
    [int]$StartupJitterSeconds = 30,
    [int]$MinThinkMilliseconds = 250,
    [int]$MaxThinkMilliseconds = 1000,
    [int]$InitialOutboundWaitSeconds = 20,
    [int]$RetryOutboundWaitSeconds = 25,
    [int]$MaxAttemptsPerStage = 3,
    [int]$InitialUsersPerReqPerSec = 75,
    [double]$ReqPerSecTolerancePercent = 10,
    [double]$MaxWebhookFailureRatePercent = 1.0,
    [double]$MinFirstStageCompletionRatePercent = 12.0,
    [double]$MinCompletionRetentionPercent = 70.0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$solutionRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $solutionRoot 'artifacts\simulator-headless'
$runStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $artifactsRoot "realistic-ladder-$runStamp"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$summaryJsonPath = Join-Path $runDir 'ladder-summary.json'
$summaryTextPath = Join-Path $runDir 'ladder-summary.txt'
$runnerScript = Join-Path $PSScriptRoot 'run-headless-simulator-capacity.ps1'

function Get-LatestStageRun {
    param([datetime]$StartedAfter)

    Get-ChildItem $artifactsRoot -Directory |
        Where-Object { $_.Name -like 'run-*' -and $_.LastWriteTime -ge $StartedAfter.AddSeconds(-2) } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

function Invoke-StageAttempt {
    param(
        [int]$TargetReqPerSec,
        [int]$Users,
        [int]$OutboundWaitSeconds,
        [int]$Attempt
    )

    $startedAt = Get-Date
    $null = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runnerScript `
        -Users $Users `
        -Workers $Workers `
        -DurationSeconds $DurationSeconds `
        -StartupJitterSeconds $StartupJitterSeconds `
        -MinThinkMilliseconds $MinThinkMilliseconds `
        -MaxThinkMilliseconds $MaxThinkMilliseconds `
        -OutboundWaitSeconds $OutboundWaitSeconds
    $exitCode = $LASTEXITCODE

    $latestRun = Get-LatestStageRun -StartedAfter $startedAt
    if ($null -eq $latestRun) {
        throw "No headless simulator artifact directory was produced for target $TargetReqPerSec attempt $Attempt."
    }

    $headlessSummaryPath = Join-Path $latestRun.FullName 'headless-summary.json'
    if (-not (Test-Path $headlessSummaryPath)) {
        throw "Headless summary json not found: $headlessSummaryPath"
    }

    $summary = Get-Content $headlessSummaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $actualReqPerSec = [double]$summary.ActualReqPerSec
    $completionRate = [double]$summary.CycleCompletionRatePercent
    $webhookFailureRate = [double]$summary.WebhookFailureRatePercent
    $carouselTimeoutRate = if ([double]$summary.CyclesStarted -gt 0) { ([double]$summary.CarouselTimeouts / [double]$summary.CyclesStarted) * 100 } else { 0 }

    return [pscustomobject]@{
        TargetReqPerSec = $TargetReqPerSec
        Attempt = $Attempt
        Users = $Users
        OutboundWaitSeconds = $OutboundWaitSeconds
        ExitCode = $exitCode
        ArtifactDirectory = $latestRun.FullName
        HeadlessSummaryPath = $headlessSummaryPath
        Summary = $summary
        ActualReqPerSec = $actualReqPerSec
        CompletionRatePercent = $completionRate
        WebhookFailureRatePercent = $webhookFailureRate
        CarouselTimeoutRatePercent = [math]::Round($carouselTimeoutRate, 2)
        WithinTargetBand = if ($TargetReqPerSec -le 0) { $false } else { [math]::Abs($actualReqPerSec - $TargetReqPerSec) / $TargetReqPerSec * 100 -le $ReqPerSecTolerancePercent }
    }
}

function Select-BestAttempt {
    param([object[]]$Attempts)

    $attemptArray = @($Attempts)
    $banded = @($attemptArray | Where-Object { $_.PSObject.Properties.Match('WithinTargetBand').Count -gt 0 -and $_.WithinTargetBand })
    if ($banded.Count -gt 0) {
        return $banded | Sort-Object @{ Expression = 'CompletionRatePercent'; Descending = $true }, @{ Expression = 'WebhookFailureRatePercent'; Descending = $false } | Select-Object -First 1
    }

    return $attemptArray | Sort-Object @{ Expression = { [math]::Abs($_.ActualReqPerSec - $_.TargetReqPerSec) } }, @{ Expression = 'CompletionRatePercent'; Descending = $true } | Select-Object -First 1
}

$stageResults = New-Object System.Collections.Generic.List[object]
$previousBest = $null
$seedUsers = 0

foreach ($target in $Targets) {
    $stageAttempts = New-Object System.Collections.Generic.List[object]
    if ($previousBest -eq $null) {
        $seedUsers = [math]::Max(1, [int][math]::Ceiling($target * $InitialUsersPerReqPerSec))
    }
    else {
        $seedUsers = [math]::Max(1, [int][math]::Ceiling([double]$previousBest.Users * ($target / [math]::Max([double]$previousBest.ActualReqPerSec, 1.0))))
    }

    foreach ($waitSeconds in @($InitialOutboundWaitSeconds, $RetryOutboundWaitSeconds)) {
        $usersForAttempt = $seedUsers
        for ($attempt = 1; $attempt -le $MaxAttemptsPerStage; $attempt++) {
            Write-Host "Running stage target=$target req/s, wait=$waitSeconds s, attempt=$attempt, users=$usersForAttempt" -ForegroundColor Cyan
            $result = Invoke-StageAttempt -TargetReqPerSec $target -Users $usersForAttempt -OutboundWaitSeconds $waitSeconds -Attempt $attempt
            $stageAttempts.Add($result) | Out-Null

            if ($result.WithinTargetBand) {
                break
            }

            if ($result.ActualReqPerSec -le 0) {
                break
            }

            $usersForAttempt = [math]::Max(1, [int][math]::Ceiling($usersForAttempt * ($target / $result.ActualReqPerSec)))
        }
    }

    $best = Select-BestAttempt -Attempts ($stageAttempts.ToArray())
    $stageGatePassed = $best.ExitCode -eq 0 -and $best.WebhookFailureRatePercent -le $MaxWebhookFailureRatePercent
    if ($stageGatePassed -and $previousBest -eq $null) {
        $stageGatePassed = $best.CompletionRatePercent -gt $MinFirstStageCompletionRatePercent
    }
    if ($stageGatePassed -and $previousBest -ne $null) {
        $stageGatePassed = $best.CompletionRatePercent -ge ($previousBest.CompletionRatePercent * ($MinCompletionRetentionPercent / 100.0))
    }

    $stageResult = [pscustomobject]@{
        TargetReqPerSec = $target
        BestAttempt = $best
        Attempts = @($stageAttempts)
        StagePassed = $stageGatePassed
    }
    $stageResults.Add($stageResult) | Out-Null

    if (-not $stageGatePassed) {
        Write-Host "Stopping ladder after target $target req/s due to stage gate failure." -ForegroundColor Yellow
        break
    }

    $previousBest = $best
}

$stageResults | ConvertTo-Json -Depth 8 | Set-Content -Path $summaryJsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('Simulator realistic ladder summary') | Out-Null
$lines.Add("GeneratedAtUtc: $([DateTime]::UtcNow.ToString('o'))") | Out-Null
$lines.Add('') | Out-Null
foreach ($stage in $stageResults) {
    $best = $stage.BestAttempt
    $lines.Add(("TargetReqPerSec: {0}" -f $stage.TargetReqPerSec)) | Out-Null
    $lines.Add(("  StagePassed: {0}" -f $stage.StagePassed)) | Out-Null
    $lines.Add(("  BestUsers: {0}" -f $best.Users)) | Out-Null
    $lines.Add(("  BestOutboundWaitSeconds: {0}" -f $best.OutboundWaitSeconds)) | Out-Null
    $lines.Add(("  ActualReqPerSec: {0:N2}" -f $best.ActualReqPerSec)) | Out-Null
    $lines.Add(("  CompletionRatePercent: {0:N2}" -f $best.CompletionRatePercent)) | Out-Null
    $lines.Add(("  WebhookFailureRatePercent: {0:N2}" -f $best.WebhookFailureRatePercent)) | Out-Null
    $lines.Add(("  CarouselTimeoutRatePercent: {0:N2}" -f $best.CarouselTimeoutRatePercent)) | Out-Null
    $lines.Add(("  DominantStageTimeout: {0}" -f $best.Summary.DominantStageTimeout)) | Out-Null
    $lines.Add(("  MonitorWarningCount: {0}" -f $best.Summary.MonitorWarningCount)) | Out-Null
    $lines.Add(("  ArtifactDirectory: {0}" -f $best.ArtifactDirectory)) | Out-Null
    $lines.Add('') | Out-Null
}
$lines.Add(("Json: {0}" -f $summaryJsonPath)) | Out-Null
$lines | Set-Content -Path $summaryTextPath -Encoding UTF8

Write-Host ''
Write-Host 'Realistic ladder completed.' -ForegroundColor Green
Write-Host "JSON: $summaryJsonPath"
Write-Host "Text: $summaryTextPath"
