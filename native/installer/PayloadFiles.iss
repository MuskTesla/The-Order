// SPDX-License-Identifier: AGPL-3.0-or-later
// Used both by the real installer and a no-registration synthetic install probe.
function PayloadPathIsPlain(Relative: String): Boolean;
var Path, Parent: String; Attributes: Cardinal;
begin
  Result := False;
  Path := ExpandConstant('{app}') + '\' + Relative;
  while Path <> '' do begin
    Attributes := GetFileAttributesW(Path);
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then Exit;
    Parent := ExtractFileDir(Path);
    if Parent = Path then Break;
    Path := Parent;
  end;
  Result := True;
end;

function PayloadFileNeedsUpdate(Relative, ExpectedSha256: String): Boolean;
var Path: String;
begin
  if not PayloadPathIsPlain(Relative) then
    RaiseException('安装目标包含链接，未替换此文件。请检查安装目录。');
  Path := ExpandConstant('{app}') + '\' + Relative;
  Result := True;
  if FileExists(Path) then begin
    try
      Result := CompareText(GetSHA256OfFile(Path), ExpectedSha256) <> 0;
    except
      Result := True; { An unreadable file is never mistaken for an unchanged file. }
    end;
  end;
end;
