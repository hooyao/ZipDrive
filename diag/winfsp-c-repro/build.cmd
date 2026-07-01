@echo off
REM Build the pure-C WinFsp repro against the installed WinFsp SDK (Developer files).
REM Links the official import library winfsp-x64.lib — no FspLoad needed.
REM
REM Requires: Visual Studio with C++ tools (cl.exe). Bootstraps MSVC via vcvars64.bat.

setlocal

set "WINFSP=C:\Program Files (x86)\WinFsp"
set "VS=C:\Program Files\Microsoft Visual Studio\18\Enterprise"
set "VCVARS=%VS%\VC\Auxiliary\Build\vcvars64.bat"

if not exist "%VCVARS%" (
  echo ERROR: vcvars64.bat not found at "%VCVARS%"
  echo Edit VS= in this script to point at your Visual Studio install.
  exit /b 1
)
if not exist "%WINFSP%\inc\winfsp\winfsp.h" (
  echo ERROR: WinFsp headers not found under "%WINFSP%\inc" — install WinFsp Developer files.
  exit /b 1
)
if not exist "%WINFSP%\lib\winfsp-x64.lib" (
  echo ERROR: winfsp-x64.lib not found under "%WINFSP%\lib" — install WinFsp Developer files.
  exit /b 1
)

call "%VCVARS%" >nul
if errorlevel 1 (
  echo ERROR: vcvars64.bat failed
  exit /b 1
)

REM /MD dynamic CRT; WinFsp SDK include + import lib; advapi32 for RegGetValueW.
REM DELAYLOAD winfsp-x64.dll so the exe starts even though the DLL lives in the SxS
REM dir (not on PATH). FspLoad() in main() loads it from the registry path first,
REM so the first WinFsp call resolves cleanly. delayimp.lib provides the delay stub.
cl /nologo /W3 /O2 /MD ^
  /I "%WINFSP%\inc" ^
  repro.c ^
  /Fe:winfsp-c-repro.exe ^
  /link "%WINFSP%\lib\winfsp-x64.lib" advapi32.lib delayimp.lib ^
  /DELAYLOAD:winfsp-x64.dll

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)

echo BUILD OK: winfsp-c-repro.exe
endlocal
