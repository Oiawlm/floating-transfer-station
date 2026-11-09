#define MyAppName "悬浮中转站"
#define MyAppVersionFile FileOpen(AddBackslash(SourcePath) + "..\version.txt")
#if MyAppVersionFile == 0
#error "Unable to read the repository version.txt file."
#endif
#define MyAppVersion Trim(FileRead(MyAppVersionFile))
#expr FileClose(MyAppVersionFile)
#undef MyAppVersionFile
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
; 发行资产名单一权威（契约 #26 修订①）：前缀 FloatingTransferStation-Setup- 与更新器
; UpdateService.InstallerAssetNamePrefix 同源互锁（LifecycleTests.ReleaseMetadata 锁定），
; 产物出生即更新器精确名，发行上传禁止改名；AppName/安装目录/exe 名不属资产命名、保持中文。
OutputBaseFilename=FloatingTransferStation-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\FloatingTransferStation\Assets\AppIcon.ico
AppMutex={#MyAppMutexName}

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autoprograms}\卸载{#MyAppName}"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\FloatingTransferStation"; ValueType: string; ValueName: "InstallDirectory"; ValueData: "{app}"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait

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
function GetFileAttributesW(const FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

const
  DataRegistryKey = 'Software\FloatingTransferStation';
  DataDirectoryRegistryValue = 'DataDirectory';
  DataParentDirectoryRegistryValue = 'DataParentDirectory';
  InstallDirectoryRegistryValue = 'InstallDirectory';
  InstallRegistryKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{9F0E0B0F-4E4F-47C2-9E63-56847E509D50}_is1';
  ManagedRootLeaf = '悬浮中转站';
  LegacyDataLeaf = 'Data';
  ErrorFileNotFound = 2;
  ErrorPathNotFound = 3;
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

function IsSameInstallDirectory(const RegisteredDirectory, CurrentDirectory: String): Boolean;
begin
  Result := IsFullyQualifiedPath(RegisteredDirectory) and
    IsFullyQualifiedPath(CurrentDirectory) and
    (CompareText(NormalizeDirectory(RegisteredDirectory),
      NormalizeDirectory(CurrentDirectory)) = 0);
end;

function IsInstallDirectorySelectionSafe(const ExistingDirectory, OwnerDirectory,
  SelectedDirectory: String): Boolean;
begin
  Result := IsFullyQualifiedPath(SelectedDirectory) and
    ((ExistingDirectory = '') or
      IsSameInstallDirectory(ExistingDirectory, SelectedDirectory) or
      IsSameInstallDirectory(ExistingDirectory, OwnerDirectory));
end;

procedure ReadInstallGuardDirectories(var ExistingDirectory, OwnerDirectory: String);
begin
  ExistingDirectory := '';
  OwnerDirectory := '';
  RegQueryStringValue(HKCU, InstallRegistryKey, 'InstallLocation', ExistingDirectory);
  RegQueryStringValue(HKCU, DataRegistryKey, InstallDirectoryRegistryValue, OwnerDirectory);
end;

function ValidateInstallDirectorySelection: Boolean;
var
  ExistingDirectory: String;
  OwnerDirectory: String;
begin
  ReadInstallGuardDirectories(ExistingDirectory, OwnerDirectory);
  Result := IsInstallDirectorySelectionSafe(ExistingDirectory, OwnerDirectory,
    ExpandConstant('{app}'));
end;

function IsCurrentInstallation: Boolean;
var
  RegisteredDirectory: String;
begin
  Result := RegQueryStringValue(HKCU, DataRegistryKey,
    InstallDirectoryRegistryValue, RegisteredDirectory) and
    IsSameInstallDirectory(RegisteredDirectory, ExpandConstant('{app}'));
end;

function BuildDataDirectory(const ParentDirectory: String): String;
begin
  // 唯一受管形状（数据目录模型统一）：<所选>/悬浮中转站——文件夹本身即数据根。
  Result := AddBackslash(NormalizeDirectory(ParentDirectory)) + ManagedRootLeaf;
end;

function BuildDirectLegacyDataDirectory(const ParentDirectory: String): String;
begin
  // 旧直下形状（待迁移旧形，仅存量登记读取）：<所选>/Data。
  Result := AddBackslash(NormalizeDirectory(ParentDirectory)) + LegacyDataLeaf;
end;

function BuildLegacyDataDirectory(const ParentDirectory: String): String;
begin
  // 旧嵌套形状（待迁移旧形，仅存量登记读取）：<所选>/悬浮中转站/Data。
  Result := AddBackslash(NormalizeDirectory(ParentDirectory)) +
    ManagedRootLeaf + '\' + LegacyDataLeaf;
end;

function GetManagedDataParent(const DataDirectory: String): String;
begin
  Result := RemoveBackslashUnlessRoot(ExtractFilePath(
    RemoveBackslashUnlessRoot(DataDirectory)));
end;

function IsLegacyManagedDataDirectory(const DataDirectory: String): Boolean;
var
  Normalized: String;
begin
  // 两个历史形状（待迁移旧形）任一：叶子恰为 Data（父是否「悬浮中转站」中间层
  // 区分直下/嵌套，不影响「旧形」判定）。
  Result := False;
  if not IsFullyQualifiedPath(DataDirectory) then
    Exit;
  Normalized := NormalizeDirectory(DataDirectory);
  if CompareText(ExtractFileName(Normalized), LegacyDataLeaf) <> 0 then
    Exit;
  Result := True;
end;

function IsLegacyNestedDataDirectory(const DataDirectory: String): Boolean;
var
  Normalized: String;
begin
  // 旧嵌套形状判别：Data 直接父目录的叶子名恰为「悬浮中转站」中间层。
  Result := False;
  if not IsLegacyManagedDataDirectory(DataDirectory) then
    Exit;
  Normalized := NormalizeDirectory(DataDirectory);
  Result := CompareText(ExtractFileName(GetManagedDataParent(Normalized)),
    ManagedRootLeaf) = 0;
end;

function GetDataParentDirectory(const DataDirectory: String): String;
var
  DataParent: String;
begin
  // 用户所选目录（登记 DataParentDirectory 的语义，三形状同义）：受管形状剥掉
  // 「悬浮中转站」叶子；旧直下形状剥掉 Data；旧嵌套形状还要再剥掉中间层。
  DataParent := GetManagedDataParent(DataDirectory);
  if CompareText(ExtractFileName(DataParent), ManagedRootLeaf) = 0 then
    Result := RemoveBackslashUnlessRoot(ExtractFilePath(DataParent))
  else
    Result := DataParent;
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
  DataParent: String;
  ParentDirectory: String;
begin
  // 三形状判定（与应用侧 DataDirectorySettings 同构）：受管形状叶子恰为
  // 「悬浮中转站」（数据根本身）；两个旧形状（待迁移旧形）叶子恰为 Data，
  // 仅存量登记继续有效，父是否「悬浮中转站」中间层区分嵌套/直下。
  Result := False;
  if not IsFullyQualifiedPath(Value) then
    Exit;

  Normalized := NormalizeDirectory(Value);
  if CompareText(ExtractFileName(Normalized), ManagedRootLeaf) = 0 then
  begin
    // 受管形状：<所选> 不能是盘符/共享根（C:\悬浮中转站 拒绝）。
    DataParent := GetManagedDataParent(Normalized);
    if IsRootDirectory(DataParent) then
      Exit;
    Result := (DataParent <> '') and
      (CompareText(Normalized, BuildDataDirectory(DataParent)) = 0);
    Exit;
  end;

  if CompareText(ExtractFileName(Normalized), LegacyDataLeaf) <> 0 then
    Exit;

  DataParent := GetManagedDataParent(Normalized);
  if CompareText(ExtractFileName(DataParent), ManagedRootLeaf) = 0 then
  begin
    // 旧嵌套形状：所选目录（中间层的上级）不能是盘符/共享根。
    ParentDirectory := GetDataParentDirectory(Normalized);
    if IsRootDirectory(ParentDirectory) then
      Exit;
    Result := (ParentDirectory <> '') and
      (CompareText(Normalized, BuildLegacyDataDirectory(ParentDirectory)) = 0);
  end
  else
  begin
    // 旧直下形状：所选目录不能是根。
    if IsRootDirectory(DataParent) then
      Exit;
    Result := (DataParent <> '') and
      (CompareText(Normalized, BuildDirectLegacyDataDirectory(DataParent)) = 0);
  end;
end;

function GetLegacyDataDirectory: String;
begin
  // 无登记时的默认数据根（与应用侧 AppPaths.DefaultDataDirectory 同义）：
  // 以 LocalAppData 为「所选目录」的同一形状。
  Result := ExpandConstant('{localappdata}\') + ManagedRootLeaf;
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
begin
  // ExistingDataDirectory 已过双形状校验，按形状反推用户所选目录即为权威值。
  Result := GetDataParentDirectory(ExistingDataDirectory);
end;

function GetSelectedDataDirectory: String;
var
  ParentDirectory: String;
begin
  ParentDirectory := NormalizeDirectory(DataDirectoryPage.Values[0]);
  // 旧形状存量登记＋用户保持原父目录：沿用旧登记，安装器绝不自动搬迁——应用
  // 下次启动时自行归一到 <同父>/悬浮中转站（唯一受管形状）。
  if IsLegacyManagedDataDirectory(ExistingDataDirectory) and
    (CompareText(ParentDirectory, NormalizeDirectory(ExistingDataParentDirectory)) = 0) then
    Result := ExistingDataDirectory
  else
    Result := BuildDataDirectory(ParentDirectory);
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
    MsgBox('请选择内容存储文件夹。', mbError, MB_OK);
    Exit;
  end;

  if not IsFullyQualifiedPath(DataDirectoryPage.Values[0]) then
  begin
    MsgBox('内容存储文件夹必须是完整的 Windows 路径。', mbError, MB_OK);
    Exit;
  end;

  ParentDirectory := NormalizeDirectory(DataDirectoryPage.Values[0]);
  if IsRootDirectory(ParentDirectory) then
  begin
    MsgBox('内容存储文件夹不能是磁盘或网络共享根目录。', mbError, MB_OK);
    Exit;
  end;

  if not ForceDirectories(ParentDirectory) then
  begin
    MsgBox('无法创建内容存储文件夹。请选择其他位置。', mbError, MB_OK);
    Exit;
  end;

  ProbeFileName := GetUniqueProbeFileName(ParentDirectory);
  if ProbeFileName = '' then
  begin
    MsgBox('无法为内容存储文件夹创建验证文件。请选择其他位置。', mbError, MB_OK);
    Exit;
  end;
  try
    if not SaveStringToFile(ProbeFileName, '悬浮中转站', False) then
    begin
      MsgBox('内容存储文件夹不可写。请选择其他位置。', mbError, MB_OK);
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
  Normalized: String;
begin
  Result := False;
  if not IsManagedDataDirectory(DataDirectory) then
  begin
    Log('Refusing to delete an unmanaged data directory: ' + DataDirectory);
    Exit;
  end;

  Normalized := NormalizeDirectory(DataDirectory);
  Result := DelTree(Normalized, True, True, True);
  // 删除范围：受管形状只删「悬浮中转站」数据根本身，旧直下形状只删 Data——
  // 两者绝不触碰用户所选父目录（哪怕已空）；仅旧嵌套形状删除后尝试移除空的
  // 「悬浮中转站」中间层（应用创建）。
  if Result and IsLegacyNestedDataDirectory(Normalized) then
    RemoveDir(GetManagedDataParent(Normalized));
end;

function IsMissingManagedDataDirectory(const DataDirectory: String): Boolean;
var
  ErrorCode: LongWord;
begin
  Result := False;
  if GetFileAttributesW(DataDirectory) <> $FFFFFFFF then
    Exit;
  ErrorCode := GetLastError;
  Result := (ErrorCode = ErrorFileNotFound) or (ErrorCode = ErrorPathNotFound);
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
  SelectedParent: String;
  ExistingParentOfData: String;
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
    ExistingParentOfData := GetManagedDataParent(ExistingDataDirectory);
    if IsPathWithinDirectory(SelectedDataDirectory, ExistingParentOfData) then
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

  // 新形状（契约 #47）：所选目录可预先存在且含其他内容（ValidateDataParentDirectory
  // 已确保可写），暂存目录建于所选目录内；绝不移除所选目录本身。
  SelectedParent := GetManagedDataParent(SelectedDataDirectory);
  if not ForceDirectories(SelectedParent) then
    Exit;

  if DirExists(ExistingDataDirectory) then
  begin
    MigrationDirectory := GetMigrationDirectory(SelectedParent);
    if (MigrationDirectory = '') or
      not CopyDirectory(ExistingDataDirectory, MigrationDirectory) or
      not DirectoryContentsMatch(ExistingDataDirectory, MigrationDirectory) or
      not RenameFile(MigrationDirectory, SelectedDataDirectory) then
    begin
      if (MigrationDirectory <> '') and DirExists(MigrationDirectory) then
        DelTree(MigrationDirectory, True, True, True);
      Exit;
    end;
    MigrationPerformed := True;
  end
  else if not ForceDirectories(SelectedDataDirectory) then
  begin
    Exit;
  end;

  CreatedMigrationDirectory := True;
  Result := True;
end;

// 静默中止可观测（契约 #26 修订②）：PrepareToInstall 失败在返回错误串中止安装前，
// 先把原因与守卫三目录值写入安装器日志（应用侧启动安装器时带 /LOG 指向数据根
// logs 目录，两层配合使静默模式下的中止可诊断）；返回串与行为零变化。
procedure LogPrepareToInstallAbort(const Reason: String);
var
  ExistingDirectory: String;
  OwnerDirectory: String;
begin
  ReadInstallGuardDirectories(ExistingDirectory, OwnerDirectory);
  Log('PrepareToInstall aborted: ' + Reason +
    '; ExistingDirectory=' + ExistingDirectory +
    '; OwnerDirectory=' + OwnerDirectory +
    '; AppDir=' + ExpandConstant('{app}'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not ValidateInstallDirectorySelection then
  begin
    LogPrepareToInstallAbort('install directory guard rejected the selection');
    Result := '旧版本尚未启用卸载归属保护。请先保留原程序目录完成一次更新，再运行安装程序更改程序目录。内容存储位置可以照常选择。';
    Exit;
  end;
  if not PrepareDataDirectoryMigration then
  begin
    LogPrepareToInstallAbort('data directory migration failed');
    Result := '无法迁移现有内容。请确认目标文件夹可用后重试；原存储位置未作修改。';
  end;
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
    '选择图片、文字和设置的存储文件夹',
    '悬浮中转站会在所选文件夹中创建并管理「悬浮中转站」文件夹（全部数据与插件都在里面）；保持原有位置不变则不会搬迁数据。点击“下一步”继续。',
    False,
    SetupMessage(msgNewFolderName));
  DataDirectoryPage.Add('存储文件夹：');
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
  Result := False;
  if not IsCurrentInstallation then
  begin
    SuppressibleMsgBox('此卸载程序不属于当前安装位置。请使用 Windows 设置或当前程序目录中的卸载程序；当前程序、内容和登记均未修改。',
      mbError, MB_OK, IDOK);
    Exit;
  end;
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

    if (UninstallDataDirectory <> '') and not IsMissingManagedDataDirectory(UninstallDataDirectory) and
      not DeleteManagedDataDirectory(UninstallDataDirectory) then
    begin
      Log('Failed to remove managed data directory: ' + UninstallDataDirectory);
      SuppressibleMsgBox('部分内容未能删除：' + UninstallDataDirectory +
        '。内容位置登记已保留，请关闭占用文件的程序后手动清理或重新安装后卸载。', mbError, MB_OK, IDOK);
      Exit;
    end;
    RegDeleteValue(HKCU, DataRegistryKey, DataDirectoryRegistryValue);
    RegDeleteValue(HKCU, DataRegistryKey, DataParentDirectoryRegistryValue);
  end;
end;
