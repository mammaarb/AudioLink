@echo off
rem Builds AudioLink.exe with the C# compiler that comes with Windows.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize /out:AudioLink.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll src\*.cs
if errorlevel 1 (echo Build failed. & exit /b 1)
echo Built AudioLink.exe
