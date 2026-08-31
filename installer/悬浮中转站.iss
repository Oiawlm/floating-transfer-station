#define MyAppName "悬浮中转站"
#define MyAppVersion "1.4.0"
#define MyAppExeName "悬浮中转站.exe"
#define MyAppMutexName "Local\FloatingTransferStation.App"

[Setup]
AppId={{9F0E0B0F-4E4F-47C2-9E63-56847E509D50}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=悬浮中转站-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
AppMutex={#MyAppMutexName}

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autoprograms}\卸载{#MyAppName}"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait skipifsilent

[Code]
type
  TWin32FindData = record
    FileAttributes: LongWord;
    CreationTimeLow: LongWord;
    CreationTimeHigh: LongWord;
    LastAccessTimeLow: LongWord;
    LastAccessTimeHigh: LongWord;
    LastWriteTimeLow: LongWord;
    LastWriteTimeHigh: LongWord;
    FileSizeHigh: LongWord;
    FileSizeLow: LongWord;
    Reserved0: LongWord;
    Reserved1: LongWord;
    FileName: array[0..259] of Char;
    AlternateFileName: array[0..13] of Char;
  end;

function FindFirstFileW(const FileName: String;
  var FindData: TWin32FindData): THandle;
  external 'FindFirstFileW@kernel32.dll stdcall';
function FindNextFileW(FindHandle: THandle;
  var FindData: TWin32FindData): Boolean;
  external 'FindNextFileW@kernel32.dll stdcall';
function FindCloseWin32(FindHandle: THandle): Boolean;
  external 'FindClose@kernel32.dll stdcall';
function GetLastError: LongWord;
  external 'GetLastError@kernel32.dll stdcall';

const
  DataRegistryKey = 'Software\FloatingTransferStation';
  DataDirectoryRegistryValue = 'DataDirectory';
  DataParentDirectoryRegistryValue = 'DataParentDirectory';
  ManagedDataParentLeaf = '悬浮中转站';
  ManagedDataLeaf = 'Data';
  ErrorFileNotFound = 2;
  ErrorNoMoreFiles = 18;
  InvalidFindHandle = -1;

var
  DataDirectoryPage: TInputDirWizardPage;
  ExistingDataDirectory: String;
  ExistingDataParentDirectory: String;
  SelectedDataDirectory: String;
  CreatedMigrationDirectory: Boolean;
  DataDirectoryCommitted: Boolean;
  MigrationPerformed: Boolean;
  UninstallDataDirectory: String;
  UninstallDataDirectoryValid: Boolean;

function IsExtendedDevicePath(const Value: String): Boolean;
begin
  Result := (Length(Value) >= 4) and (Value[1] = '\') and
    (Value[2] = '\') and ((Value[3] = '?') or (Value[3] = '.')) and
    (Value[4] = '\');
end;

function IsFullyQualifiedPath(const Value: String): Boolean;
var
  Candidate: String;
  Index: Integer;
begin
  Candidate := Trim(Value);
  if IsExtendedDevicePath(Candidate) then
  begin
    Result := False;
    Exit;
  end;

  if (Length(Candidate) >= 3) and (Candidate[2] = ':') and
    (Candidate[3] = '\') then
  begin
    Result := True;
    Exit;
  end;

  Result := False;
  if (Length(Candidate) < 5) or (Candidate[1] <> '\') or
    (Candidate[2] <> '\') then
    Exit;

  for Index := 3 to Length(Candidate) - 1 do
  begin
    if Candidate[Index] = '\' then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function NormalizeDirectory(const Value: String): String;
begin
  if not IsFullyQualifiedPath(Value) then
  begin
    Result := '';
    Exit;
  end;
  Result := RemoveBackslashUnlessRoot(ExpandFileName(Trim(Value)));
end;

function IsRootDirectory(const Value: String): Boolean;
var
  Normalized: String;
  WithoutTrailingBackslash: String;
  Index: Integer;
  BackslashCount: Integer;
begin
  if not IsFullyQualifiedPath(Value) then
  begin
    Result := False;
    Exit;
  end;
  Normalized := ExpandFileName(Trim(Value));
  Result := (Length(Normalized) = 3) and (Normalized[2] = ':') and
    (Normalized[3] = '\');
  if Result or (Normalized[1] <> '\') or (Normalized[2] <> '\') then
    Exit;

  WithoutTrailingBackslash := Normalized;
  while (Length(WithoutTrailingBackslash) > 2) and
    (WithoutTrailingBackslash[Length(WithoutTrailingBackslash)] = '\') do
  begin
    Delete(WithoutTrailingBackslash, Length(WithoutTrailingBackslash), 1);
  end;

  BackslashCount := 0;
  for Index := 1 to Length(WithoutTrailingBackslash) do
  begin
    if WithoutTrailingBackslash[Index] = '\' then
      BackslashCount := BackslashCount + 1;
  end;
  Result := BackslashCount = 3;
end;

function BuildDataDirectory(const ParentDirectory: String): String;
begin
  Result := AddBackslash(NormalizeDirectory(ParentDirectory)) +
    ManagedDataParentLeaf + '\' + ManagedDataLeaf;
end;

function GetManagedDataParent(const DataDirectory: String): String;
begin
  Result := RemoveBackslashUnlessRoot(ExtractFilePath(
    RemoveBackslashUnlessRoot(DataDirectory)));
end;

function GetDataParentDirectory(const DataDirectory: String): String;
begin
  Result := RemoveBackslashUnlessRoot(ExtractFilePath(
    GetManagedDataParent(DataDirectory)));
end;

function IsPathWithinDirectory(const CandidatePath, ParentDirectory: String): Boolean;
var
  NormalizedCandidate: String;
  NormalizedParent: String;
  ParentPrefix: String;
begin
  Result := False;
  if not IsFullyQualifiedPath(CandidatePath) or
    not IsFullyQualifiedPath(ParentDirectory) then
    Exit;

  NormalizedCandidate := NormalizeDirectory(CandidatePath);
  NormalizedParent := NormalizeDirectory(ParentDirectory);
  ParentPrefix := AddBackslash(NormalizedParent);
  Result := (CompareText(NormalizedCandidate, NormalizedParent) = 0) or
    (CompareText(Copy(NormalizedCandidate, 1, Length(ParentPrefix)),
      ParentPrefix) = 0);
end;

function IsManagedDataDirectory(const Value: String): Boolean;
var
  Normalized: String;
  ManagedParent: String;
  ParentDirectory: String;
begin
  Result := False;
  if not IsFullyQualifiedPath(Value) then
    Exit;

  Normalized := NormalizeDirectory(Value);
  ManagedParent := GetManagedDataParent(Normalized);
  ParentDirectory := GetDataParentDirectory(Normalized);
  if IsRootDirectory(ParentDirectory) then
    Exit;

  Result := (CompareText(ExtractFileName(Normalized), ManagedDataLeaf) = 0) and
    (CompareText(ExtractFileName(ManagedParent), ManagedDataParentLeaf) = 0) and
    (ParentDirectory <> '') and
    (CompareText(Normalized, BuildDataDirectory(ParentDirectory)) = 0);
end;

function GetLegacyDataDirectory: String;
begin
  Result := ExpandConstant('{localappdata}\悬浮中转站\Data');
end;

function ReadExistingDataDirectory: String;
var
  RegisteredValue: String;
begin
  if RegQueryStringValue(HKCU, DataRegistryKey,
    DataDirectoryRegistryValue, RegisteredValue) and
    IsManagedDataDirectory(RegisteredValue) then
  begin
    Result := NormalizeDirectory(RegisteredValue);
  end
  else
  begin
    Result := GetLegacyDataDirectory;
  end;
end;

function ReadExistingDataParentDirectory: String;
var
  RegisteredParent: String;
begin
  if RegQueryStringValue(HKCU, DataRegistryKey,
    DataParentDirectoryRegistryValue, RegisteredParent) and
    IsFullyQualifiedPath(RegisteredParent) and
    not IsRootDirectory(RegisteredParent) and
    (CompareText(BuildDataDirectory(RegisteredParent), ExistingDataDirectory) = 0) then
  begin
    Result := NormalizeDirectory(RegisteredParent);
  end
  else
  begin
    Result := GetDataParentDirectory(ExistingDataDirectory);
  end;
end;

function GetSelectedDataDirectory: String;
begin
  Result := BuildDataDirectory(DataDirectoryPage.Values[0]);
end;

function GetUniqueProbeFileName(const ParentDirectory: String): String;
var
  Attempt: Integer;
begin
  Result := '';
  for Attempt := 1 to 100 do
  begin
    Result := AddBackslash(ParentDirectory) + '.write-probe-' +
      GetDateTimeString('yyyymmddhhnnss', #0, #0) + '-' + IntToStr(Attempt);
    if not FileOrDirExists(Result) then
      Exit;
  end;
  Result := '';
end;

function ValidateDataParentDirectory: Boolean;
var
  ParentDirectory: String;
  ProbeFileName: String;
begin
  Result := False;
  if Trim(DataDirectoryPage.Values[0]) = '' then
  begin
    MsgBox('请选择内容存储父目录。', mbError, MB_OK);
    Exit;
  end;

  if not IsFullyQualifiedPath(DataDirectoryPage.Values[0]) then
  begin
    MsgBox('内容存储父目录必须是完整的 Windows 路径。', mbError, MB_OK);
    Exit;
  end;

  ParentDirectory := NormalizeDirectory(DataDirectoryPage.Values[0]);
  if IsRootDirectory(ParentDirectory) then
  begin
    MsgBox('内容存储父目录不能是磁盘或网络共享根目录。', mbError, MB_OK);
    Exit;
  end;

  if not ForceDirectories(ParentDirectory) then
  begin
    MsgBox('无法创建内容存储父目录。请选择其他位置。', mbError, MB_OK);
    Exit;
  end;

  ProbeFileName := GetUniqueProbeFileName(ParentDirectory);
  if ProbeFileName = '' then
  begin
    MsgBox('无法为内容存储父目录创建验证文件。请选择其他位置。', mbError, MB_OK);
    Exit;
  end;
  try
    if not SaveStringToFile(ProbeFileName, '悬浮中转站', False) then
    begin
      MsgBox('内容存储父目录不可写。请选择其他位置。', mbError, MB_OK);
      Exit;
    end;
  finally
    if FileExists(ProbeFileName) then
      DeleteFile(ProbeFileName);
  end;

  DataDirectoryPage.Values[0] := ParentDirectory;
  Result := True;
end;

function GetFindDataName(const FindData: TWin32FindData): String;
var
  Index: Integer;
begin
  Result := '';
  for Index := 0 to 259 do
  begin
    if FindData.FileName[Index] = #0 then
      Exit;
    Result := Result + FindData.FileName[Index];
  end;
end;

function CopyDirectory(const SourceDirectory, TargetDirectory: String): Boolean;
var
  FindData: TWin32FindData;
  FindErrorCode: LongWord;
  FindHandle: THandle;
  SourceItem: String;
  TargetItem: String;
begin
  Result := False;
  if not ForceDirectories(TargetDirectory) then
    Exit;

  FindHandle := FindFirstFileW(AddBackslash(SourceDirectory) + '*', FindData);
  if FindHandle = THandle(InvalidFindHandle) then
  begin
    FindErrorCode := GetLastError;
    Result := FindErrorCode = ErrorFileNotFound;
    Exit;
  end;

  try
    repeat
      if (GetFindDataName(FindData) <> '.') and
        (GetFindDataName(FindData) <> '..') then
      begin
        SourceItem := AddBackslash(SourceDirectory) + GetFindDataName(FindData);
        TargetItem := AddBackslash(TargetDirectory) + GetFindDataName(FindData);
        if (FindData.FileAttributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          if not CopyDirectory(SourceItem, TargetItem) then
            Exit;
        end
        else if not CopyFile(SourceItem, TargetItem, False) then
        begin
          Exit;
        end;
      end;

      if not FindNextFileW(FindHandle, FindData) then
      begin
        FindErrorCode := GetLastError;
        if FindErrorCode <> ErrorNoMoreFiles then
          Exit;
        Result := True;
        Exit;
      end;
    until False;
  finally
    FindCloseWin32(FindHandle);
  end;
end;

function DirectoryContentsMatch(const SourceDirectory, TargetDirectory: String): Boolean;
var
  FindData: TWin32FindData;
  FindErrorCode: LongWord;
  FindHandle: THandle;
  SourceItem: String;
  TargetItem: String;
begin
  Result := False;
  if not DirExists(TargetDirectory) then
    Exit;

  FindHandle := FindFirstFileW(AddBackslash(SourceDirectory) + '*', FindData);
  if FindHandle = THandle(InvalidFindHandle) then
  begin
    FindErrorCode := GetLastError;
    Result := FindErrorCode = ErrorFileNotFound;
    Exit;
  end;

  try
    repeat
      if (GetFindDataName(FindData) <> '.') and
        (GetFindDataName(FindData) <> '..') then
      begin
        SourceItem := AddBackslash(SourceDirectory) + GetFindDataName(FindData);
        TargetItem := AddBackslash(TargetDirectory) + GetFindDataName(FindData);
        if (FindData.FileAttributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          if not DirectoryContentsMatch(SourceItem, TargetItem) then
            Exit;
        end
        else if not FileExists(TargetItem) then
        begin
          Exit;
        end;
      end;

      if not FindNextFileW(FindHandle, FindData) then
      begin
        FindErrorCode := GetLastError;
        if FindErrorCode <> ErrorNoMoreFiles then
          Exit;
        Result := True;
        Exit;
      end;
    until False;
  finally
    FindCloseWin32(FindHandle);
  end;
end;

function DeleteManagedDataDirectory(const DataDirectory: String): Boolean;
var
  ManagedParent: String;
begin
  Result := False;
  if not IsManagedDataDirectory(DataDirectory) then
  begin
    Log('Refusing to delete an unmanaged data directory: ' + DataDirectory);
    Exit;
  end;

  ManagedParent := GetManagedDataParent(DataDirectory);
  Result := DelTree(ManagedParent, True, True, True);
end;

procedure RemovePreparedDataDirectory;
begin
  if CreatedMigrationDirectory and IsManagedDataDirectory(SelectedDataDirectory) then
  begin
    if not DeleteManagedDataDirectory(SelectedDataDirectory) then
      Log('Failed to remove uncommitted data directory: ' + SelectedDataDirectory);
  end;
end;

function GetMigrationDirectory(const ManagedParent: String): String;
var
  Attempt: Integer;
begin
  Result := '';
  for Attempt := 1 to 100 do
  begin
    Result := AddBackslash(ManagedParent) + '.migration-' +
      GetDateTimeString('yyyymmddhhnnss', #0, #0) + '-' + IntToStr(Attempt);
    if not FileOrDirExists(Result) then
      Exit;
  end;
  Result := '';
end;

function PrepareDataDirectoryMigration: Boolean;
var
  ManagedParent: String;
  ExistingManagedParent: String;
  MigrationDirectory: String;
begin
  Result := False;
  SelectedDataDirectory := GetSelectedDataDirectory;
  CreatedMigrationDirectory := False;
  MigrationPerformed := False;

  if CompareText(NormalizeDirectory(SelectedDataDirectory),
    NormalizeDirectory(ExistingDataDirectory)) = 0 then
  begin
    if not DirExists(SelectedDataDirectory) and
      not ForceDirectories(SelectedDataDirectory) then
      Exit;
    Result := True;
    Exit;
  end
  else
  begin
    ExistingManagedParent := GetManagedDataParent(ExistingDataDirectory);
    if IsPathWithinDirectory(SelectedDataDirectory, ExistingManagedParent) then
    begin
      Log('Refusing to migrate data into its existing managed parent: ' +
        SelectedDataDirectory);
      Exit;
    end;
    if DirExists(SelectedDataDirectory) then
    begin
      Log('Refusing to overwrite an existing data directory: ' + SelectedDataDirectory);
      Exit;
    end;
  end;

  ManagedParent := GetManagedDataParent(SelectedDataDirectory);
  if DirExists(ManagedParent) then
  begin
    Log('Refusing to use a pre-existing managed data parent: ' + ManagedParent);
    Exit;
  end;
  if not ForceDirectories(ManagedParent) then
    Exit;

  if DirExists(ExistingDataDirectory) then
  begin
    MigrationDirectory := GetMigrationDirectory(ManagedParent);
    if (MigrationDirectory = '') or
      not CopyDirectory(ExistingDataDirectory, MigrationDirectory) or
      not DirectoryContentsMatch(ExistingDataDirectory, MigrationDirectory) or
      not RenameFile(MigrationDirectory, SelectedDataDirectory) then
    begin
      if (MigrationDirectory <> '') and DirExists(MigrationDirectory) then
        DelTree(MigrationDirectory, True, True, True);
      RemoveDir(ManagedParent);
      Exit;
    end;
    MigrationPerformed := True;
  end
  else if not ForceDirectories(SelectedDataDirectory) then
  begin
    RemoveDir(ManagedParent);
    Exit;
  end;

  CreatedMigrationDirectory := True;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not PrepareDataDirectoryMigration then
    Result := '无法迁移现有内容。请确认目标文件夹可用后重试；原存储位置未作修改。';
end;

procedure ForceCloseRunningApplication;
var
  ResultCode: Integer;
begin
  if CheckForMutexes('{#MyAppMutexName}') then
  begin
    if not Exec(
      ExpandConstant('{sys}\taskkill.exe'),
      '/F /IM "{#MyAppExeName}"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
    begin
      Log('Failed to start taskkill; AppMutex will keep setup from overwriting the running application.');
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  ForceCloseRunningApplication;
  Result := True;
end;

procedure InitializeWizard;
begin
  ExistingDataDirectory := ReadExistingDataDirectory;
  ExistingDataParentDirectory := ReadExistingDataParentDirectory;
  DataDirectoryPage := CreateInputDirPage(wpSelectDir,
    '内容存储位置',
    '选择图片、文字和设置的存储父目录',
    '悬浮中转站会在所选文件夹中创建并管理“悬浮中转站\Data”。点击“下一步”继续。',
    False,
    SetupMessage(msgNewFolderName));
  DataDirectoryPage.Add('存储父目录：');
  DataDirectoryPage.Values[0] := ExistingDataParentDirectory;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  if CurPageID = DataDirectoryPage.ID then
    Result := ValidateDataParentDirectory
  else
    Result := True;
end;

procedure RestoreRegistrationValue(const ValueName, PreviousValue: String;
  const PreviousValueExists: Boolean);
begin
  if PreviousValueExists then
    RegWriteStringValue(HKCU, DataRegistryKey, ValueName, PreviousValue)
  else
    RegDeleteValue(HKCU, DataRegistryKey, ValueName);
end;

function WriteDataDirectoryRegistration: Boolean;
var
  PreviousDataDirectory: String;
  PreviousDataParentDirectory: String;
  PreviousDataDirectoryExists: Boolean;
  PreviousDataParentDirectoryExists: Boolean;
begin
  PreviousDataDirectoryExists := RegQueryStringValue(HKCU, DataRegistryKey,
    DataDirectoryRegistryValue, PreviousDataDirectory);
  PreviousDataParentDirectoryExists := RegQueryStringValue(HKCU, DataRegistryKey,
    DataParentDirectoryRegistryValue, PreviousDataParentDirectory);
  Result := RegWriteStringValue(HKCU, DataRegistryKey,
    DataParentDirectoryRegistryValue, DataDirectoryPage.Values[0]) and
    RegWriteStringValue(HKCU, DataRegistryKey,
      DataDirectoryRegistryValue, SelectedDataDirectory);
  if not Result then
  begin
    RestoreRegistrationValue(DataDirectoryRegistryValue, PreviousDataDirectory,
      PreviousDataDirectoryExists);
    RestoreRegistrationValue(DataParentDirectoryRegistryValue,
      PreviousDataParentDirectory, PreviousDataParentDirectoryExists);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not WriteDataDirectoryRegistration then
    begin
      MsgBox('无法登记内容存储位置。原存储位置未作修改，本次新建副本将被清理。',
        mbError, MB_OK);
      Exit;
    end;

    DataDirectoryCommitted := True;
    if MigrationPerformed and DirExists(ExistingDataDirectory) and
      not DeleteManagedDataDirectory(ExistingDataDirectory) then
    begin
      MsgBox('新内容存储位置已启用，但无法删除旧副本：' + ExistingDataDirectory,
        mbInformation, MB_OK);
    end;
  end;
end;

procedure DeinitializeSetup;
begin
  if not DataDirectoryCommitted then
    RemovePreparedDataDirectory;
end;

function InitializeUninstall: Boolean;
var
  RegisteredValue: String;
begin
  UninstallDataDirectory := '';
  UninstallDataDirectoryValid := False;
  if RegQueryStringValue(HKCU, DataRegistryKey,
    DataDirectoryRegistryValue, RegisteredValue) then
  begin
    if IsManagedDataDirectory(RegisteredValue) then
    begin
      UninstallDataDirectory := NormalizeDirectory(RegisteredValue);
      UninstallDataDirectoryValid := True;
    end
    else
    begin
      Log('Refusing to remove malformed registered data directory: ' + RegisteredValue);
      MsgBox('无法安全删除已登记的内容目录：' + RegisteredValue +
        '。数据和登记值已保留。', mbError, MB_OK);
    end;
  end
  else
  begin
    UninstallDataDirectory := GetLegacyDataDirectory;
    UninstallDataDirectoryValid := True;
  end;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    if not UninstallDataDirectoryValid then
      Exit;

    if (UninstallDataDirectory <> '') and DirExists(UninstallDataDirectory) and
      not DeleteManagedDataDirectory(UninstallDataDirectory) then
    begin
      Log('Failed to remove managed data directory: ' + UninstallDataDirectory);
    end;
    RegDeleteValue(HKCU, DataRegistryKey, DataDirectoryRegistryValue);
    RegDeleteValue(HKCU, DataRegistryKey, DataParentDirectoryRegistryValue);
  end;
end;
