@echo off
rem ==== Run Dynamic Island with one click ====
rem The running instance is closed cleanly first, then the app is rebuilt and started.
rem This window closes itself once the app is running; the app keeps running.
cd /d "%~dp0"
title Dynamic Island

rem Create a "Dynamic Island" shortcut on the Desktop (once), so next time you can start it from there
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p=[Environment]::GetFolderPath('Desktop')+'\Dynamic Island.lnk'; if (-not (Test-Path $p)) { $s=(New-Object -ComObject WScript.Shell).CreateShortcut($p); $s.TargetPath='%~f0'; $s.WorkingDirectory='%~dp0'; $s.WindowStyle=7; $s.IconLocation='%SystemRoot%\System32\imageres.dll,-1023'; $s.Description='Run Dynamic Island'; $s.Save() }"

rem Ask the old instance to exit cleanly (so the .exe isn't locked during the build)
powershell -NoProfile -Command "try { $e=[System.Threading.EventWaitHandle]::OpenExisting('DynamicIsland.Quit'); [void]$e.Set(); Start-Sleep -Milliseconds 1500 } catch {}"

rem Build first, then start the app separately from this window.
rem (With "dotnet run", closing this window would also close the app.)
dotnet build -nologo -v q
if errorlevel 1 (
  echo.
  echo Build failed. See the error messages above.
  pause
  exit /b 1
)
start "" /D "%~dp0" "%~dp0bin\Debug\net8.0-windows10.0.19041.0\DynamicIsland.exe"
exit /b 0
