@echo off
setlocal EnableExtensions

set "SOLUTION_ROOT=%~dp0.."
pushd "%SOLUTION_ROOT%" >nul

set "FAKE_FB_USERS=%~1"
if "%FAKE_FB_USERS%"=="" set "FAKE_FB_USERS=200"

set "DURATION=%~2"
if "%DURATION%"=="" set "DURATION=600s"

set "COOLDOWN_SECONDS=%~3"
if "%COOLDOWN_SECONDS%"=="" set "COOLDOWN_SECONDS=60"

set "OUTBOUND_WAIT_SECONDS=%~4"
if "%OUTBOUND_WAIT_SECONDS%"=="" set "OUTBOUND_WAIT_SECONDS=10"

set "CYCLE_P95_MS=%~5"
if "%CYCLE_P95_MS%"=="" set "CYCLE_P95_MS=120000"

set "WORKER_INSTANCES=%~6"
if "%WORKER_INSTANCES%"=="" set "WORKER_INSTANCES=1"

set "SHARD_COUNT=%~7"
if "%SHARD_COUNT%"=="" set "SHARD_COUNT=1"

set "STARTUP_JITTER_SECONDS=%~8"
if "%STARTUP_JITTER_SECONDS%"=="" set "STARTUP_JITTER_SECONDS=30"

set /a TOTAL_FAKE_FB_USERS=%FAKE_FB_USERS% * %SHARD_COUNT%

set "BASE_URL=http://127.0.0.1:5277"
set "FAKE_META_BASE_URL=http://127.0.0.1:5277/fake-meta"
set "HOST_ENVIRONMENT=PerformanceFakeFb"
set "MESSAGE_TEXT=GET_STARTED"
set "PAGE_ID=PAGE_ID_PERF"
set "K6_ARTIFACT_PREFIX=fake-fb-voting-cycle"
set "MONITOR_ARTIFACT_PREFIX=fake-fb-voting-cycle-monitor"
if not "%SHARD_COUNT%"=="1" set "K6_ARTIFACT_PREFIX=fake-fb-voting-cycle-sharded"
if not "%SHARD_COUNT%"=="1" set "MONITOR_ARTIFACT_PREFIX=fake-fb-voting-cycle-sharded-monitor"
set "API_EXE=%SOLUTION_ROOT%\src\GameController.FBServiceExt\bin\Release\net8.0\GameController.FBServiceExt.exe"
set "API_CONTENT_ROOT=%SOLUTION_ROOT%\src\GameController.FBServiceExt"
set "WORKER_EXE=%SOLUTION_ROOT%\src\GameController.FBServiceExt.Worker\bin\Release\net8.0\GameController.FBServiceExt.Worker.exe"
set "SQL_CONTAINER_NAME=fbserviceext-sqlserver"
set "SQL_IMAGE=mcr.microsoft.com/mssql/server:2022-latest"
set "SQL_HOST=127.0.0.1"
set "SQL_PORT=14333"
set "SQL_DATABASE_NAME=GameControllerFBServiceExt"
set "SQL_SA_PASSWORD=FbServiceExt_Strong_2026!"
set "SQL_MASTER_CONNECTION_STRING=Server=%SQL_HOST%,%SQL_PORT%;Database=master;User ID=sa;Password=%SQL_SA_PASSWORD%;Encrypt=True;TrustServerCertificate=True;"
set "SQL_CONNECTION_STRING=Server=%SQL_HOST%,%SQL_PORT%;Database=%SQL_DATABASE_NAME%;User ID=sa;Password=%SQL_SA_PASSWORD%;Encrypt=True;TrustServerCertificate=True;"
set "RABBITMQ_API=http://127.0.0.1:15672/api"
set "RABBITMQ_USER=fbserviceext"
set "RABBITMQ_PASSWORD=DevOnly_RabbitMq2026!"
set "RAW_QUEUE_NAME=fbserviceext.raw.ingress"
set "NORMALIZED_QUEUE_NAME=fbserviceext.normalized.events"
set "REDIS_CONTAINER_NAME=fbserviceext-redis"
set "REDIS_KEY_PREFIX=fbserviceext"
set "APPDATA=%SOLUTION_ROOT%\.appdata"
set "LOCALAPPDATA=%SOLUTION_ROOT%\.localappdata"
set "DOTNET_CLI_HOME=%SOLUTION_ROOT%\.dotnet"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_NOLOGO=1"

if not exist "%APPDATA%" mkdir "%APPDATA%" >nul 2>&1
if not exist "%LOCALAPPDATA%" mkdir "%LOCALAPPDATA%" >nul 2>&1
if not exist "%DOTNET_CLI_HOME%" mkdir "%DOTNET_CLI_HOME%" >nul 2>&1

echo.
echo ==============================================
echo FBServiceExt Fake Facebook Voting Cycle Runner
echo ==============================================
echo   Environment          : %HOST_ENVIRONMENT%
echo   Fake FB users/shard  : %FAKE_FB_USERS%
echo   Shard count          : %SHARD_COUNT%
echo   Total fake FB users  : %TOTAL_FAKE_FB_USERS%
echo   Duration             : %DURATION%
echo   Cooldown seconds     : %COOLDOWN_SECONDS%
echo   Outbound wait        : %OUTBOUND_WAIT_SECONDS%s
echo   Startup jitter       : %STARTUP_JITTER_SECONDS%s
echo   Cycle p95 target     : %CYCLE_P95_MS% ms
echo   Worker instances     : %WORKER_INSTANCES%
echo   Message text         : %MESSAGE_TEXT%
echo   K6 artifacts         : %K6_ARTIFACT_PREFIX%
echo   Monitor prefix       : %MONITOR_ARTIFACT_PREFIX%
echo   Metrics UI           : http://localhost:5277/dev/metrics
echo   Fake Meta API        : %FAKE_META_BASE_URL%
echo   RabbitMQ UI          : http://127.0.0.1:15672
echo.

echo [1/8] Stopping existing FBServiceExt processes...
taskkill /F /IM GameController.FBServiceExt.exe /T >nul 2>&1
taskkill /F /IM GameController.FBServiceExt.Worker.exe /T >nul 2>&1

echo [2/8] Purging RabbitMQ perf queues...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$pair='%RABBITMQ_USER%:%RABBITMQ_PASSWORD%'; $bytes=[System.Text.Encoding]::ASCII.GetBytes($pair); $auth=[Convert]::ToBase64String($bytes); $headers=@{Authorization='Basic ' + $auth}; Invoke-RestMethod -Method Delete -Headers $headers -Uri '%RABBITMQ_API%/queues/%%2F/%RAW_QUEUE_NAME%/contents'; Invoke-RestMethod -Method Delete -Headers $headers -Uri '%RABBITMQ_API%/queues/%%2F/%NORMALIZED_QUEUE_NAME%/contents'"
if errorlevel 1 goto :queue_failed

echo [3/8] Ensuring local SQL container is running...
docker inspect "%SQL_CONTAINER_NAME%" >nul 2>&1
if errorlevel 1 (
  echo   Creating %SQL_CONTAINER_NAME% from %SQL_IMAGE%...
  docker run -d --name "%SQL_CONTAINER_NAME%" -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=%SQL_SA_PASSWORD%" -p %SQL_PORT%:1433 "%SQL_IMAGE%"
  if errorlevel 1 goto :sql_failed
) else (
  set "SQL_RUNNING="
  for /f %%s in ('docker inspect -f "{{.State.Running}}" "%SQL_CONTAINER_NAME%"') do set "SQL_RUNNING=%%s"
  if /I not "%SQL_RUNNING%"=="true" (
    echo   Starting existing %SQL_CONTAINER_NAME%...
    docker start "%SQL_CONTAINER_NAME%"
    if errorlevel 1 goto :sql_failed
  )
)

echo Waiting for SQL readiness...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$deadline=(Get-Date).AddMinutes(2); $connectionString='%SQL_MASTER_CONNECTION_STRING%'; while((Get-Date) -lt $deadline){ try { $conn = New-Object System.Data.SqlClient.SqlConnection $connectionString; $conn.Open(); $cmd = $conn.CreateCommand(); $cmd.CommandText = 'SELECT 1'; [void]$cmd.ExecuteScalar(); $conn.Close(); exit 0 } catch { Start-Sleep -Seconds 2 } }; exit 1"
if errorlevel 1 goto :sql_failed

echo Ensuring %SQL_DATABASE_NAME% exists...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$conn = New-Object System.Data.SqlClient.SqlConnection '%SQL_MASTER_CONNECTION_STRING%'; $conn.Open(); $cmd = $conn.CreateCommand(); $cmd.CommandText = \"IF DB_ID(N'%SQL_DATABASE_NAME%') IS NULL CREATE DATABASE [%SQL_DATABASE_NAME%]\"; [void]$cmd.ExecuteNonQuery(); $conn.Close();"
if errorlevel 1 goto :sql_failed

echo [4/8] Resetting fake-FB SQL and Redis state...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\perf\reset-fake-fb-state.ps1" ^
  -SqlContainerName "%SQL_CONTAINER_NAME%" ^
  -SqlDatabaseName "%SQL_DATABASE_NAME%" ^
  -SqlSaPassword "%SQL_SA_PASSWORD%" ^
  -RedisContainerName "%REDIS_CONTAINER_NAME%" ^
  -RedisKeyPrefix "%REDIS_KEY_PREFIX%"
if errorlevel 1 goto :state_reset_failed

echo [5/8] Building Release solution...
dotnet build ".\GameController.FBServiceExt.sln" -c Release -m:1 -nr:false -p:UseSharedCompilation=false
if errorlevel 1 goto :build_failed

if not exist "%API_EXE%" goto :api_missing
if not exist "%WORKER_EXE%" goto :worker_missing

echo [6/8] Starting API in %HOST_ENVIRONMENT% environment...
start "FBServiceExt API (%HOST_ENVIRONMENT%)" powershell.exe -NoExit -ExecutionPolicy Bypass -Command ^
  "Set-Location '%SOLUTION_ROOT%\src\GameController.FBServiceExt\bin\Release\net8.0'; $env:DOTNET_ENVIRONMENT='%HOST_ENVIRONMENT%'; $env:ASPNETCORE_ENVIRONMENT='%HOST_ENVIRONMENT%'; $env:APPDATA='%APPDATA%'; $env:LOCALAPPDATA='%LOCALAPPDATA%'; $env:DOTNET_CLI_HOME='%DOTNET_CLI_HOME%'; .\GameController.FBServiceExt.exe --urls http://127.0.0.1:5277 --contentRoot '%API_CONTENT_ROOT%'"

echo [7/8] Starting %WORKER_INSTANCES% Worker instance(s) in %HOST_ENVIRONMENT% environment...
for /L %%I in (1,1,%WORKER_INSTANCES%) do (
  start "FBServiceExt Worker %%I (%HOST_ENVIRONMENT%)" powershell.exe -NoExit -ExecutionPolicy Bypass -Command ^
    "Set-Location '%SOLUTION_ROOT%\src\GameController.FBServiceExt.Worker\bin\Release\net8.0'; $env:DOTNET_ENVIRONMENT='%HOST_ENVIRONMENT%'; $env:ASPNETCORE_ENVIRONMENT='%HOST_ENVIRONMENT%'; $env:APPDATA='%APPDATA%'; $env:LOCALAPPDATA='%LOCALAPPDATA%'; $env:DOTNET_CLI_HOME='%DOTNET_CLI_HOME%'; $env:SqlStorage__ConnectionString='%SQL_CONNECTION_STRING%'; .\GameController.FBServiceExt.Worker.exe"
)
echo Waiting for API health endpoint...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$deadline=(Get-Date).AddSeconds(90); while((Get-Date) -lt $deadline){ try { $response = Invoke-WebRequest '%BASE_URL%/health/ready' -UseBasicParsing -TimeoutSec 3; if($response.StatusCode -eq 200){ exit 0 } } catch {} Start-Sleep -Seconds 2 }; exit 1"
if errorlevel 1 goto :health_failed

echo [8/8] Running monitored fake Facebook voting-cycle test...
echo.
if "%SHARD_COUNT%"=="1" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\perf\run-k6-fake-fb-voting-cycle-monitored.ps1" ^
    -BaseUrl "%BASE_URL%" ^
    -FakeMetaBaseUrl "%FAKE_META_BASE_URL%" ^
    -FakeFbUsers %FAKE_FB_USERS% ^
    -Duration "%DURATION%" ^
    -CooldownSeconds %COOLDOWN_SECONDS% ^
    -OutboundWaitSeconds %OUTBOUND_WAIT_SECONDS% ^
    -StartupJitterSeconds %STARTUP_JITTER_SECONDS% ^
    -CycleP95Ms %CYCLE_P95_MS% ^
    -MessageText "%MESSAGE_TEXT%" ^
    -PageId "%PAGE_ID%" ^
    -SqlConnectionString "%SQL_CONNECTION_STRING%" ^
    -K6ArtifactPrefix "%K6_ARTIFACT_PREFIX%" ^
    -ArtifactPrefix "%MONITOR_ARTIFACT_PREFIX%"
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\perf\run-k6-fake-fb-voting-cycle-sharded-monitored.ps1" ^
    -BaseUrl "%BASE_URL%" ^
    -FakeMetaBaseUrl "%FAKE_META_BASE_URL%" ^
    -FakeFbUsersPerShard %FAKE_FB_USERS% ^
    -ShardCount %SHARD_COUNT% ^
    -Duration "%DURATION%" ^
    -CooldownSeconds %COOLDOWN_SECONDS% ^
    -OutboundWaitSeconds %OUTBOUND_WAIT_SECONDS% ^
    -StartupJitterSeconds %STARTUP_JITTER_SECONDS% ^
    -CycleP95Ms %CYCLE_P95_MS% ^
    -MessageText "%MESSAGE_TEXT%" ^
    -PageId "%PAGE_ID%" ^
    -SqlConnectionString "%SQL_CONNECTION_STRING%" ^
    -K6ArtifactPrefix "%K6_ARTIFACT_PREFIX%" ^
    -ArtifactPrefix "%MONITOR_ARTIFACT_PREFIX%"
)
set "EXIT_CODE=%ERRORLEVEL%"

echo.
echo [post] Querying SQL counts for this isolated run...
docker exec %SQL_CONTAINER_NAME% /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "%SQL_SA_PASSWORD%" -C -d %SQL_DATABASE_NAME% -Q "SET NOCOUNT ON; SELECT COUNT(*) AS NormalizedEventsCount FROM dbo.NormalizedEvents; SELECT COUNT(*) AS AcceptedVotesCount FROM dbo.AcceptedVotes;"
if errorlevel 1 (
  echo   SQL count summary could not be read.
)

echo.
echo Metrics dashboard: http://localhost:5277/dev/metrics
echo Fake Meta API: %FAKE_META_BASE_URL%
echo RabbitMQ dashboard: http://127.0.0.1:15672
echo.

popd >nul
exit /b %EXIT_CODE%

:queue_failed
echo.
echo RabbitMQ queue purge failed.
echo Check the management API and queue permissions.
popd >nul
exit /b 1

:sql_failed
echo.
echo SQL container did not become ready.
echo Check Docker availability and the %SQL_CONTAINER_NAME% container state.
popd >nul
exit /b 1

:state_reset_failed
echo.
echo Fake-FB SQL or Redis state reset failed.
echo Check the SQL container, Redis container, and cleanup commands.
popd >nul
exit /b 1

:build_failed
echo.
echo Release build failed.
popd >nul
exit /b 1

:api_missing
echo.
echo API executable was not found:
echo   %API_EXE%
popd >nul
exit /b 1

:worker_missing
echo.
echo Worker executable was not found:
echo   %WORKER_EXE%
popd >nul
exit /b 1

:health_failed
echo.
echo API health check did not become ready in time.
echo Check the opened API/Worker windows for startup errors.
echo Metrics dashboard: http://localhost:5277/dev/metrics
popd >nul
exit /b 1



