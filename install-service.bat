@echo off
rem Installs Redis as a Windows service: it starts with Windows, runs in the background and is
rem restarted if it crashes. Double-click this file; it asks for administrator rights itself.
rem uninstall-service.bat removes the service again.
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

echo.
echo The Redis service will run from this folder:
echo     "%~dp0"
echo Its data and log go to the "data" folder inside it.
echo If this folder is in Downloads or on the desktop, move it somewhere permanent first
echo (for example C:\Redis) and run this file from there.
echo.
choice /C YN /M "Install the Redis service now"
if errorlevel 2 exit /b 1

if not exist "%~dp0data" mkdir "%~dp0data"
"%~dp0RedisService.exe" install -c "%~dp0redis.conf" --dir "%~dp0data" --logfile "%~dp0data\redis.log"
if errorlevel 1 (
    echo.
    sc query Redis >nul 2>&1
    if errorlevel 1 (
        echo Installing the service failed; the reason is shown above.
    ) else (
        echo The Redis service was installed but did not start; the reason is shown above.
        echo If a RedisService window is still open, close it: it holds the Redis port.
        echo Then start the service with:  sc start Redis
    )
    pause
    exit /b 1
)

echo.
echo Done. Redis runs as the "Redis" service and starts with Windows. It listens on the port
echo set in redis.conf (6379 unless you changed it). Connect with redis-cli.exe in this folder.
echo Check the service with:  sc query Redis
pause
