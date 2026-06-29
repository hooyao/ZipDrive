@echo off
rem ─────────────────────────────────────────────────────────────────────────
rem  Native AOT publish for ZipDrive.
rem
rem  Locates a Visual Studio install with the "Desktop development with C++"
rem  workload via vswhere, sources vcvars64 (so link.exe is on PATH), and puts
rem  the VS Installer dir on PATH (the ILC link target shells out to vswhere.exe).
rem  Produces a single native ZipDrive.exe in publish\ — no .NET runtime needed
rem  on the target, but WinFsp must be installed (https://winfsp.dev/rel/).
rem
rem  Usage:  build-aot.bat            (defaults to win-x64)
rem          build-aot.bat win-arm64
rem ─────────────────────────────────────────────────────────────────────────
setlocal

set "rid=%~1"
if "%rid%"=="" set "rid=win-x64"

set "vswhere=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%vswhere%" (
    echo error: vswhere.exe not found at "%vswhere%".
    echo install/repair the Visual Studio Installer from https://aka.ms/vs/install
    exit /b 1
)

set "vsInstall="
for /f "usebackq tokens=*" %%i in (`"%vswhere%" -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "vsInstall=%%i"

if not defined vsInstall (
    echo error: no Visual Studio install with the C++ Tools workload found.
    echo install "Desktop development with C++" via the Visual Studio Installer.
    exit /b 1
)

set "vcvars=%vsInstall%\VC\Auxiliary\Build\vcvars64.bat"
if not exist "%vcvars%" (
    echo error: vcvars64.bat not found at "%vcvars%".
    echo the C++ Tools workload appears incomplete; repair via the VS Installer.
    exit /b 1
)

rem Put the Installer dir first so the ILC link target can find vswhere.exe.
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"

call "%vcvars%" >nul
if errorlevel 1 exit /b %errorlevel%

dotnet publish "%~dp0src\ZipDrive.Cli\ZipDrive.Cli.csproj" -c Release -r %rid% -o "%~dp0publish"
exit /b %errorlevel%
