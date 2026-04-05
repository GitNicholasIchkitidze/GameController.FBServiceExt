param(
    [int]$Users = 15000,
    [int]$Workers = 2,
    [int]$DurationSeconds = 300,
    [int]$StartupJitterSeconds = 30,
    [int]$MinThinkMilliseconds = 250,
    [int]$MaxThinkMilliseconds = 1000,
    [int]$OutboundWaitSeconds = 15,
    [switch]$SkipWarmup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$runStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $root "artifacts\simulator-headless\run-$runStamp"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

$sqlContainerName = 'fbserviceext-sqlserver'
$sqlImage = 'mcr.microsoft.com/mssql/server:2022-latest'
$sqlHost = '127.0.0.1'
$sqlPort = 14333
$sqlDatabaseName = 'GameControllerFBServiceExt'
$sqlSaPassword = 'FbServiceExt_Strong_2026!'
$sqlMasterConnectionString = "Server=$sqlHost,$sqlPort;Database=master;User ID=sa;Password=$sqlSaPassword;Encrypt=True;TrustServerCertificate=True;"
$sqlConnectionString = "Server=$sqlHost,$sqlPort;Database=$sqlDatabaseName;User ID=sa;Password=$sqlSaPassword;Encrypt=True;TrustServerCertificate=True;"

$rabbitApi = 'http://127.0.0.1:15672/api'
$rabbitUser = 'fbserviceext'
$rabbitPassword = 'DevOnly_RabbitMq2026!'
$rawQueue = 'fbserviceext.raw.ingress'
$normalizedQueue = 'fbserviceext.normalized.events'

$apiExe = Join-Path $root 'src\GameController.FBServiceExt\bin\Release\net8.0\GameController.FBServiceExt.exe'
$workerExe = Join-Path $root 'src\GameController.FBServiceExt.Worker\bin\Release\net8.0\GameController.FBServiceExt.Worker.exe'
$simExe = Join-Path $root 'src\GameController.FBServiceExt.FakeFBForSimulate\bin\Release\net8.0-windows\GameController.FBServiceExt.FakeFBForSimulate.exe'
$apiContentRoot = Join-Path $root 'src\GameController.FBServiceExt'

$appDataRoot = Join-Path $root '.simulator-appdata'
$localAppDataRoot = Join-Path $root '.simulator-localappdata'
$dotnetCliHome = Join-Path $root '.simulator-dotnet'
New-Item -ItemType Directory -Force -Path $appDataRoot, $localAppDataRoot, $dotnetCliHome | Out-Null

$apiLog = Join-Path $runDir 'api.log'
$apiErrLog = Join-Path $runDir 'api.err.log'
$simLog = Join-Path $runDir 'simulator.log'
$metricsJson = Join-Path $runDir 'metrics.json'
$queuesJson = Join-Path $runDir 'queues.json'
$dbSummaryJson = Join-Path $runDir 'db-summary.json'
$summaryTxt = Join-Path $runDir 'summary.txt'

function Stop-ExistingProcesses {
    [void](& cmd.exe /c 'taskkill /F /IM GameController.FBServiceExt.exe /T >nul 2>&1')
    [void](& cmd.exe /c 'taskkill /F /IM GameController.FBServiceExt.Worker.exe /T >nul 2>&1')
    [void](& cmd.exe /c 'taskkill /F /IM GameController.FBServiceExt.FakeFBForSimulate.exe /T >nul 2>&1')
}

function Invoke-Docker {
    param([string[]]$Arguments)
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Docker command failed: docker $($Arguments -join ' ')"
    }
}

function Ensure-SqlContainer {
    $inspect = & docker inspect $sqlContainerName 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Creating $sqlContainerName..."
        Invoke-Docker @('run','-d','--name',$sqlContainerName,'-e','ACCEPT_EULA=Y','-e',"MSSQL_SA_PASSWORD=$sqlSaPassword",'-p',"$sqlPort:1433",$sqlImage)
    }
    else {
        $running = & docker inspect -f '{{.State.Running}}' $sqlContainerName
        if ($LASTEXITCODE -ne 0) {
            throw 'Failed to inspect SQL container state.'
        }
        if ($running.Trim() -ne 'true') {
            Write-Host "Starting existing $sqlContainerName..."
            Invoke-Docker @('start',$sqlContainerName)
        }
    }

    Write-Host 'Waiting for SQL readiness...'
    $deadline = (Get-Date).AddMinutes(2)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $conn = New-Object System.Data.SqlClient.SqlConnection $sqlMasterConnectionString
            $conn.Open()
            $cmd = $conn.CreateCommand()
            $cmd.CommandText = 'SELECT 1'
            [void]$cmd.ExecuteScalar()
            $conn.Close()
            $ready = $true
            break
        }
        catch {
            Start-Sleep -Seconds 2
        }
    }

    if (-not $ready) {
        throw 'SQL container did not become ready in time.'
    }

    $conn = New-Object System.Data.SqlClient.SqlConnection $sqlMasterConnectionString
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "IF DB_ID(N'$sqlDatabaseName') IS NULL CREATE DATABASE [$sqlDatabaseName]"
    [void]$cmd.ExecuteNonQuery()
    $conn.Close()
}

function Reset-State {
    Write-Host 'Resetting SQL/Redis state...'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'perf\reset-fake-fb-state.ps1') -SqlContainerName $sqlContainerName -SqlDatabaseName $sqlDatabaseName -SqlSaPassword $sqlSaPassword -RedisContainerName 'fbserviceext-redis' -RedisKeyPrefix 'fbserviceext'
    if ($LASTEXITCODE -ne 0) {
        throw 'State reset failed.'
    }
}

function Purge-Queues {
    Write-Host 'Purging RabbitMQ queues...'
    $pair = "$rabbitUser`:$rabbitPassword"
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($pair)
    $auth = [Convert]::ToBase64String($bytes)
    $headers = @{ Authorization = 'Basic ' + $auth }
    Invoke-RestMethod -Method Delete -Headers $headers -Uri "$rabbitApi/queues/%2F/$rawQueue/contents" | Out-Null
    Invoke-RestMethod -Method Delete -Headers $headers -Uri "$rabbitApi/queues/%2F/$normalizedQueue/contents" | Out-Null
}

function Start-LoggedProcess {
    param(
        [string]$FileName,
        [string]$Arguments,
        [string]$WorkingDirectory,
        [hashtable]$Environment,
        [string]$StdOutPath,
        [string]$StdErrPath
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FileName
    $psi.Arguments = $Arguments
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    foreach ($entry in $Environment.GetEnumerator()) {
        $psi.Environment[$entry.Key] = [string]$entry.Value
    }

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    $null = $process.Start()

    $stdoutTask = [System.Threading.Tasks.Task[string]]::Run([Func[string]]{ $process.StandardOutput.ReadToEnd() })
    $stderrTask = [System.Threading.Tasks.Task[string]]::Run([Func[string]]{ $process.StandardError.ReadToEnd() })

    [pscustomobject]@{
        Process = $process
        StdOutTask = $stdoutTask
        StdErrTask = $stderrTask
        StdOutPath = $StdOutPath
        StdErrPath = $StdErrPath
    }
}

function Stop-LoggedProcess {
    param(
        $Handle,
        [string]$ImageName
    )

    if ($null -eq $Handle) { return }

    if (-not [string]::IsNullOrWhiteSpace($ImageName)) {
        [void](& cmd.exe /c "taskkill /F /IM $ImageName /T >nul 2>&1")
        Start-Sleep -Milliseconds 500
    }

    $process = $Handle.Process
    if ($process -and -not $process.HasExited) {
        try { $process.Kill($true) } catch {}
        $null = $process.WaitForExit(3000)
    }

    if ($Handle.StdOutTask.Wait(3000)) {
        [System.IO.File]::WriteAllText($Handle.StdOutPath, $Handle.StdOutTask.Result, [System.Text.Encoding]::UTF8)
    }
    else {
        [System.IO.File]::WriteAllText($Handle.StdOutPath, '[log capture incomplete]', [System.Text.Encoding]::UTF8)
    }

    if ($Handle.StdErrTask.Wait(3000)) {
        [System.IO.File]::WriteAllText($Handle.StdErrPath, $Handle.StdErrTask.Result, [System.Text.Encoding]::UTF8)
    }
    else {
        [System.IO.File]::WriteAllText($Handle.StdErrPath, '[log capture incomplete]', [System.Text.Encoding]::UTF8)
    }

    if ($process) { $process.Dispose() }
}

function Wait-ForApiHealth {
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest 'http://127.0.0.1:5277/health/ready' -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -eq 200) { return }
        }
        catch {}
        Start-Sleep -Seconds 2
    }
    throw 'API health endpoint did not become ready in time.'
}

function Wait-ForWorkerConsumers {
    param([int]$ExpectedConsumers)

    $pair = "$rabbitUser`:$rabbitPassword"
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($pair)
    $auth = [Convert]::ToBase64String($bytes)
    $headers = @{ Authorization = 'Basic ' + $auth }
    $deadline = (Get-Date).AddSeconds(90)

    while ((Get-Date) -lt $deadline) {
        try {
            $queues = Invoke-RestMethod -Method Get -Headers $headers -Uri "$rabbitApi/queues/%2F"
            $raw = $queues | Where-Object { $_.name -eq $rawQueue }
            $normalized = $queues | Where-Object { $_.name -eq $normalizedQueue }
            if ($raw.consumers -ge $ExpectedConsumers -and $normalized.consumers -ge $ExpectedConsumers) {
                return
            }
        }
        catch {}
        Start-Sleep -Seconds 2
    }

    throw "Worker consumers did not attach to RabbitMQ queues in time. Expected at least $ExpectedConsumers."
}

function Save-MetricsSnapshot {
    try {
        Invoke-RestMethod 'http://127.0.0.1:5277/dev/metrics/api' | ConvertTo-Json -Depth 12 | Set-Content -Path $metricsJson -Encoding UTF8
    }
    catch {
        "metrics snapshot failed: $($_.Exception.Message)" | Set-Content -Path $metricsJson -Encoding UTF8
    }
}

function Save-QueueSnapshot {
    try {
        $pair = "$rabbitUser`:$rabbitPassword"
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($pair)
        $auth = [Convert]::ToBase64String($bytes)
        $headers = @{ Authorization = 'Basic ' + $auth }
        Invoke-RestMethod -Method Get -Headers $headers -Uri "$rabbitApi/queues/%2F" | Select-Object name,consumers,messages,messages_ready,messages_unacknowledged | ConvertTo-Json -Depth 6 | Set-Content -Path $queuesJson -Encoding UTF8
    }
    catch {
        "queue snapshot failed: $($_.Exception.Message)" | Set-Content -Path $queuesJson -Encoding UTF8
    }
}

function Get-DbCounts {
    $query = "SET NOCOUNT ON; SELECT 'NormalizedEvents' AS [Table], COUNT(*) AS [Count] FROM dbo.NormalizedEvents UNION ALL SELECT 'AcceptedVotes' AS [Table], COUNT(*) AS [Count] FROM dbo.AcceptedVotes;"
    $rows = & docker exec $sqlContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $sqlSaPassword -C -d $sqlDatabaseName -Q $query -W -s '|'
    if ($LASTEXITCODE -ne 0) { throw 'sqlcmd failed' }
    return $rows
}

function Save-DbSummary {
    try {
        (Get-DbCounts) | Set-Content -Path $dbSummaryJson -Encoding UTF8
    }
    catch {
        "db summary failed: $($_.Exception.Message)" | Set-Content -Path $dbSummaryJson -Encoding UTF8
    }
}

function Invoke-HeadlessSimulator {
    param(
        [int]$SimUsers,
        [int]$SimDurationSeconds,
        [int]$SimStartupJitterSeconds,
        [int]$SimOutboundWaitSeconds,
        [int]$SimMinThinkMilliseconds,
        [int]$SimMaxThinkMilliseconds,
        [string]$LogPath
    )

    & $simExe --headless --users $SimUsers --duration-seconds $SimDurationSeconds --cooldown-seconds 60 --startup-jitter-seconds $SimStartupJitterSeconds --min-think-ms $SimMinThinkMilliseconds --max-think-ms $SimMaxThinkMilliseconds --outbound-wait-seconds $SimOutboundWaitSeconds 2>&1 | Tee-Object -FilePath $LogPath
    if ($null -ne $LASTEXITCODE) { return $LASTEXITCODE }
    return 0
}

function Write-RunSummary {
    try {
        $simLogContent = Get-Content -Path $simLog -Raw
        $match = [regex]::Match($simLogContent, 'FinalSnapshot:\s+active=(?<active>\d+),\s+started=(?<started>\d+),\s+completed=(?<completed>\d+),\s+failed=(?<failed>\d+),\s+webhookAttempts=(?<attempts>\d+),\s+webhookSuccesses=(?<successes>\d+),\s+webhookFailures=(?<failures>\d+),\s+outbound=(?<outbound>\d+),\s+acceptedTexts=(?<accepted>\d+),\s+unexpectedShapes=(?<unexpected>\d+),\s+averageCompletedCycleMs=(?<avg>[0-9.]+)')
        $dbRows = Get-DbCounts

        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add('Headless simulator capacity run')
        $lines.Add('==============================')
        $lines.Add("Users: $Users")
        $lines.Add("Workers: $Workers")
        $lines.Add("DurationSeconds: $DurationSeconds")
        $lines.Add("StartupJitterSeconds: $StartupJitterSeconds")
        $lines.Add("MinThinkMilliseconds: $MinThinkMilliseconds")
        $lines.Add("MaxThinkMilliseconds: $MaxThinkMilliseconds")
        $lines.Add("OutboundWaitSeconds: $OutboundWaitSeconds")
        $lines.Add('')

        if ($match.Success) {
            $attempts = [double]$match.Groups['attempts'].Value
            $lines.Add('Final snapshot')
            $lines.Add('--------------')
            $lines.Add("CyclesStarted: $($match.Groups['started'].Value)")
            $lines.Add("CyclesCompleted: $($match.Groups['completed'].Value)")
            $lines.Add("CyclesFailed: $($match.Groups['failed'].Value)")
            $lines.Add("WebhookAttempts: $($match.Groups['attempts'].Value)")
            $lines.Add("WebhookSuccesses: $($match.Groups['successes'].Value)")
            $lines.Add("WebhookFailures: $($match.Groups['failures'].Value)")
            $lines.Add("OutboundMessagesReceived: $($match.Groups['outbound'].Value)")
            $lines.Add("AcceptedTextsReceived: $($match.Groups['accepted'].Value)")
            $lines.Add("UnexpectedOutboundShapes: $($match.Groups['unexpected'].Value)")
            $lines.Add("AverageCompletedCycleMs: $($match.Groups['avg'].Value)")
            $lines.Add(("ActualReqPerSec: {0:N2}" -f ($attempts / [Math]::Max($DurationSeconds, 1))))
            $lines.Add('')
        }

        $lines.Add('Database counts')
        $lines.Add('---------------')
        foreach ($row in $dbRows) {
            $lines.Add($row)
        }
        $lines.Add('')
        $lines.Add("MetricsJson: $metricsJson")
        $lines.Add("QueuesJson: $queuesJson")
        $lines.Add("DbSummary: $dbSummaryJson")
        $lines.Add("ApiLog: $apiLog")
        $lines.Add("ApiErrLog: $apiErrLog")
        $lines.Add("SimulatorLog: $simLog")

        [System.IO.File]::WriteAllLines($summaryTxt, $lines, [System.Text.Encoding]::UTF8)
    }
    catch {
        "summary generation failed: $($_.Exception.Message)" | Set-Content -Path $summaryTxt -Encoding UTF8
    }
}

Write-Host '=========================================='
Write-Host 'Headless FakeFB Capacity Runner'
Write-Host '=========================================='
Write-Host "Users               : $Users"
Write-Host "Workers             : $Workers"
Write-Host "Duration sec        : $DurationSeconds"
Write-Host "Startup jitter sec  : $StartupJitterSeconds"
Write-Host "Think ms            : $MinThinkMilliseconds..$MaxThinkMilliseconds"
Write-Host "Outbound wait sec   : $OutboundWaitSeconds"
Write-Host "Run dir             : $runDir"
Write-Host ''

$apiHandle = $null
$workerHandles = New-Object System.Collections.Generic.List[object]
$simExitCode = 1
try {
    Stop-ExistingProcesses
    Ensure-SqlContainer
    Reset-State
    Purge-Queues

    Write-Host 'Building Release solution...'
    & dotnet build (Join-Path $root 'GameController.FBServiceExt.sln') -c Release -m:1 -nr:false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw 'Release build failed.'
    }

    if (-not (Test-Path $apiExe)) { throw "API executable not found: $apiExe" }
    if (-not (Test-Path $workerExe)) { throw "Worker executable not found: $workerExe" }
    if (-not (Test-Path $simExe)) { throw "Simulator executable not found: $simExe" }

    $commonEnvironment = @{
        'DOTNET_ENVIRONMENT' = 'Simulator'
        'ASPNETCORE_ENVIRONMENT' = 'Simulator'
        'APPDATA' = $appDataRoot
        'LOCALAPPDATA' = $localAppDataRoot
        'DOTNET_CLI_HOME' = $dotnetCliHome
        'DOTNET_SKIP_FIRST_TIME_EXPERIENCE' = '1'
        'DOTNET_NOLOGO' = '1'
    }

    $apiHandle = Start-LoggedProcess -FileName $apiExe -Arguments "--urls http://127.0.0.1:5277 --contentRoot `"$apiContentRoot`"" -WorkingDirectory (Split-Path $apiExe -Parent) -Environment $commonEnvironment -StdOutPath $apiLog -StdErrPath $apiErrLog

    for ($index = 1; $index -le $Workers; $index++) {
        $workerOut = Join-Path $runDir ("worker-{0:D2}.log" -f $index)
        $workerErr = Join-Path $runDir ("worker-{0:D2}.err.log" -f $index)
        $workerEnvironment = @{}
        foreach ($entry in $commonEnvironment.GetEnumerator()) { $workerEnvironment[$entry.Key] = $entry.Value }
        $workerEnvironment['SqlStorage__ConnectionString'] = $sqlConnectionString
        $workerEnvironment['FB_SIM_MANAGED_WORKER_SLOT'] = $index.ToString()
        $workerHandles.Add((Start-LoggedProcess -FileName $workerExe -Arguments '' -WorkingDirectory (Split-Path $workerExe -Parent) -Environment $workerEnvironment -StdOutPath $workerOut -StdErrPath $workerErr))
    }

    Start-Sleep -Seconds 3
    if ($apiHandle.Process.HasExited) { throw 'API process exited during startup.' }
    foreach ($handle in $workerHandles) {
        if ($handle.Process.HasExited) { throw "Worker process exited during startup. PID=$($handle.Process.Id)" }
    }

    Wait-ForApiHealth
    Wait-ForWorkerConsumers -ExpectedConsumers $Workers
    Start-Sleep -Seconds 3

    if (-not $SkipWarmup) {
        Write-Host 'Running warm-up cycle...'
        [void](Invoke-HeadlessSimulator -SimUsers 1 -SimDurationSeconds 20 -SimStartupJitterSeconds 0 -SimOutboundWaitSeconds 25 -SimMinThinkMilliseconds 500 -SimMaxThinkMilliseconds 1200 -LogPath (Join-Path $runDir 'warmup.log'))
        Start-Sleep -Seconds 2
        Reset-State
        Purge-Queues
        Start-Sleep -Seconds 1
    }

    Write-Host 'Running headless simulator...'
    $simExitCode = Invoke-HeadlessSimulator -SimUsers $Users -SimDurationSeconds $DurationSeconds -SimStartupJitterSeconds $StartupJitterSeconds -SimOutboundWaitSeconds $OutboundWaitSeconds -SimMinThinkMilliseconds $MinThinkMilliseconds -SimMaxThinkMilliseconds $MaxThinkMilliseconds -LogPath $simLog

    Save-MetricsSnapshot
    Save-QueueSnapshot
    Save-DbSummary
    Write-RunSummary
}
catch {
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    Save-MetricsSnapshot
    Save-QueueSnapshot
    Save-DbSummary
    Write-RunSummary
    $simExitCode = 1
}
finally {
    Stop-LoggedProcess $apiHandle 'GameController.FBServiceExt.exe'
    foreach ($handle in $workerHandles) {
        Stop-LoggedProcess $handle 'GameController.FBServiceExt.Worker.exe'
    }
    Stop-ExistingProcesses

    Write-Host ''
    Write-Host 'Artifacts:'
    Write-Host "  Summary      : $summaryTxt"
    Write-Host "  Simulator    : $simLog"
    Write-Host "  Metrics json : $metricsJson"
    Write-Host "  Queues json  : $queuesJson"
    Write-Host "  DB summary   : $dbSummaryJson"
}

exit $simExitCode
