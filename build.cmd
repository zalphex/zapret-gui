@echo off
setlocal
cd /d "%~dp0"
msbuild zapretgui.csproj /nologo /t:Rebuild /p:Configuration=Release /p:Platform=x64 || exit /b 1
msbuild Tests\CoreChecks.csproj /nologo /t:Rebuild /p:Configuration=Release /p:Platform=x64 || exit /b 1
Tests\bin\CoreChecks.exe || exit /b 1
where makensis >nul 2>nul
if errorlevel 1 (
  "%ProgramFiles(x86)%\NSIS\makensis.exe" Setup\Installer.nsi
) else (
  makensis Setup\Installer.nsi
)
exit /b %errorlevel%
