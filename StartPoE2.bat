@echo off
title PoE2 Launcher

echo [1/3] Starting GameHelper (ViGEm virtual controller)...
start "" "C:\Games\Hy-v Tool\GameHelper2-main\GameHelper\bin\Release\net10.0-windows\win-x64\Launcher.exe"

echo [2/3] Waiting 6 seconds for virtual controller to connect to Slot 0...
timeout /t 6 /nobreak > nul

echo [3/3] Launching Path of Exile 2...
start "" "steam://rungameid/2694490"

echo Done! GameHelper + PoE2 started.
timeout /t 3 /nobreak > nul
