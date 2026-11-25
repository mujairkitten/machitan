; Machitan's installers (Inno Setup 7, or 6.3 and later), in two editions, each for x64 PCs
; and for Windows on Arm (ARM64) PCs:
;   Full     everything Machitan needs, the .NET 10 runtime included.
;   Trimmed  the same without the .NET 10 runtime: the PC needs the .NET 10 Runtime (x64, or
;            Arm64 for the ARM64 installer); Setup checks for it first and points to
;            Microsoft's download page if it is missing.
; All bring the Windows App SDK's parts Machitan uses (WinUI 3), not its AI parts.
;
; Made by build-installer.cmd in the project folder, which publishes a Release build per
; edition and kind of PC to publish\Machitan-Full, publish\Machitan-Trimmed,
; publish\Machitan-ARM64-Full and publish\Machitan-ARM64-Trimmed, then compiles this script
; once for each (/DEdition=Full or Trimmed, /DArch=x64 or ARM64). The installers land in
; publish\: Machitan-Setup-<version>-Full.exe and Machitan-Setup-<version>-Trimmed.exe (x64),
; Machitan-Setup-<version>-ARM64-Full.exe and Machitan-Setup-<version>-ARM64-Trimmed.exe, the
; version being Machitan.exe's own (<Version> in Machitan.csproj).
; The x64 installers also run on Windows on Arm (Windows runs x64 apps there); the ARM64 ones
; run only there, natively. All are the same app to Windows (one AppId): one replaces another. Windows stamps a program with four
; numbers (0.9.0 becomes 0.9.0.0); a last ".0" is left out of the file name and Windows' Apps
; list, so they say 0.9.0.
;
; What it does:
; - Installs for all users in C:\Program Files\Machitan (Windows asks for administrator
;   rights once). Program Files, not the user's own folders, because Settings > Shortcuts >
;   "Shortcuts in apps running as administrator" makes a scheduled task that starts this
;   Machitan.exe as administrator; only a folder that needs administrator rights to change
;   keeps that safe.
; - Both editions are the same app to Windows (one AppId): either one updates or replaces the
;   other. The files of the Machitan already there go first, so nothing is left from another
;   edition or an older version.
; - Adds Machitan to the Start menu (a desktop shortcut only if ticked).
; - A Machitan that is running is asked to exit cleanly first ("Machitan.exe --exit" does the
;   same), so Explorer's taskbar comes back; at the end Machitan can be started again.
; - Uninstall (Windows' Settings > Apps): Machitan is asked to exit, its files and shortcuts
;   go, its "Run at startup" entry goes if it names this copy, and the AdminKeys scheduled
;   task goes. Settings and pins in %AppData%\Machitan stay, for a later install.

#define AppName "Machitan"

#ifndef Edition
  #define Edition "Full"
#endif

#if Edition != "Full" && Edition != "Trimmed"
  #error Edition must be Full or Trimmed (build-installer.cmd passes /DEdition=Full or /DEdition=Trimmed).
#endif

#ifndef Arch
  #define Arch "x64"
#endif

#if Arch == "x64"
  #define PublishDir AddBackslash(SourcePath) + "..\publish\Machitan-" + Edition
  #define FileSuffix "-" + Edition
  #define DotNetArch "x64"
#elif Arch == "ARM64"
  #define PublishDir AddBackslash(SourcePath) + "..\publish\Machitan-ARM64-" + Edition
  #define FileSuffix "-ARM64-" + Edition
  #define DotNetArch "Arm64"
#else
  #error Arch must be x64 or ARM64 (build-installer.cmd passes /DArch=x64 or /DArch=ARM64).
#endif

#if !FileExists(PublishDir + "\Machitan.exe")
  #error Machitan.exe is not in this edition's publish folder. Run build-installer.cmd, which publishes it first.
#endif

#define FileVersion GetVersionNumbersString(PublishDir + "\Machitan.exe")
#if Copy(FileVersion, Len(FileVersion) - 1) == ".0"
  #define AppVersion Copy(FileVersion, 1, Len(FileVersion) - 2)
#else
  #define AppVersion FileVersion
#endif

[Setup]
AppId={{19095618-65AC-4465-A960-1A10F039367D}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
; Setup's own file version needs all four numbers.
VersionInfoVersion={#FileVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup ({#Arch}, {#Edition})
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
#if Arch == "ARM64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041
LicenseFile=..\LICENSE
SetupIconFile=..\machitan.ico
UninstallDisplayIcon={app}\Machitan.exe
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\publish
OutputBaseFilename={#AppName}-Setup-{#AppVersion}{#FileSuffix}
SetupLogging=yes
; Machitan is closed by the [Code] below; Windows' Restart Manager only covers what is left
; (the small helper processes), and nothing is restarted behind the user's back.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
ReadyLabel2a=If Machitan is running, Setup closes it first (Explorer's taskbar comes back meanwhile); you can start it again at the end.%n%nClick Install to continue with the installation, or click Back if you want to review or change any settings.
ReadyLabel2b=If Machitan is running, Setup closes it first (Explorer's taskbar comes back meanwhile); you can start it again at the end.%n%nClick Install to continue with the installation.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Machitan.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Machitan.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; As the user who started Setup, never as administrator: the shell must run with normal rights.
Filename: "{app}\Machitan.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; The task Settings > Shortcuts makes for "Shortcuts in apps running as administrator".
; Nothing happens if it was never made.
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /tn ""Machitan\AdminKeys"" /f"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveAdminKeysTask"

[Code]
const
  EVENT_MODIFY_STATE = $0002;
  // The names Machitan uses (Core/ExitSignal.cs, Core/SingleInstance.cs).
  ExitEventName = 'Local\Machitan_ExitRequest';
  ShellMutexName = 'Local\Machitan_SingleInstance';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  CloseWaitMs = 20000;
  StillRunning = 'Machitan is still running and did not close. Close it (right-click the bar, then Exit Machitan) and try again.';

function OpenEventW(DesiredAccess: Cardinal; InheritHandle: LongBool; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Event: THandle): LongBool;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): LongBool;
  external 'CloseHandle@kernel32.dll stdcall';

#if Edition == "Trimmed"
const
  DotNetDownloadPage = 'https://dotnet.microsoft.com/download/dotnet/10.0';
#if Arch == "ARM64"
  DotNetSetupKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\arm64';
#else
  DotNetSetupKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64';
#endif

// True when a .NET folder holds a .NET 10 runtime (the "Microsoft.NETCore.App" framework,
// which both the .NET Runtime and the .NET Desktop Runtime bring).
function HasDotNet10(const Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if Root = '' then
    Exit;

  if FindFirst(AddBackslash(Root) + 'shared\Microsoft.NETCore.App\10.*', FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

// Where .NET's own installer says {#DotNetArch} .NET is (as Machitan.exe looks for it), else its
// usual folder. On Windows on Arm, x64 .NET goes in dotnet\x64 and Arm64 .NET in dotnet itself.
function DotNet10Installed(): Boolean;
var
  Location: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM32, DotNetSetupKey, 'InstallLocation', Location) then
    Result := HasDotNet10(Location);
  if not Result then
    if RegQueryStringValue(HKLM64, DotNetSetupKey, 'InstallLocation', Location) then
      Result := HasDotNet10(Location);
  if not Result then
  begin
#if Arch == "ARM64"
    Result := HasDotNet10(ExpandConstant('{commonpf64}\dotnet'));
#else
    if IsArm64() then
      Result := HasDotNet10(ExpandConstant('{commonpf64}\dotnet\x64'))
    else
      Result := HasDotNet10(ExpandConstant('{commonpf64}\dotnet'));
#endif
  end;
end;

// The Trimmed edition needs the .NET 10 Runtime ({#DotNetArch}); without it Setup stops before
// anything is changed, and can open Microsoft's download page.
function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if DotNet10Installed() then
    Exit;

  Log('The .NET 10 Runtime ({#DotNetArch}) was not found; Setup stops.');
  if SuppressibleMsgBox('This edition of Machitan needs the .NET 10 Runtime ({#DotNetArch}), which is not on this PC.' + #13#10#13#10 +
      'Open Microsoft''s download page now? Install ".NET Runtime 10" for {#DotNetArch} there, then run this Setup again.' + #13#10#13#10 +
      'The Full edition of Machitan''s Setup brings .NET with it instead.',
      mbConfirmation, MB_YESNO, IDNO) = IDYES then
    ShellExecAsOriginalUser('open', DotNetDownloadPage, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  Result := False;
end;
#endif

// Asks a running Machitan to exit cleanly, as "Machitan.exe --exit" does, and waits for it.
// True when no Machitan runs afterwards.
function CloseMachitan(): Boolean;
var
  Event: THandle;
  Waited: Integer;
begin
  Result := True;
  if not CheckForMutexes(ShellMutexName) then
    Exit;

  Log('Machitan is running; asking it to exit.');
  Event := OpenEventW(EVENT_MODIFY_STATE, False, ExitEventName);
  if Event <> 0 then
  begin
    SetEvent(Event);
    CloseHandle(Event);
  end
  else
    Log('Machitan''s exit signal could not be opened.');

  Waited := 0;
  while CheckForMutexes(ShellMutexName) and (Waited < CloseWaitMs) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;

  Result := not CheckForMutexes(ShellMutexName);
  if Result then
  begin
    // Its guardian puts Explorer's taskbar back and ends a moment later.
    Sleep(1500);
    Log('Machitan exited.');
  end
  else
    Log('Machitan did not exit in time.');
end;

// The files of the Machitan already in the install folder go before the new ones come, so
// nothing is left from another edition or an older version: the Full edition's .NET files
// would make the Trimmed edition's Machitan.exe look for .NET in its own folder, and an
// install made before the AI parts were dropped still holds them (about 62 MB). Only in a
// folder that holds Machitan.exe and Machitan.dll; Setup's own uninstaller (unins*) stays.
procedure RemoveOldFiles();
var
  AppDir: String;
  Path: String;
  FindRec: TFindRec;
begin
  AppDir := ExpandConstant('{app}');
  if not (FileExists(AppDir + '\Machitan.exe') and FileExists(AppDir + '\Machitan.dll')) then
    Exit;

  Log('Removing the files of the Machitan already in ' + AppDir + '.');
  if FindFirst(AppDir + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..')
          and (CompareText(Copy(FindRec.Name, 1, 5), 'unins') <> 0) then
        begin
          Path := AppDir + '\' + FindRec.Name;
          if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
          begin
            if not DelTree(Path, True, True, True) then
              Log('Could not remove ' + Path + '.');
          end
          else if not DeleteFile(Path) then
            Log('Could not remove ' + Path + '.');
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not CloseMachitan() then
    Result := StillRunning;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    RemoveOldFiles();
end;

function InitializeUninstall(): Boolean;
begin
  Result := CloseMachitan();
  if not Result then
    SuppressibleMsgBox(StillRunning, mbError, MB_OK, IDOK);
end;

// "Run at startup" (Settings > General) is Windows' own startup list; the entry goes when it
// names this copy of Machitan.exe, as Machitan writes it (the path in quotes).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  if RegQueryStringValue(HKEY_CURRENT_USER, RunKey, 'Machitan', Command)
    and (CompareText(Trim(Command), '"' + ExpandConstant('{app}\Machitan.exe') + '"') = 0) then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'Machitan');
    Log('Machitan''s Run at startup entry was removed.');
  end;
end;
