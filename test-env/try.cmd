@echo off
rem Double-click to build Turbo Stack, start a fresh Ubuntu test server in Docker Desktop,
rem install the panel on it and open the setup page in your browser.
rem Needs: Docker Desktop (running), Git for Windows, .NET 10 SDK and Node 24.
setlocal

set "GITBASH=%ProgramFiles%\Git\bin\bash.exe"
if not exist "%GITBASH%" (
    echo Git for Windows is required: https://git-scm.com/download/win
    pause
    exit /b 1
)

docker info >nul 2>&1
if errorlevel 1 (
    echo Docker Desktop is not running. Start it, wait until it says "Engine running", then try again.
    pause
    exit /b 1
)

echo Building and starting the test server. The first run takes a few minutes...
"%GITBASH%" -lc "cd \"$(cygpath '%~dp0')\" && ./dev try"
if errorlevel 1 (
    echo.
    echo Something went wrong; see the messages above.
)
pause
