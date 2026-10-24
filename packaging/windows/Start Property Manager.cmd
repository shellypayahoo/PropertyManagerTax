@echo off
rem Starts Property Manager and opens it in the browser.
rem The owner's data is kept in Documents\Property Manager, separate from the app, so the app folder can be
rem replaced with a newer version without touching it.
title Property Manager
cd /d "%~dp0app"

set "PM_DATA=%USERPROFILE%\Documents\Property Manager"
if not exist "%PM_DATA%" mkdir "%PM_DATA%"
set "ConnectionStrings__PropertyManager=Data Source=%PM_DATA%\propertymanager.db"
set "DocumentStorage__LocalPath=%PM_DATA%\documents"
set "ASPNETCORE_URLS=http://localhost:5220"

rem Already running? Just open it.
powershell -NoProfile -Command "try { (Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 http://localhost:5220/) | Out-Null; exit 0 } catch { exit 1 }"
if %errorlevel%==0 (
    start "" http://localhost:5220
    exit /b
)

echo Starting Property Manager...
echo Keep this window open while you use the app. Close it to stop Property Manager.
echo Your data is in %PM_DATA%
start "" /b PropertyManager.Web.exe

rem Open the browser once the app answers (up to about a minute).
powershell -NoProfile -Command "for ($i = 0; $i -lt 60; $i++) { try { (Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 http://localhost:5220/) | Out-Null; Start-Process 'http://localhost:5220'; exit 0 } catch { Start-Sleep -Seconds 1 } }; Write-Host 'Property Manager did not start. Check the messages above.'"
rem Keep the window open with the app's messages until the owner closes it.
:wait
timeout /t 3600 /nobreak >nul
goto wait
