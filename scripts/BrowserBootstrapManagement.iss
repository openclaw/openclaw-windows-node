; Shared by installer AND uninstaller. Fixed argv, bounded stdin/stdout, no shell or JSON command-line interpolation.
; Native process containment and EOF completion are distinct from Chrome's EOF revocation.
function BBCreatePipe(var R, W: NativeInt; SA: AnsiString; Size: Cardinal): Boolean;
  external 'CreatePipe@kernel32.dll stdcall';
function BBHandleFlags(H: NativeInt; Mask, Flags: Cardinal): Boolean;
  external 'SetHandleInformation@kernel32.dll stdcall';
function BBClose(H: NativeInt): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function BBCreateProcess(App: String; Cmd: String; ProcessSA, ThreadSA: NativeInt; Inherit: Boolean;
  Flags: Cardinal; Env: NativeInt; Directory: String; Startup, ProcessInfo: AnsiString): Boolean;
  external 'CreateProcessW@kernel32.dll stdcall';
function BBWrite(H: NativeInt; Data: AnsiString; Count: Cardinal; var Written: Cardinal; Overlapped: NativeInt): Boolean;
  external 'WriteFile@kernel32.dll stdcall';
function BBRead(H: NativeInt; Data: AnsiString; Count: Cardinal; var ReadCount: Cardinal; Overlapped: NativeInt): Boolean;
  external 'ReadFile@kernel32.dll stdcall';
function BBPeek(H: NativeInt; Buffer: NativeInt; BufferSize: Cardinal; ReadBytes: NativeInt; var Available: Cardinal; LeftBytes: NativeInt): Boolean;
  external 'PeekNamedPipe@kernel32.dll stdcall';
function BBWait(H: NativeInt; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function BBExit(H: NativeInt; var Code: Cardinal): Boolean;
  external 'GetExitCodeProcess@kernel32.dll stdcall';
function BBResume(H: NativeInt): Cardinal;
  external 'ResumeThread@kernel32.dll stdcall';
function BBTerminate(H: NativeInt; Code: Cardinal): Boolean;
  external 'TerminateProcess@kernel32.dll stdcall';
function BBJob(Security, Name: NativeInt): NativeInt;
  external 'CreateJobObjectW@kernel32.dll stdcall';
function BBJobLimits(Job: NativeInt; InfoClass: Integer; Info: AnsiString; Size: Cardinal): Boolean;
  external 'SetInformationJobObject@kernel32.dll stdcall';
function BBAssign(Job, Process: NativeInt): Boolean;
  external 'AssignProcessToJobObject@kernel32.dll stdcall';
function BBTick: Int64;
  external 'GetTickCount64@kernel32.dll stdcall';
function BBError: Cardinal;
  external 'GetLastError@kernel32.dll stdcall';

procedure BBPut(var Buffer: AnsiString; Offset, Bytes: Integer; Value: Int64);
var I: Integer;
begin
  for I := 0 to Bytes - 1 do begin Buffer[Offset + I + 1] := Chr(Value and 255); Value := Value shr 8; end;
end;

function BBGet(Buffer: AnsiString; Offset, Bytes: Integer): Int64;
var I: Integer;
begin
  Result := 0;
  for I := Bytes - 1 downto 0 do Result := (Result shl 8) or Ord(Buffer[Offset + I + 1]);
end;

procedure BBDispose(var H: NativeInt);
begin
  if H <> 0 then begin BBClose(H); H := 0; end;
end;

function BBDrain(H: NativeInt; Limit: Integer; var Data: AnsiString; var Eof: Boolean): Boolean;
var Available, ReadCount: Cardinal; Chunk: AnsiString; Count: Integer;
begin
  Result := False;
  if Eof then begin Result := True; Exit; end;
  if not BBPeek(H, 0, 0, 0, Available, 0) then begin
    Eof := BBError = 109;
    Result := Eof; Exit;
  end;
  if Available > Cardinal(Limit - Length(Data)) then Exit;
  if Available > 0 then begin
    Count := Available; if Count > 4096 then Count := 4096;
    Chunk := StringOfChar(#0, Count);
    if not BBRead(H, Chunk, Count, ReadCount, 0) then Exit;
    if ReadCount > Cardinal(Count) then Exit;
    Data := Data + Copy(Chunk, 1, ReadCount);
  end;
  Result := True;
end;

procedure BBExpect(S: String; var P: Integer; C: Char);
begin
  if (P > Length(S)) or (S[P] <> C) then RaiseException('Invalid management receipt.');
  P := P + 1;
end;

function BBHex(S: String; var P: Integer): Integer;
var I, V: Integer; C: Char;
begin
  Result := 0;
  for I := 1 to 4 do begin
    if P > Length(S) then RaiseException('Invalid management receipt.');
    C := S[P]; P := P + 1;
    if (C >= '0') and (C <= '9') then V := Ord(C)-Ord('0')
    else if (C >= 'a') and (C <= 'f') then V := Ord(C)-Ord('a')+10
    else if (C >= 'A') and (C <= 'F') then V := Ord(C)-Ord('A')+10
    else begin RaiseException('Invalid management receipt.'); V := 0; end;
    Result := Result * 16 + V;
  end;
end;

function BBString(S: String; var P: Integer): String;
var C: Char; V, Low: Integer;
begin
  Result := ''; BBExpect(S, P, '"');
  while P <= Length(S) do begin
    C := S[P]; P := P + 1;
    if C = '"' then Exit;
    if Ord(C) < 32 then RaiseException('Invalid management receipt.');
    if C = '\' then begin
      if P > Length(S) then RaiseException('Invalid management receipt.');
      C := S[P]; P := P + 1;
      case C of
        '"', '\', '/': Result := Result + C;
        'b': Result := Result + #8;
        'f': Result := Result + #12;
        'n': Result := Result + #10;
        'r': Result := Result + #13;
        't': Result := Result + #9;
        'u': begin
          V := BBHex(S, P);
          if (V >= $DC00) and (V <= $DFFF) then RaiseException('Invalid management receipt.');
          Result := Result + Chr(V);
          if (V >= $D800) and (V <= $DBFF) then begin
            BBExpect(S, P, '\'); BBExpect(S, P, 'u'); Low := BBHex(S, P);
            if (Low < $DC00) or (Low > $DFFF) then RaiseException('Invalid management receipt.');
            Result := Result + Chr(Low);
          end;
        end;
      else RaiseException('Invalid management receipt.');
      end;
    end else Result := Result + C;
  end;
  RaiseException('Invalid management receipt.');
end;

procedure BBLiteral(S: String; var P: Integer; Value: String);
begin
  if Copy(S, P, Length(Value)) <> Value then RaiseException('Invalid management receipt.');
  P := P + Length(Value);
end;

function BBNullable(S: String; var P: Integer): String;
begin
  if Copy(S, P, 4) = 'null' then begin BBLiteral(S, P, 'null'); Result := '#null'; end
  else Result := BBString(S, P);
end;

function BBGuid(S: String): Boolean;
var I: Integer;
begin
  Result := False;
  if (Length(S) <> 36) or (S = '00000000-0000-0000-0000-000000000000') then Exit;
  for I := 1 to 36 do begin
    if (I = 9) or (I = 14) or (I = 19) or (I = 24) then begin if S[I] <> '-' then Exit; end
    else if not (((S[I] >= '0') and (S[I] <= '9')) or ((S[I] >= 'a') and (S[I] <= 'f'))) then Exit;
  end;
  Result := True;
end;

function BBDescriptor(S: String; var P: Integer): Boolean;
var Key, G, M, E, B, R, Root: String; Seen, Count: Integer;
begin
  Result := False; BBExpect(S, P, '{'); Seen := 0; Count := 0;
  repeat
    if Count > 0 then BBExpect(S, P, ','); Key := BBString(S, P); BBExpect(S, P, ':');
    if Key = 'generation' then begin if (Seen and 1) <> 0 then RaiseException('Duplicate receipt field.'); Seen := Seen or 1; G := BBString(S, P); end
    else if Key = 'manifestPath' then begin if (Seen and 2) <> 0 then RaiseException('Duplicate receipt field.'); Seen := Seen or 2; M := BBString(S, P); end
    else if Key = 'launcherPath' then begin if (Seen and 4) <> 0 then RaiseException('Duplicate receipt field.'); Seen := Seen or 4; E := BBString(S, P); end
    else if Key = 'bindingPath' then begin if (Seen and 8) <> 0 then RaiseException('Duplicate receipt field.'); Seen := Seen or 8; B := BBString(S, P); end
    else if Key = 'receiptPath' then begin if (Seen and 16) <> 0 then RaiseException('Duplicate receipt field.'); Seen := Seen or 16; R := BBString(S, P); end
    else RaiseException('Unknown receipt field.');
    Count := Count + 1;
    if Count > 5 then RaiseException('Invalid management receipt.');
  until (P <= Length(S)) and (S[P] = '}');
  BBExpect(S, P, '}');
  if (Seen <> 31) or not BBGuid(G) then Exit;
  Root := ExpandConstant('{localappdata}') + '\OpenClawTray\browser-native\generations\' + G + '\';
  Result := (M = Root + 'ai.openclaw.browser_bootstrap.json') and (E = Root + 'OpenClaw.BrowserBootstrap.exe') and
    (B = Root + 'OpenClaw.BrowserBootstrap.binding.json') and (R = Root + 'OpenClaw.BrowserBootstrap.owned.json');
end;

function BBReceipt(Bytes: AnsiString; ExitCode: Cardinal; Action, Store: String): Boolean;
var S, Key, Code, Registration, Mode, Inventory: String; P, Seen, Count, Bit: Integer; Ok, HasDescriptor: Boolean;
begin
  Result := False;
  try
    if (Length(Bytes) < 2) or (Length(Bytes) > 32768) or (Bytes[Length(Bytes)] <> #10) then Exit;
    S := Utf8Decode(Bytes); if Utf8Encode(S) <> Bytes then Exit;
    P := 1; BBExpect(S, P, '{'); Seen := 0; Count := 0; Ok := False; HasDescriptor := False;
    repeat
      if Count > 0 then BBExpect(S, P, ','); Key := BBString(S, P); BBExpect(S, P, ':'); Bit := 0;
      if Key = 'v' then begin Bit := 1; BBLiteral(S, P, '1'); end
      else if Key = 'ok' then begin Bit := 2; Ok := Copy(S, P, 4) = 'true'; if Ok then BBLiteral(S, P, 'true') else BBLiteral(S, P, 'false'); end
      else if Key = 'code' then begin Bit := 4; Code := BBString(S, P); end
      else if Key = 'registration' then begin Bit := 8; Registration := BBNullable(S, P); end
      else if Key = 'mode' then begin Bit := 16; Mode := BBNullable(S, P); end
      else if Key = 'store' then begin Bit := 32; Inventory := BBNullable(S, P); end
      else if Key = 'installation' then begin
        Bit := 64;
        if Copy(S, P, 4) = 'null' then BBLiteral(S, P, 'null')
        else begin HasDescriptor := BBDescriptor(S, P); if not HasDescriptor then Exit; end;
      end else Exit;
      if (Seen and Bit) <> 0 then Exit; Seen := Seen or Bit; Count := Count + 1;
      if Count > 7 then Exit;
    until (P <= Length(S)) and (S[P] = '}');
    BBExpect(S, P, '}'); BBExpect(S, P, #10);
    if (Seen <> 127) or (P <> Length(S) + 1) then Exit;
    if (Inventory <> '#null') and (Inventory <> 'missing') and (Inventory <> 'requested') and (Inventory <> 'foreign') and (Inventory <> 'invalid') then Exit;
    if not Ok or (Code <> 'ok') or (ExitCode <> 0) then Exit; { No failure or uncertain receipt becomes success. }
    if Action = 'install' then Result := (Registration = 'owned') and (Mode = 'companion-managed-wsl') and HasDescriptor and ((Store = 'preserve') or (Inventory = 'requested'))
    else Result := (Action = 'uninstall') and (Registration = 'missing') and (Mode = '#null') and not HasDescriptor and (Inventory = 'missing');
  except
    Result := False; { Never log private receipt data or parser exceptions. }
  end;
end;

function RunBrowserManagement(Action, Store: String): Boolean;
var
  InR, InW, OutR, OutW, ErrR, ErrW, Job, ProcessH, ThreadH: NativeInt;
  PointerSize, StartupSize, SABytes, InfoBytes: Integer;
  SA, Startup, PI, Limits, Input, Output, Errors: AnsiString;
  Written, ExitCode: Cardinal;
  Started: Int64;
  Exe, Cmd: String;
  OutEof, ErrEof, Done: Boolean;
begin
  Result := False;
  InR := 0; InW := 0; OutR := 0; OutW := 0; ErrR := 0; ErrW := 0; Job := 0; ProcessH := 0; ThreadH := 0;
  if not (((Action = 'install') and ((Store = 'preserve') or (Store = 'request'))) or ((Action = 'uninstall') and (Store = 'remove'))) then Exit;
  Exe := ExpandConstant('{app}\tools\browser-bootstrap\OpenClaw.BrowserBootstrap.exe');
  if not FileExists(Exe) then Exit;
  Input := '{"v":1,"action":"' + Action + '","mode":"companion-managed-wsl","context":null,"expectedOrigins":["chrome-extension://kcdjddhmeafeomebliikmbpblkmkfoig/"],"store":"' + Store + '"}';
  PointerSize := SizeOf(ProcessH); StartupSize := 68; SABytes := 12; InfoBytes := 112;
  if PointerSize = 8 then begin StartupSize := 104; SABytes := 24; InfoBytes := 144; end;
  if (PointerSize <> 4) and (PointerSize <> 8) then Exit;
  SA := StringOfChar(#0, SABytes); BBPut(SA, 0, 4, SABytes); BBPut(SA, PointerSize * 2, 4, 1);
  Startup := StringOfChar(#0, StartupSize); PI := StringOfChar(#0, PointerSize * 2 + 8);
  Limits := StringOfChar(#0, InfoBytes); BBPut(Limits, 16, 4, $2000); { KILL_ON_JOB_CLOSE }
  Started := BBTick;
  try
    if not BBCreatePipe(InR, InW, SA, 4096) or not BBCreatePipe(OutR, OutW, SA, 4096) or not BBCreatePipe(ErrR, ErrW, SA, 4096) then Exit;
    if not BBHandleFlags(InW, 1, 0) or not BBHandleFlags(OutR, 1, 0) or not BBHandleFlags(ErrR, 1, 0) then Exit;
    Job := BBJob(0, 0); if (Job = 0) or not BBJobLimits(Job, 9, Limits, InfoBytes) then Exit;
    BBPut(Startup, 0, 4, StartupSize);
    if PointerSize = 4 then begin
      BBPut(Startup, 44, 4, $100); BBPut(Startup, 56, 4, InR); BBPut(Startup, 60, 4, OutW); BBPut(Startup, 64, 4, ErrW);
    end else begin
      BBPut(Startup, 60, 4, $100); BBPut(Startup, 80, 8, InR); BBPut(Startup, 88, 8, OutW); BBPut(Startup, 96, 8, ErrW);
    end;
    Cmd := '"' + Exe + '" --manage';
    if not BBCreateProcess(Exe, Cmd, 0, 0, True, $08000004, 0, ExpandConstant('{app}'), Startup, PI) then Exit;
    ProcessH := BBGet(PI, 0, PointerSize); ThreadH := BBGet(PI, PointerSize, PointerSize);
    if not BBAssign(Job, ProcessH) then begin BBTerminate(ProcessH, 1); Exit; end;
    if BBResume(ThreadH) = $FFFFFFFF then Exit;
    BBDispose(InR); BBDispose(OutW); BBDispose(ErrW); BBDispose(ThreadH);
    if not BBWrite(InW, Input, Length(Input), Written, 0) or (Written <> Cardinal(Length(Input))) then Exit;
    BBDispose(InW); { Management request completion requires EOF. }
    OutEof := False; ErrEof := False; Output := ''; Errors := ''; Done := False;
    while BBTick - Started < 60000 do begin
      if not BBDrain(OutR, 32768, Output, OutEof) or not BBDrain(ErrR, 0, Errors, ErrEof) then Exit;
      Done := BBWait(ProcessH, 0) = 0;
      if Done and OutEof and ErrEof then begin
        if BBExit(ProcessH, ExitCode) then Result := BBReceipt(Output, ExitCode, Action, Store);
        Exit;
      end;
      Sleep(10);
    end;
  finally
    BBDispose(Job); { Timeout/failure revokes and terminates the entire owned process tree. }
    if ProcessH <> 0 then BBWait(ProcessH, 2000);
    BBDispose(InR); BBDispose(InW); BBDispose(OutR); BBDispose(OutW); BBDispose(ErrR); BBDispose(ErrW);
    BBDispose(ThreadH); BBDispose(ProcessH);
  end;
end;
