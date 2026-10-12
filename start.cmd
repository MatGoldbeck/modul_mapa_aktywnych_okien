@echo off
rem Uruchomienie prototypu: buduje backend (.NET 9), instaluje frontend (Electron) przy pierwszym
rem starcie i uruchamia aplikację w tle (ikona w zasobniku). Okno konsoli można potem zamknąć.
setlocal
cd /d "%~dp0"

where dotnet >nul 2>&1 || (echo Brak .NET 9 SDK. Zainstaluj: winget install Microsoft.DotNet.SDK.9 & pause & exit /b 1)
where npm.cmd >nul 2>&1 || (echo Brak Node.js. Zainstaluj: winget install OpenJS.NodeJS.LTS ^(potem nowy terminal^) & pause & exit /b 1)

rem Działający backend blokuje swój plik .exe – wtedy nie budujemy, tylko pokazujemy działającą aplikację.
tasklist /FI "IMAGENAME eq MapaOkien.Backend.exe" 2>nul | find /I "MapaOkien.Backend.exe" >nul
if %errorlevel%==0 (
  echo Aplikacja juz dziala - pokazuje okno mapy.
  goto run
)

echo [1/3] Budowanie backendu...
dotnet build backend -c Release --nologo -v q
if errorlevel 1 (echo Budowanie backendu nie powiodlo sie. & pause & exit /b 1)

if not exist frontend\node_modules\electron\dist\electron.exe (
  echo [2/3] Instalacja frontendu ^(pierwsze uruchomienie^)...
  pushd frontend
  call npm.cmd ci
  if errorlevel 1 (popd & echo Instalacja frontendu nie powiodla sie. & pause & exit /b 1)
  popd
)

:run
echo [3/3] Start. Aplikacja dziala w tle - ikona w zasobniku, gest: Ctrl+Shift + ruch myszy.
set "ELECTRON_RUN_AS_NODE="
start "" "%~dp0frontend\node_modules\electron\dist\electron.exe" "%~dp0frontend"
endlocal
