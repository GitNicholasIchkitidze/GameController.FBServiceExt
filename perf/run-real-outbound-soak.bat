@echo off
setlocal EnableExtensions

set "TARGET_MESSAGES_PER_SECOND=%~1"
if "%TARGET_MESSAGES_PER_SECOND%"=="" set "TARGET_MESSAGES_PER_SECOND=5"

set "DURATION=%~2"
if "%DURATION%"=="" set "DURATION=600s"

set "EVENTS_PER_REQUEST=%~3"
if "%EVENTS_PER_REQUEST%"=="" set "EVENTS_PER_REQUEST=1"

call "%~dp0run-performance-stress.bat" %TARGET_MESSAGES_PER_SECOND% %DURATION% %EVENTS_PER_REQUEST% 300 600 PerformanceRealOutbound GET_STARTED real-outbound-soak real-outbound-soak-monitor
exit /b %ERRORLEVEL%
