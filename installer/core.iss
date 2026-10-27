; SS per-user installer core. Payload is staged at ../publish/app/ during build.
; The WPF bootstrapper creates a private session and passes /SSSESSION and /SSID.
; This script never places model data under {app}, deletes external data, restarts
; Windows or terminates other applications. The bootstrapper owns postchecks.
#define AppVersion "4.0.0-beta.1"
[Setup]
AppId={{8DE2C08B-9FC4-4D2C-B563-DF66B79395EA}
AppName=SS本地模型管理器
AppVersion={#AppVersion}
AppPublisher=SS
DefaultDirName={localappdata}\Programs\SSLocalModelManager
DefaultGroupName=SS本地模型管理器
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\build
OutputBaseFilename=SS.CoreSetup
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
DisableStartupPrompt=yes
DirExistsWarning=no
CloseApplications=no
RestartIfNeededByRun=no
UninstallDisplayName=SS本地模型管理器
UninstallDisplayIcon={app}\SSLocalModelManager.exe
UninstallFilesDir={app}\_uninstall
#ifdef SS_TEST
CreateUninstallRegKey=no
#endif

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\SS本地模型管理器"; Filename: "{app}\SSLocalModelManager.exe"; WorkingDir: "{app}"

[Code]
var
  EventDir, SessionId: String;
  Sequence: Integer;

function ValidSessionId(const S: String): Boolean;
var I: Integer;
begin
  Result := Length(S) = 32;
  if not Result then exit;
  for I := 1 to Length(S) do
    if not (((S[I] >= '0') and (S[I] <= '9')) or ((S[I] >= 'a') and (S[I] <= 'f'))) then begin
      Result := False; exit;
    end;
end;

procedure Emit(const Stage: String; Done, Total: Integer);
var Line: String;
begin
  if (EventDir = '') or (not ValidSessionId(SessionId)) or (Total <= 0) then exit;
  Sequence := Sequence + 1;
  Line := Format('{"schemaVersion":1,"sessionId":"%s","seq":%d,"stage":"%s","completed":%d,"total":%d}', [SessionId, Sequence, Stage, Done, Total]) + #13#10;
  SaveStringToFile(AddBackslash(EventDir) + 'events.jsonl', Line, True);
end;

function InitializeSetup: Boolean;
begin
  EventDir := ExpandConstant('{param:SSSESSION|}');
  SessionId := ExpandConstant('{param:SSID|}');
  Sequence := 0;
  Result := ValidSessionId(SessionId) and (EventDir <> '') and DirExists(EventDir);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var FindRec: TFindRec; AppPath: String; Occupied: Boolean;
begin
  Result := '';
  AppPath := ExpandConstant('{app}');
  Occupied := False;
  if DirExists(AppPath) and FindFirst(AddBackslash(AppPath) + '*', FindRec) then begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then Occupied := True;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  if Occupied then Result := '目标目录不为空；为保护现有文件，请选择新的空目录。';
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  if (MaxProgress > 0) and (CurProgress >= 0) and (CurProgress <= MaxProgress) then
    Emit('deploy', CurProgress, MaxProgress);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then Emit('installed', 1, 1);
end;
