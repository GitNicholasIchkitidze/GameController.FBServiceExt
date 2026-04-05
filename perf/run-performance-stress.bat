@echo off
setlocal EnableExtensions

set "SOLUTION_ROOT=%~dp0.."
pushd "%SOLUTION_ROOT%" >nul

set "TARGET_MESSAGES_PER_SECOND=%~1"
if "%TARGET_MESSAGES_PER_SECOND%"=="" set "TARGET_MESSAGES_PER_SECOND=400"

set "DURATION=%~2"
if "%DURATION%"=="" set "DURATION=300s"

set "EVENTS_PER_REQUEST=%~3"
if "%EVENTS_PER_REQUEST%"=="" set "EVENTS_PER_REQUEST=1"

set "ACK_P95_MS=%~4"
if "%ACK_P95_MS%"=="" set "ACK_P95_MS=300"

set "ACK_P99_MS=%~5"
if "%ACK_P99_MS%"=="" set "ACK_P99_MS=600"

set "HOST_ENVIRONMENT=%~6"
if "%HOST_ENVIRONMENT%"=="" set "HOST_ENVIRONMENT=Performance"

set "MESSAGE_TEXT=%~7"
if "%MESSAGE_TEXT%"=="" set "MESSAGE_TEXT=GET_STARTED"

set "K6_ARTIFACT_PREFIX=%~8"
if "%K6_ARTIFACT_PREFIX%"=="" set "K6_ARTIFACT_PREFIX=ack-250rps"

set "MONITOR_ARTIFACT_PREFIX=%~9"
if "%MONITOR_ARTIFACT_PREFIX%"=="" set "MONITOR_ARTIFACT_PREFIX=ack-stress-monitored"

set "BASE_URL=http://127.0.0.1:5277"
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
echo FBServiceExt Performance Stress Runner
echo ==============================================
echo   Environment    : %HOST_ENVIRONMENT%
echo   Messages/sec   : %TARGET_MESSAGES_PER_SECOND%
echo   Duration       : %DURATION%
echo   Events/request : %EVENTS_PER_REQUEST%
echo   Message text   : %MESSAGE_TEXT%
echo   ACK p95 target : %ACK_P95_MS% ms
echo   ACK p99 target : %ACK_P99_MS% ms
echo   SQL target     : %SQL_HOST%:%SQL_PORT% (%SQL_CONTAINER_NAME%)
echo   K6 artifacts   : %K6_ARTIFACT_PREFIX%
echo   Monitor prefix : %MONITOR_ARTIFACT_PREFIX%
echo   Metrics UI     : http://localhost:5277/dev/metrics
echo   RabbitMQ UI    : http://127.0.0.1:15672
echo.

echo [1/7] Stopping existing FBServiceExt processes...
taskkill /F /IM GameController.FBServiceExt.exe /T >nul 2>&1
taskkill /F /IM GameController.FBServiceExt.Worker.exe /T >nul 2>&1

echo [2/7] Purging RabbitMQ perf queues...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$pair='%RABBITMQ_USER%:%RABBITMQ_PASSWORD%'; $bytes=[System.Text.Encoding]::ASCII.GetBytes($pair); $auth=[Convert]::ToBase64String($bytes); $headers=@{Authorization='Basic ' + $auth}; Invoke-RestMethod -Method Delete -Headers $headers -Uri '%RABBITMQ_API%/queues/%%2F/%RAW_QUEUE_NAME%/contents'; Invoke-RestMethod -Method Delete -Headers $headers -Uri '%RABBITMQ_API%/queues/%%2F/%NORMALIZED_QUEUE_NAME%/contents'"
if errorlevel 1 goto :queue_failed

echo [3/7] Ensuring local SQL container is running...
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

echo [4/7] Building Release solution...
dotnet build ".\GameController.FBServiceExt.sln" -c Release -m:1 -nr:false -p:UseSharedCompilation=false
if errorlevel 1 goto :build_failed

if not exist "%API_EXE%" goto :api_missing
if not exist "%WORKER_EXE%" goto :worker_missing

echo [5/7] Starting API in %HOST_ENVIRONMENT% environment...
start "FBServiceExt API (%HOST_ENVIRONMENT%)" powershell.exe -NoExit -ExecutionPolicy Bypass -Command ^
  "Set-Location '%SOLUTION_ROOT%\src\GameController.FBServiceExt\bin\Release\net8.0'; $env:DOTNET_ENVIRONMENT='%HOST_ENVIRONMENT%'; $env:ASPNETCORE_ENVIRONMENT='%HOST_ENVIRONMENT%'; $env:APPDATA='%APPDATA%'; $env:LOCALAPPDATA='%LOCALAPPDATA%'; $env:DOTNET_CLI_HOME='%DOTNET_CLI_HOME%'; .\GameController.FBServiceExt.exe --urls http://127.0.0.1:5277 --contentRoot '%API_CONTENT_ROOT%'"

echo [6/7] Starting Worker in %HOST_ENVIRONMENT% environment...
start "FBServiceExt Worker (%HOST_ENVIRONMENT%)" powershell.exe -NoExit -ExecutionPolicy Bypass -Command ^
  "Set-Location '%SOLUTION_ROOT%\src\GameController.FBServiceExt.Worker\bin\Release\net8.0'; $env:DOTNET_ENVIRONMENT='%HOST_ENVIRONMENT%'; $env:ASPNETCORE_ENVIRONMENT='%HOST_ENVIRONMENT%'; $env:APPDATA='%APPDATA%'; $env:LOCALAPPDATA='%LOCALAPPDATA%'; $env:DOTNET_CLI_HOME='%DOTNET_CLI_HOME%'; $env:SqlStorage__ConnectionString='%SQL_CONNECTION_STRING%'; .\GameController.FBServiceExt.Worker.exe"

echo Waiting for API health endpoint...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$deadline=(Get-Date).AddSeconds(90); while((Get-Date) -lt $deadline){ try { $response = Invoke-WebRequest '%BASE_URL%/health/ready' -UseBasicParsing -TimeoutSec 3; if($response.StatusCode -eq 200){ exit 0 } } catch {} Start-Sleep -Seconds 2 }; exit 1"
if errorlevel 1 goto :health_failed

echo [7/7] Running monitored stress test...
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\perf\run-k6-ack-stress-monitored.ps1" ^
  -BaseUrl "%BASE_URL%" ^
  -TargetMessagesPerSecond %TARGET_MESSAGES_PER_SECOND% ^
  -EventsPerRequest %EVENTS_PER_REQUEST% ^
  -Duration "%DURATION%" ^
  -AckP95Ms %ACK_P95_MS% ^
  -AckP99Ms %ACK_P99_MS% ^
  -MessageText "%MESSAGE_TEXT%" ^
  -K6ArtifactPrefix "%K6_ARTIFACT_PREFIX%" ^
  -ArtifactPrefix "%MONITOR_ARTIFACT_PREFIX%"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
echo Metrics dashboard: http://localhost:5277/dev/metrics
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

:sql_failed
echo.
echo SQL container did not become ready.
echo Check Docker availability and the %SQL_CONTAINER_NAME% container state.
popd >nul
exit /b 1

:health_failed
echo.
echo API health check did not become ready in time.
echo Check the opened API/Worker windows for startup errors.
echo Metrics dashboard: http://localhost:5277/dev/metrics
popd >nul
exit /b 1
