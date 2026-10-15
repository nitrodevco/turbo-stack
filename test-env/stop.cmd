@echo off
rem Double-click to stop and remove the Docker test server (same as "Delete" on ts-test in Docker Desktop).
setlocal
set "GITBASH=%ProgramFiles%\Git\bin\bash.exe"
"%GITBASH%" -lc "cd \"$(cygpath '%~dp0')\" && ./dev down"
echo Test server stopped.
pause
