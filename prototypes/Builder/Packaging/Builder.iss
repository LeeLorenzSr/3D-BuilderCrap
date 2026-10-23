#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef PackageVersion
  #define PackageVersion "1.0.1"
#endif
#ifndef PackageAppId
  #define PackageAppId "7C2AC698-9A70-49C8-BF7D-A58F2CF1EB90"
#endif
#ifndef PackageName
  #define PackageName "3D BuilderCrap"
#endif
#define ProductId "7C2AC698-9A70-49C8-BF7D-A58F2CF1EB90"

[Setup]
AppId={#PackageAppId}
AppName={#PackageName}
AppVersion={#PackageVersion}
AppPublisher=3D BuilderCrap project
VersionInfoVersion={#PackageVersion}.0
DefaultDirName={localappdata}\Programs\{#PackageName}
DefaultGroupName={#PackageName}
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
UninstallDisplayIcon={app}\Builder.exe
UninstallDisplayName={#PackageName}
SetupIconFile=..\Builder.Desktop\Assets\BuilderCrap.ico
OutputBaseFilename=3D-BuilderCrap-{#PackageVersion}-win-x64-Setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
AllowCancelDuringInstall=yes
SetupLogging=yes
SetupMutex=3DBuilderCrap-Setup-{#PackageAppId}
UninstallLogMode=append
InfoBeforeFile=InstallNotes.txt

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\package-files.txt"; Flags: dontcopy

[INI]
Filename: "{app}\.builder-install.ini"; Section: "Installation"; Key: "ProductId"; String: "{#ProductId}"; Flags: uninsdeleteentry
Filename: "{app}\.builder-install.ini"; Section: "Installation"; Key: "Id"; String: "{code:InstallId}"; Flags: uninsdeleteentry
Filename: "{app}\.builder-install.ini"; Section: "Installation"; Key: "Version"; String: "{#PackageVersion}"; Flags: uninsdeleteentry

[Icons]
Name: "{group}\{#PackageName}"; Filename: "{app}\Builder.exe"; WorkingDir: "{app}"
Name: "{group}\Help and recovery"; Filename: "{app}\Help\index.html"
Name: "{group}\Uninstall {#PackageName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#PackageName}"; Filename: "{app}\Builder.exe"; WorkingDir: "{app}"; Comment: "Launch {#PackageName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\3DBuilderCrap\Installations\{#PackageAppId}"; ValueType: string; ValueName: "Id"; ValueData: "{code:InstallId}"; Flags: uninsdeletekey

[UninstallDelete]
Type: files; Name: "{app}\.builder-install.ini"
Type: dirifempty; Name: "{app}"

[Code]
var
  MaintenanceHandle: THandle;
  CurrentInstallId: String;
  BackupDirectory: String;
  BackupReady, InstallCommitted: Boolean;
  OldFiles, IncomingFiles: TArrayOfString;

function CreateMutexW(Attributes: NativeInt; InitialOwner: Boolean; Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function ReleaseMutex(Handle: THandle): Boolean;
  external 'ReleaseMutex@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function GetFileAttributesW(Name: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

procedure ReleaseMaintenance;
begin
  if MaintenanceHandle <> 0 then begin
    ReleaseMutex(MaintenanceHandle);
    CloseHandle(MaintenanceHandle);
    MaintenanceHandle := 0;
  end;
end;

function ValidId(const Value: String): Boolean;
var I: Integer;
begin
  Result := Length(Value) = 64;
  if Result then for I := 1 to Length(Value) do
    if Pos(Value[I], '0123456789ABCDEFabcdef') = 0 then Result := False;
end;

function LoadIdentity(const Directory: String; const MustExist: Boolean): String;
var Marker, Product, RegisteredPath, RegisteredId: String; HasRegistration: Boolean;
begin
  Result := '';
  Marker := AddBackslash(Directory) + '.builder-install.ini';
  HasRegistration := RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#PackageAppId}_is1', 'InstallLocation', RegisteredPath);
  if HasRegistration and (CompareText(RemoveBackslashUnlessRoot(RegisteredPath), RemoveBackslashUnlessRoot(Directory)) <> 0) then begin
    Result := 'An installation is already registered in another folder. Update that copy in place, or uninstall it before choosing a new folder.'; exit;
  end;
  if FileExists(Marker) then begin
    Product := GetIniString('Installation', 'ProductId', '', Marker);
    CurrentInstallId := GetIniString('Installation', 'Id', '', Marker);
    if (Product <> '{#ProductId}') or not ValidId(CurrentInstallId) then
      Result := 'The installation marker is invalid. No files were changed. Close the app and repair the original installation.';
  end else if HasRegistration and RegQueryStringValue(HKCU, 'Software\3DBuilderCrap\Installations\{#PackageAppId}', 'Id', RegisteredId) and ValidId(RegisteredId) then
    CurrentInstallId := RegisteredId
  else if MustExist then
    Result := 'The installation marker is missing. Run the matching installer to repair this copy before uninstalling.'
  else
    CurrentInstallId := Uppercase(GetSHA256OfString(UTF8Encode(ExpandFileName(Directory))));
end;

function HoldMaintenance: String;
var Prefix: String; WaitResult: Cardinal;
begin
  Result := '';
  ReleaseMaintenance;
  Prefix := 'Global\3DBuilderCrap-{#ProductId}-' + Uppercase(CurrentInstallId);
  MaintenanceHandle := CreateMutexW(0, False, Prefix + '-Maintenance');
  if MaintenanceHandle = 0 then begin Result := 'Installation access could not be reserved. No files were changed.'; exit; end;
  WaitResult := WaitForSingleObject(MaintenanceHandle, 0);
  if (WaitResult <> 0) and (WaitResult <> 128) then begin
    CloseHandle(MaintenanceHandle); MaintenanceHandle := 0;
    Result := 'Another installation is using this copy. Wait for it to finish and try again.'; exit;
  end;
  if CheckForMutexes(Prefix + '-Running') then begin
    ReleaseMaintenance;
    Result := '3D BuilderCrap is running. Save your work and close this installation''s desktop windows and MCP clients before updating or uninstalling. Nothing was closed or changed.';
  end;
end;

function InstallId(Param: String): String;
begin Result := CurrentInstallId; end;

function DirectoryHasFiles(const Directory: String): Boolean;
var Find: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Directory) + '*', Find) then begin
    try
      repeat
        if (Find.Name <> '.') and (Find.Name <> '..') then begin Result := True; break; end;
      until not FindNext(Find);
    finally FindClose(Find); end;
  end;
end;

function SafeRelative(const Value: String): Boolean;
var Resolved, Directory, Part: String;
begin
  Directory := RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}')));
  Resolved := ExpandFileName(AddBackslash(Directory) + Value);
  Result := (Value <> '') and (Pos(':', Value) = 0) and (Value[1] <> '\') and
    (Value[1] <> '/') and (Pos('..', Value) = 0) and
    (CompareText(Copy(Resolved, 1, Length(Directory) + 1), AddBackslash(Directory)) = 0);
  { Do not follow a junction out of the named application directory. }
  Part := ExtractFileDir(Resolved);
  while Result and (Length(Part) >= Length(Directory)) do begin
    if DirExists(Part) and ((GetFileAttributesW(Part) and $400) <> 0) then Result := False;
    Part := ExtractFileDir(Part);
  end;
end;

function Listed(const Value: String; const Values: TArrayOfString): Boolean;
var I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(Values) - 1 do
    if CompareText(Value, Values[I]) = 0 then begin Result := True; exit; end;
end;

procedure AddOld(const Value: String);
var Count: Integer;
begin
  if FileExists(AddBackslash(ExpandConstant('{app}')) + Value) and not Listed(Value, OldFiles) then begin
    Count := GetArrayLength(OldFiles); SetArrayLength(OldFiles, Count + 1); OldFiles[Count] := Value;
  end;
end;

procedure DeleteOwnedBackup;
begin
  { The only recursive removal is this exact, marker-verified installer-owned child. }
  if (CompareText(ExpandFileName(BackupDirectory), ExpandFileName(ExpandConstant('{app}\.builder-rollback'))) <> 0) or
    ((GetFileAttributesW(BackupDirectory) and $400) <> 0) or
    (GetIniString('Rollback', 'ProductId', '', BackupDirectory + '\owner.ini') <> '{#ProductId}') then
    RaiseException('Rollback folder ownership could not be verified. No backup was removed.');
  if not DelTree(BackupDirectory, True, True, True) then
    RaiseException('The update completed, but its backup could not be removed: ' + BackupDirectory);
  BackupReady := False;
end;

procedure RestoreBackup;
var I: Integer; Source, Destination: String;
begin
  Log('Restoring the previous installer-owned payload.');
  for I := 0 to GetArrayLength(OldFiles) - 1 do begin
    if not SafeRelative(OldFiles[I]) then RaiseException('Unsafe rollback inventory.');
    Source := BackupDirectory + '\files\' + OldFiles[I];
    Destination := AddBackslash(ExpandConstant('{app}')) + OldFiles[I];
    if FileExists(Source) then begin
      if not FileExists(Destination) or (GetSHA256OfFile(Source) <> GetSHA256OfFile(Destination)) then begin
        ForceDirectories(ExtractFileDir(Destination));
        if not CopyFile(Source, Destination, False) then
          RaiseException('Close programs using this copy and rerun Setup. Previous files are retained in ' + BackupDirectory);
      end;
    end;
  end;
  for I := 0 to GetArrayLength(IncomingFiles) - 1 do begin
    if not SafeRelative(IncomingFiles[I]) then RaiseException('Unsafe incoming inventory.');
    Destination := AddBackslash(ExpandConstant('{app}')) + IncomingFiles[I];
    if not Listed(IncomingFiles[I], OldFiles) and FileExists(Destination) then
      if not DeleteFile(Destination) then RaiseException('A new package file could not be removed; backup retained in ' + BackupDirectory);
  end;
  DeleteOwnedBackup;
end;

function PrepareBackup: String;
var I: Integer; Source, Destination: String;
begin
  Result := '';
  SetArrayLength(OldFiles, 0);
  if FileExists(ExpandConstant('{app}\Builder.exe')) then begin
    if not LoadStringsFromFile(ExpandConstant('{app}\package-files.txt'), OldFiles) then begin
      Result := 'The installed file inventory is missing. No files were changed. Uninstall this copy, preserving your models, before reinstalling.'; exit;
    end;
  end;
  AddOld('.builder-install.ini'); AddOld('unins000.exe'); AddOld('unins000.dat');
  ExtractTemporaryFile('package-files.txt');
  if not LoadStringsFromFile(ExpandConstant('{tmp}\package-files.txt'), IncomingFiles) then begin Result := 'The package inventory is missing.'; exit; end;
  for I := 0 to GetArrayLength(IncomingFiles) - 1 do begin
    if not SafeRelative(IncomingFiles[I]) then begin Result := 'The package inventory is invalid.'; exit; end;
    if FileExists(AddBackslash(ExpandConstant('{app}')) + IncomingFiles[I]) and not Listed(IncomingFiles[I], OldFiles) then begin
      Result := 'Setup would overwrite a user-added file: ' + IncomingFiles[I] + '. Move that file elsewhere first. Nothing was changed.'; exit;
    end;
  end;
  if GetArrayLength(OldFiles) = 0 then exit;
  BackupDirectory := ExpandConstant('{app}\.builder-rollback');
  if DirExists(BackupDirectory) then begin Result := 'An unfinished update backup already exists. No files were changed.'; exit; end;
  ForceDirectories(BackupDirectory);
  SetIniString('Rollback', 'ProductId', '{#ProductId}', BackupDirectory + '\owner.ini');
  SaveStringsToFile(BackupDirectory + '\old-files.txt', OldFiles, False);
  SaveStringsToFile(BackupDirectory + '\incoming-files.txt', IncomingFiles, False);
  for I := 0 to GetArrayLength(OldFiles) - 1 do begin
    if not SafeRelative(OldFiles[I]) then begin Result := 'The installed inventory is invalid.'; DeleteOwnedBackup; exit; end;
    Source := AddBackslash(ExpandConstant('{app}')) + OldFiles[I];
    Destination := BackupDirectory + '\files\' + OldFiles[I];
    if FileExists(Source) then begin
      ForceDirectories(ExtractFileDir(Destination));
      if not CopyFile(Source, Destination, False) then begin
        Result := 'Installed files could not be backed up. Close other programs using this copy and try again. No application files were changed.'; DeleteOwnedBackup; exit;
      end;
    end;
  end;
  SetIniString('Rollback', 'Ready', '1', BackupDirectory + '\owner.ini');
  BackupReady := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Directory: String; InstalledVersion, IncomingVersion: Int64;
begin
  ReleaseMaintenance;
  Directory := ExpandConstant('{app}');
  Result := LoadIdentity(Directory, False);
  if Result <> '' then exit;
  if not FileExists(AddBackslash(Directory) + '.builder-install.ini') and not RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#PackageAppId}_is1') and DirectoryHasFiles(Directory) then begin
    Result := 'Choose an empty directory. This destination already contains files and is not a recognized 3D BuilderCrap installation. Nothing was changed.'; exit;
  end;
  Result := HoldMaintenance;
  if Result <> '' then exit;
  BackupDirectory := ExpandConstant('{app}\.builder-rollback');
  if DirExists(BackupDirectory) then begin
    if GetIniString('Rollback', 'ProductId', '', BackupDirectory + '\owner.ini') <> '{#ProductId}' then begin Result := 'An unrelated rollback folder already exists. Nothing was changed.'; exit; end;
    if GetIniString('Rollback', 'Ready', '', BackupDirectory + '\owner.ini') = '1' then begin
      if not LoadStringsFromFile(BackupDirectory + '\old-files.txt', OldFiles) or not LoadStringsFromFile(BackupDirectory + '\incoming-files.txt', IncomingFiles) then begin Result := 'The interrupted update inventory could not be read. Backup preserved.'; exit; end;
      if GetIniString('Rollback', 'Committed', '', BackupDirectory + '\owner.ini') <> '1' then RestoreBackup else DeleteOwnedBackup;
    end else DeleteOwnedBackup;
  end;
  if FileExists(AddBackslash(Directory) + 'Builder.exe') then begin
    if not GetPackedVersion(AddBackslash(Directory) + 'Builder.exe', InstalledVersion) then begin
      Result := 'The installed version could not be checked. No files were changed.'; exit;
    end;
    if not GetPackedVersion(ExpandConstant('{srcexe}'), IncomingVersion) then begin
      Result := 'The package version could not be checked. No files were changed.'; exit;
    end;
    if ComparePackedVersion(InstalledVersion, IncomingVersion) > 0 then begin
      Result := 'A newer version is installed. Downgrade was refused; no files were changed. Use the same or a newer installer.'; exit;
    end;
  end;
  Result := PrepareBackup;
end;

procedure DeinitializeSetup;
begin
  try
    if BackupReady then begin
      if InstallCommitted then DeleteOwnedBackup else RestoreBackup;
    end;
  finally ReleaseMaintenance; end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    InstallCommitted := True;
    if BackupReady then SetIniString('Rollback', 'Committed', '1', BackupDirectory + '\owner.ini');
  end;
end;

function InitializeUninstall: Boolean;
var Problem: String;
begin
  Problem := LoadIdentity(ExpandConstant('{app}'), True);
  if Problem = '' then Problem := HoldMaintenance;
  if (Problem = '') and DirExists(ExpandConstant('{app}\.builder-rollback')) then Problem := 'An interrupted update backup is present. Run the same or a newer installer to repair it before uninstalling.';
  Result := Problem = '';
  if not Result then SuppressibleMsgBox(Problem, mbInformation, MB_OK, IDOK);
end;

procedure DeinitializeUninstall;
begin ReleaseMaintenance; end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Directory, Marker: String;
begin
  if CurUninstallStep = usPostUninstall then begin
    Directory := ExpandConstant('{app}');
    if DirExists(Directory) and DirectoryHasFiles(Directory) then begin
      { Preserve user-added files and enough ownership to allow a safe reinstall. }
      Marker := AddBackslash(Directory) + '.builder-install.ini';
      SetIniString('Installation', 'ProductId', '{#ProductId}', Marker);
      SetIniString('Installation', 'Id', CurrentInstallId, Marker);
    end;
  end;
end;
