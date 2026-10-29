@echo off
rem Builds bin\HebrewDictate.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No SDK or installs needed.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" /nologo /target:winexe /codepage:65001 /out:"%~dp0bin\HebrewDictate.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0src\Dictate.cs"
if errorlevel 1 (echo Build failed. & exit /b 1) else (echo Built bin\HebrewDictate.exe)

