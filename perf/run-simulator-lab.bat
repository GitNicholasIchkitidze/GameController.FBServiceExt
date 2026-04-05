@echo off
setlocal EnableExtensions

set "SOLUTION_ROOT=%~dp0.."
pushd "%SOLUTION_ROOT%" >nul

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
echo FBServiceExt Simulator Lab Starter
echo ==============================================
echo   API env          : Simulator
echo   Worker env       : Simulator
echo   API URL          : http://127.0.0.1:5277
echo   Metrics UI       : http://127.0.0.1:5277/dev/metrics
echo   Logs UI          : http://127.0.0.1:5277/dev/logs
echo   Fake callback    : http://127.0.0.1:5290
echo   Worker cooldown  : 60 sec
echo   Confirm timeout  : 30 sec
echo.

echo [1/4] Stopping existing API/Worker processes...
taskkill /F /IM GameController.FBServiceExt.exe /T >nul 2>&1
taskkill /F /IM GameController.FBServiceExt.Worker.exe /T >nul 2>&1

echo [2/4] Building Release solution...
dotnet build ".\GameController.FBServiceExt.sln" -c Release -m:1 -nr:false -p:UseSharedCompilation=false
if errorlevel 1 goto :build_failed

echo [3/4] Starting API in Simulator environment...
start "FBServiceExt API (Simulator)" powershell.exe -NoExit -ExecutionPolicy Bypass -Command ^
  "Set-Location '%SOLUTION_ROOT%\src\GameController.FBServiceExt\bin\Release\net8.0'; $env:DOTNET_ENVIRONMENT='Simulator'; $env:ASPNETCORE_ENVIRONMENT='Simulator'; $env:APPDATA='%APPDATA%'; $env:LOCALAPPDATA='%LOCALAPPDATA%'; $env:DOTNET_CLI_HOME='%DOTNET_CLI_HOME%'; .\GameController.FBServiceExt.exe --urls http://127.0.0.1:5277 --contentRoot '%SOLUTION_ROOT%\src\GameController.FBServiceExt'"

echo [4/4] Starting Worker in Simulator environment...
start "FBServiceExt Worker (Simulator)" powershell.exe -NoExit -ExecutionPolicy Bypass -Command ^
  "Set-Location '%SOLUTION_ROOT%\src\GameController.FBServiceExt.Worker\bin\Release\net8.0'; $env:DOTNET_ENVIRONMENT='Simulator'; $env:ASPNETCORE_ENVIRONMENT='Simulator'; $env:APPDATA='%APPDATA%'; $env:LOCALAPPDATA='%LOCALAPPDATA%'; $env:DOTNET_CLI_HOME='%DOTNET_CLI_HOME%'; .\GameController.FBServiceExt.Worker.exe"

echo Waiting for API health endpoint...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$deadline=(Get-Date).AddSeconds(90); while((Get-Date) -lt $deadline){ try { $response = Invoke-WebRequest 'http://127.0.0.1:5277/health/ready' -UseBasicParsing -TimeoutSec 3; if($response.StatusCode -eq 200){ exit 0 } } catch {} Start-Sleep -Seconds 2 }; exit 1"
if errorlevel 1 goto :health_failed

echo.
echo Simulator lab hosts are ready.
echo Start WinForms FakeFBForSimulate and keep these UI values:
echo   Webhook URL      = http://127.0.0.1:5277/api/facebook/webhooks
echo   Listener URL     = http://127.0.0.1:5290
echo   Cooldown (sec)   = 60
echo   Outbound wait    = 15
echo   Start token      = GET_STARTED
echo.
echo Metrics: http://127.0.0.1:5277/dev/metrics
echo Logs   : http://127.0.0.1:5277/dev/logs
echo.
popd >nul
exit /b 0

:build_failed
echo.
echo Release build failed.
popd >nul
exit /b 1

:health_failed
echo.
echo API health check did not become ready in time.
echo Check the opened API/Worker windows for startup errors.
popd >nul
exit /b 1
