@echo off
rem Makes Machitan's installers, in publish\:
rem   Machitan-Setup-<version>-x64-Full.exe       x64: everything Machitan needs, the .NET 10 runtime included
rem   Machitan-Setup-<version>-x64-Trimmed.exe    x64: the same without the .NET 10 runtime
rem                                               (the PC needs the .NET 10 Runtime, x64)
rem   Machitan-Setup-<version>-ARM64-Full.exe     the same two for Windows on Arm PCs
rem   Machitan-Setup-<version>-ARM64-Trimmed.exe  (the Trimmed one needs the .NET 10 Runtime, Arm64)
rem Each one is a Release build published to its own folder in publish\ (Machitan-x64-Full,
rem Machitan-x64-Trimmed, Machitan-ARM64-Full, Machitan-ARM64-Trimmed), then compiled from
rem installer\Machitan.iss with Inno Setup.
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
rem (Machitan-Full and Machitan-Trimmed are the x64 folders' names before 0.9.3.)
if exist "publish\Machitan-Full" rmdir /s /q "publish\Machitan-Full"
if exist "publish\Machitan-Trimmed" rmdir /s /q "publish\Machitan-Trimmed"
if exist "publish\Machitan-x64-Full" rmdir /s /q "publish\Machitan-x64-Full"
if exist "publish\Machitan-x64-Trimmed" rmdir /s /q "publish\Machitan-x64-Trimmed"
if exist "publish\Machitan-ARM64-Full" rmdir /s /q "publish\Machitan-ARM64-Full"
if exist "publish\Machitan-ARM64-Trimmed" rmdir /s /q "publish\Machitan-ARM64-Trimmed"
if exist "publish\Machitan-Setup-*.exe" del /q "publish\Machitan-Setup-*.exe"

echo === 1/8: publishing the x64 Full edition (with .NET 10) to publish\Machitan-x64-Full
dotnet publish "Machitan.csproj" -c Release -r win-x64 --self-contained true -p:Platform=x64 -o "publish\Machitan-x64-Full"
if errorlevel 1 goto buildfailed

echo === 2/8: publishing the x64 Trimmed edition (without .NET 10) to publish\Machitan-x64-Trimmed
dotnet publish "Machitan.csproj" -c Release -r win-x64 --self-contained false -p:Platform=x64 -o "publish\Machitan-x64-Trimmed"
if errorlevel 1 goto buildfailed

echo === 3/8: publishing the ARM64 Full edition (with .NET 10) to publish\Machitan-ARM64-Full
dotnet publish "Machitan.csproj" -c Release -r win-arm64 --self-contained true -p:Platform=ARM64 -o "publish\Machitan-ARM64-Full"
if errorlevel 1 goto buildfailed

echo === 4/8: publishing the ARM64 Trimmed edition (without .NET 10) to publish\Machitan-ARM64-Trimmed
dotnet publish "Machitan.csproj" -c Release -r win-arm64 --self-contained false -p:Platform=ARM64 -o "publish\Machitan-ARM64-Trimmed"
if errorlevel 1 goto buildfailed

echo === 5/8: making the x64 Full installer with "%ISCC%"
"%ISCC%" /DEdition=Full /DArch=x64 "installer\Machitan.iss"
if errorlevel 1 goto setupfailed

echo === 6/8: making the x64 Trimmed installer
"%ISCC%" /DEdition=Trimmed /DArch=x64 "installer\Machitan.iss"
if errorlevel 1 goto setupfailed

echo === 7/8: making the ARM64 Full installer
"%ISCC%" /DEdition=Full /DArch=ARM64 "installer\Machitan.iss"
if errorlevel 1 goto setupfailed

echo === 8/8: making the ARM64 Trimmed installer
"%ISCC%" /DEdition=Trimmed /DArch=ARM64 "installer\Machitan.iss"
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
