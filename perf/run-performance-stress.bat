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

set "BASE_URL=http://127.0.0.1:5277"
set "API_EXE=%SOLUTION_ROOT%\src\GameController.FBServiceExt\bin\Release\net8.0\GameController.FBServiceExt.exe"
set "API_CONTENT_ROOT=%SOLUTION_ROOT%\src\GameController.FBServiceExt"
set "WORKER_EXE=%SOLUTION_ROOT%\src\GameController.FBServiceExt.Worker\bin\Release\net8.0\GameController.FBServiceExt.Worker.exe"
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
echo   Messages/sec   : %TARGET_MESSAGES_PER_SECOND%
echo   Duration       : %DURATION%
echo   Events/request : %EVENTS_PER_REQUEST%
echo   ACK p95 target : %ACK_P95_MS% ms
echo   ACK p99 target : %ACK_P99_MS% ms
echo   Metrics UI     : http://localhost:5277/dev/metrics
echo   RabbitMQ UI    : http://127.0.0.1:15672
echo.

echo [1/5] Stopping existing FBServiceExt processes...
taskkill /F /IM GameController.FBServiceExt.exe /T >nul 2>&1
taskkill /F /IM GameController.FBServiceExt.Worker.exe /T >nul 2>&1

echo [2/5] Building Release solution...
dotnet build ".\GameController.FBServiceExt.sln" -c Release -m:1 -nr:false -p:UseSharedCompilation=false
if errorlevel 1 goto :build_failed

if not exist "%API_EXE%" goto :api_missing
if not exist "%WORKER_EXE%" goto :worker_missing

echo [3/5] Starting API in Performance environment...
start "FBServiceExt API (Performance)" powershell.exe -NoExit -ExecutionPolicy Bypass -Command ^
  "Set-Location '%SOLUTION_ROOT%\src\GameController.FBServiceExt\bin\Release\net8.0'; $env:DOTNET_ENVIRONMENT='Performance'; $env:ASPNETCORE_ENVIRONMENT='Performance'; $env:APPDATA='%APPDATA%'; $env:LOCALAPPDATA='%LOCALAPPDATA%'; $env:DOTNET_CLI_HOME='%DOTNET_CLI_HOME%'; .\GameController.FBServiceExt.exe --urls http://127.0.0.1:5277 --contentRoot '%API_CONTENT_ROOT%'"

echo [4/5] Starting Worker in Performance environment...
start "FBServiceExt Worker (Performance)" powershell.exe -NoExit -ExecutionPolicy Bypass -Command ^
  "Set-Location '%SOLUTION_ROOT%\src\GameController.FBServiceExt.Worker\bin\Release\net8.0'; $env:DOTNET_ENVIRONMENT='Performance'; $env:ASPNETCORE_ENVIRONMENT='Performance'; $env:APPDATA='%APPDATA%'; $env:LOCALAPPDATA='%LOCALAPPDATA%'; $env:DOTNET_CLI_HOME='%DOTNET_CLI_HOME%'; .\GameController.FBServiceExt.Worker.exe"

echo Waiting for API health endpoint...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$deadline=(Get-Date).AddSeconds(90); while((Get-Date) -lt $deadline){ try { $response = Invoke-WebRequest '%BASE_URL%/health/ready' -UseBasicParsing -TimeoutSec 3; if($response.StatusCode -eq 200){ exit 0 } } catch {} Start-Sleep -Seconds 2 }; exit 1"
if errorlevel 1 goto :health_failed

echo [5/5] Running monitored stress test...
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\perf\run-k6-ack-stress-monitored.ps1" ^
  -BaseUrl "%BASE_URL%" ^
  -TargetMessagesPerSecond %TARGET_MESSAGES_PER_SECOND% ^
  -EventsPerRequest %EVENTS_PER_REQUEST% ^
  -Duration "%DURATION%" ^
  -AckP95Ms %ACK_P95_MS% ^
  -AckP99Ms %ACK_P99_MS%
set "EXIT_CODE=%ERRORLEVEL%"

echo.
echo Metrics dashboard: http://localhost:5277/dev/metrics
echo RabbitMQ dashboard: http://127.0.0.1:15672
echo.

popd >nul
exit /b %EXIT_CODE%

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

