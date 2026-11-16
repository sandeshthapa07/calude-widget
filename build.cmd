@echo off
rem Builds Claude Widget with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No Visual Studio or SDK needed. Output goes to .\bin
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set CSC="%FW%\csc.exe" /nologo /optimize+ /warn:4
set SRC=%~dp0src
set OUT=%~dp0bin
set ICON=%~dp0assets\app.ico
if not exist "%OUT%" mkdir "%OUT%"

set WPF=/r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Drawing.dll"

echo Building ClaudeWidget.exe
%CSC% /target:winexe /win32icon:"%ICON%" /resource:"%ICON%",ClaudeWidget.app.ico /out:"%OUT%\ClaudeWidget.exe" %WPF% "%SRC%\Core.cs" "%SRC%\Widget.cs" || exit /b 1

echo Building claude-widget-hook.exe
%CSC% /target:winexe /win32icon:"%ICON%" /out:"%OUT%\claude-widget-hook.exe" "%SRC%\Core.cs" "%SRC%\Hook.cs" || exit /b 1

echo Building claude-widget-cli.exe
%CSC% /target:exe /win32icon:"%ICON%" /out:"%OUT%\claude-widget-cli.exe" "%SRC%\Core.cs" "%SRC%\Hook.cs" || exit /b 1

echo Done: %OUT%
