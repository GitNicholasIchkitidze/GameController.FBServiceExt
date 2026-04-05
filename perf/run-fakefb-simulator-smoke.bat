@echo off
setlocal

set ROOT=%~dp0..
for %%I in ("%ROOT%") do set ROOT=%%~fI
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
set DOTNET_CLI_HOME=%ROOT%\.dotnet-cli
if not exist "%DOTNET_CLI_HOME%" mkdir "%DOTNET_CLI_HOME%" >nul 2>&1

set ARTIFACT_ROOT=%ROOT%\artifacts\simulator-smoke
if not exist "%ARTIFACT_ROOT%" mkdir "%ARTIFACT_ROOT%" >nul 2>&1

for /f %%I in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set RUNSTAMP=%%I
set RUN_DIR=%ARTIFACT_ROOT%\run-%RUNSTAMP%
mkdir "%RUN_DIR%" >nul 2>&1

set API_EXE=%ROOT%\src\GameController.FBServiceExt\bin\Release\net8.0\GameController.FBServiceExt.exe
set WORKER_EXE=%ROOT%\src\GameController.FBServiceExt.Worker\bin\Release\net8.0\GameController.FBServiceExt.Worker.exe
set SIM_EXE=%ROOT%\src\GameController.FBServiceExt.FakeFBForSimulate\bin\Release\net8.0-windows\GameController.FBServiceExt.FakeFBForSimulate.exe
set API_CONTENT_ROOT=%ROOT%\src\GameController.FBServiceExt

set USERS=%1
if "%USERS%"=="" set USERS=5
set DURATION=%2
if "%DURATION%"=="" set DURATION=90

set API_LOG=%RUN_DIR%\api.log
set WORKER_LOG=%RUN_DIR%\worker.log
set SIM_LOG=%RUN_DIR%\simulator.log

echo ==========================================
echo FakeFB Simulator Smoke Runner
echo ==========================================
echo Users        : %USERS%
echo Duration sec : %DURATION%
echo Run dir      : %RUN_DIR%
echo.

taskkill /F /IM GameController.FBServiceExt.exe /T >nul 2>&1
taskkill /F /IM GameController.FBServiceExt.Worker.exe /T >nul 2>&1
taskkill /F /IM GameController.FBServiceExt.FakeFBForSimulate.exe /T >nul 2>&1

dotnet build "%ROOT%\GameController.FBServiceExt.sln" -c Release
if errorlevel 1 exit /b 1

powershell -NoProfile -Command "$env:DOTNET_ENVIRONMENT='Simulator'; $env:ASPNETCORE_ENVIRONMENT='Simulator'; Start-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile','-Command', \"& '%API_EXE%' --urls 'http://127.0.0.1:5277' --contentRoot '%API_CONTENT_ROOT%' *>> '%API_LOG%'\""
powershell -NoProfile -Command "$env:DOTNET_ENVIRONMENT='Simulator'; $env:ASPNETCORE_ENVIRONMENT='Simulator'; Start-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile','-Command', \"& '%WORKER_EXE%' *>> '%WORKER_LOG%'\""

powershell -NoProfile -Command "$deadline=(Get-Date).AddSeconds(40); while((Get-Date) -lt $deadline){ try { $r=Invoke-WebRequest 'http://127.0.0.1:5277/health/ready' -UseBasicParsing -TimeoutSec 2; if($r.StatusCode -eq 200){ exit 0 } } catch {} Start-Sleep -Milliseconds 500 }; exit 1"
if errorlevel 1 (
  echo API health check failed.
  exit /b 1
)

powershell -NoProfile -Command "& '%SIM_EXE%' --headless --users %USERS% --duration-seconds %DURATION% --cooldown-seconds 60 --startup-jitter-seconds 0 --min-think-ms 200 --max-think-ms 600 --outbound-wait-seconds 15 *>&1 | Tee-Object '%SIM_LOG%'; exit $LASTEXITCODE"
set SIM_EXIT=%ERRORLEVEL%

echo.
echo Last API log lines:
powershell -NoProfile -Command "Get-Content '%API_LOG%' -Tail 40"
echo.
echo Last Worker log lines:
powershell -NoProfile -Command "Get-Content '%WORKER_LOG%' -Tail 60"

taskkill /F /IM GameController.FBServiceExt.exe /T >nul 2>&1
taskkill /F /IM GameController.FBServiceExt.Worker.exe /T >nul 2>&1

echo.
echo Artifacts:
echo   API log     : %API_LOG%
echo   Worker log  : %WORKER_LOG%
echo   Simulator   : %SIM_LOG%
exit /b %SIM_EXIT%
