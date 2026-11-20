@echo off
rem Makes Machitan's two installers, in publish\:
rem   Machitan-Setup-<version>-Full.exe     everything Machitan needs, the .NET 10 runtime included
rem   Machitan-Setup-<version>-Trimmed.exe  the same without the .NET 10 runtime
rem                                         (the PC needs the .NET 10 Runtime, x64)
rem Each edition is a Release build (x64) published to publish\Machitan-Full or
rem publish\Machitan-Trimmed, then compiled from installer\Machitan.iss with Inno Setup.
rem Needs: the .NET 10 SDK (comes with Visual Studio) and Inno Setup 7 (or 6.3 and later),
rem installed once:
rem   winget install --id JRSoftware.InnoSetup.7 -e
setlocal
cd /d "%~dp0"

set "ISCC="
for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do if not defined ISCC set "ISCC=%%I"
call :find "%ProgramFiles%\Inno Setup 7\ISCC.exe"
call :find "%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"
call :find "%LocalAppData%\Programs\Inno Setup 7\ISCC.exe"
call :find "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
call :find "%ProgramFiles%\Inno Setup 6\ISCC.exe"
call :find "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC (
    echo Inno Setup was not found. Install it once, then run this again:
    echo     winget install --id JRSoftware.InnoSetup.7 -e
    goto failed
)

rem Older results go first, so the publish folder holds only this run's.
if exist "publish\Machitan" rmdir /s /q "publish\Machitan"
if exist "publish\Machitan-Full" rmdir /s /q "publish\Machitan-Full"
if exist "publish\Machitan-Trimmed" rmdir /s /q "publish\Machitan-Trimmed"
if exist "publish\Machitan-Setup-*.exe" del /q "publish\Machitan-Setup-*.exe"

echo === 1/4: publishing the Full edition (with .NET 10) to publish\Machitan-Full
dotnet publish "Machitan.csproj" -c Release -r win-x64 --self-contained true -p:Platform=x64 -o "publish\Machitan-Full"
if errorlevel 1 goto buildfailed

echo === 2/4: publishing the Trimmed edition (without .NET 10) to publish\Machitan-Trimmed
dotnet publish "Machitan.csproj" -c Release -r win-x64 --self-contained false -p:Platform=x64 -o "publish\Machitan-Trimmed"
if errorlevel 1 goto buildfailed

echo === 3/4: making the Full installer with "%ISCC%"
"%ISCC%" /DEdition=Full "installer\Machitan.iss"
if errorlevel 1 goto setupfailed

echo === 4/4: making the Trimmed installer
"%ISCC%" /DEdition=Trimmed "installer\Machitan.iss"
if errorlevel 1 goto setupfailed

echo.
echo Done. The installers are in the publish folder:
dir /b "publish\Machitan-Setup-*.exe"
start "" "%~dp0publish"
pause
exit /b 0

:find
if not defined ISCC if exist "%~1" set "ISCC=%~1"
exit /b 0

:buildfailed
echo The Release build failed; see the messages above.
goto failed

:setupfailed
echo Inno Setup could not make the installer; see the messages above.
goto failed

:failed
pause
exit /b 1
