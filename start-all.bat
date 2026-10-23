@echo off
REM ============================================
REM  BcMes one-click startup (real-factory stack)
REM  Order: Docker infra -> Simulator -> Server -> Client
REM ============================================
cd /d "%~dp0"

echo [0/4] Checking Docker services (SQL Server / Redis / TDengine) ...
docker compose up -d
if errorlevel 1 (
  echo ERROR: Docker is not running. Please start Docker Desktop first.
  pause
  exit /b 1
)
timeout /t 8 >nul

echo [1/4] Starting production-line simulator on http://localhost:5101 ...
start "BcMes.Simulator" cmd /k "dotnet run --project src\BcMes.Simulator --no-build"
timeout /t 6 >nul

echo [2/4] Starting MES server on http://localhost:5100 ...
start "BcMes.Server" cmd /k "dotnet run --project src\BcMes.Server --no-build"
timeout /t 8 >nul

echo [3/4] Starting WPF workstation client ...
start "BcMes.Client" cmd /k "dotnet run --project src\BcMes.Client --no-build"

echo.
echo All processes launched.
echo   Simulator  : http://localhost:5101/api/status
echo   MES Server : http://localhost:5100/api/health
echo   TDengine   : http://localhost:6041  (REST, root/taosdata for CLI)
echo   SQL Server : localhost,1433  Redis : localhost:6380
echo   Client     : WPF window
echo.
echo Close this window whenever you like; the started windows keep running.
pause
