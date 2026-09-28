@echo off
rem Runs Redis in this console window.
rem With RedisService.exe (the -with-Service package) Redis is supervised: it is restarted if it
rem crashes, and Ctrl+C stops it with SHUTDOWN, which saves if redis.conf has save points (Ctrl+C twice forces).
rem Without it, redis-server.exe runs directly; Ctrl+C makes it exit the same way.
cd /d "%~dp0"
if exist "%~dp0RedisService.exe" (
    "%~dp0RedisService.exe" run --foreground -c "%~dp0redis.conf" %*
) else (
    "%~dp0redis-server.exe" redis.conf %*
)
pause
