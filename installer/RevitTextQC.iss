; Revit Text QC - Setup.exe
;
; Installs the add-in for every Revit 2024-2027 found on the PC, for the current Windows user only
; (no administrator rights). Everything is inside the Setup: no internet is needed to install, and the
; add-in still updates itself from GitHub releases afterwards.
;
; Build (after ./build.ps1 has produced artifacts\<year>\):
;     ISCC.exe /DAppVersion=0.6.3 installer\RevitTextQC.iss        -> dist\RevitTextQC-Setup.exe

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{9D3E4A7C-5B1F-4C2E-8F6A-2B7D0C1E9A55}
AppName=Revit Text QC
AppVersion={#AppVersion}
AppVerName=Revit Text QC {#AppVersion}
AppPublisher=Revit Text QC
AppPublisherURL=https://github.com/Surajprem7/RevitTextQC
AppSupportURL=https://github.com/Surajprem7/RevitTextQC
VersionInfoVersion={#AppVersion}
; Only the small uninstaller lives here; the add-in files go to each Revit's own Addins folder.
DefaultDirName={localappdata}\RevitTextQC
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
OutputDir=..\dist
OutputBaseFilename=RevitTextQC-Setup
UninstallDisplayName=Revit Text QC
CloseApplications=no

[Messages]
FinishedLabel=Revit Text QC {#AppVersion} is installed.%n%nStart Revit and open the "Text QC" tab. The first time, Revit asks about an unsigned add-in: choose "Always Load".

[Files]
; One block per Revit version. A block is installed only when that Revit is on this PC.
Source: "..\artifacts\2024\RevitTextQC.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Flags: ignoreversion; Check: RevitInstalled('2024')
Source: "..\artifacts\2024\RevitTextQC\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\RevitTextQC"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: RevitInstalled('2024')
Source: "..\artifacts\2025\RevitTextQC.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Flags: ignoreversion; Check: RevitInstalled('2025')
Source: "..\artifacts\2025\RevitTextQC\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\RevitTextQC"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: RevitInstalled('2025')
Source: "..\artifacts\2026\RevitTextQC.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Flags: ignoreversion; Check: RevitInstalled('2026')
Source: "..\artifacts\2026\RevitTextQC\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\RevitTextQC"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: RevitInstalled('2026')
Source: "..\artifacts\2027\RevitTextQC.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2027"; Flags: ignoreversion; Check: RevitInstalled('2027')
Source: "..\artifacts\2027\RevitTextQC\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2027\RevitTextQC"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: RevitInstalled('2027')

[UninstallDelete]
; The add-in updates itself, so remove its whole folder, not only the files this Setup copied.
; Settings in %APPDATA%\RevitTextQC are kept.
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2024\RevitTextQC"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2024\RevitTextQC.addin"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2025\RevitTextQC"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\RevitTextQC.addin"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2026\RevitTextQC"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2026\RevitTextQC.addin"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2027\RevitTextQC"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2027\RevitTextQC.addin"

[Code]
// A Revit version counts as present when it is installed, or when it already has an Addins folder for this user.
function RevitInstalled(Year: String): Boolean;
begin
  Result := FileExists(GetEnv('ProgramW6432') + '\Autodesk\Revit ' + Year + '\Revit.exe')
         or FileExists(GetEnv('ProgramFiles') + '\Autodesk\Revit ' + Year + '\Revit.exe')
         or DirExists(ExpandConstant('{userappdata}') + '\Autodesk\Revit\Addins\' + Year);
end;

function RevitRunning: Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if Exec(ExpandConstant('{cmd}'), '/c tasklist /FI "IMAGENAME eq Revit.exe" | find /I "Revit.exe"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := (ResultCode = 0);
end;

function InitializeSetup: Boolean;
begin
  Result := True;

  // The add-in files cannot be replaced while Revit has them open.
  while RevitRunning do
  begin
    if WizardSilent then
    begin
      Result := False;
      Exit;
    end;
    if MsgBox('Revit is open. Close every Revit window, then click OK to continue.', mbError, MB_OKCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;

  if not (RevitInstalled('2024') or RevitInstalled('2025') or RevitInstalled('2026') or RevitInstalled('2027')) then
  begin
    if not WizardSilent then
      MsgBox('Revit 2024, 2025, 2026 or 2027 was not found on this PC, so there is nothing to install.', mbError, MB_OK);
    Result := False;
  end;
end;
