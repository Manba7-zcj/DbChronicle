@echo off
setlocal
set "VSIX=%~dp0XmlValueIntelliSense.vsix"
if not exist "%VSIX%" (
    echo XmlValueIntelliSense.vsix not found next to this script.
    pause
    exit /b 1
)

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo vswhere.exe not found. Install Visual Studio 2022 first.
    pause
    exit /b 1
)

set "VSPATH="
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -version "[17.0,18.0)" -property installationPath`) do set "VSPATH=%%i"
if not defined VSPATH (
    echo Visual Studio 2022 not found.
    pause
    exit /b 1
)

echo Installing XML Value IntelliSense to %VSPATH%
"%VSPATH%\Common7\IDE\VSIXInstaller.exe" /quiet "%VSIX%"
if errorlevel 1 (
    echo Installation failed. Close all Visual Studio instances and try again.
    pause
    exit /b 1
)

echo Installed. Restart Visual Studio 2022.
pause
