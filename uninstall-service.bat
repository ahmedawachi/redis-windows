@echo off
rem Stops and removes the Redis service that install-service.bat created.
rem The data folder is kept. Double-click this file; it asks for administrator rights itself.
setlocal
cd /d "%~dp0"

fltmc >nul 2>&1
if errorlevel 1 (
    if "%~1"=="--elevated" (
        echo Administrator rights are still missing. Right-click this file and choose "Run as administrator".
        pause
        exit /b 1
    )
    echo Asking for administrator rights...
    set "RW_SELF=%~f0"
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath $env:RW_SELF -ArgumentList '--elevated' -Verb RunAs" || (
        echo Administrator rights were not granted, so nothing was changed.
        pause
    )
    exit /b
)

"%~dp0RedisService.exe" uninstall
if errorlevel 1 (
    echo.
    echo Removing the service failed; the reason is shown above.
) else (
    echo.
    echo The Redis service is removed. Your data in the "data" folder is still there.
)
pause
