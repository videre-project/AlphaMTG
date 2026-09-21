#!/usr/bin/env bash
set -euo pipefail

project=/workspace/AlphaMTG/AlphaMTG.csproj
output=/workspace/AlphaMTG/bin/Debug/net10.0-windows

# Keep the current directory at the workspace root so BotClient's
# DotEnv.LoadFile() resolves /workspace/.env from the bind mount.
cd /workspace

# Wine's accelerated WPF composition can render as a black surface. Use the
# same software-rendering compatibility settings as Tracker.
wine reg add 'HKCU\Software\Microsoft\Avalon.Graphics' \
  /v DisableHWAcceleration /t REG_DWORD /d 1 /f >/dev/null
wine reg delete 'HKCU\Software\Wine\X11 Driver' /v UseXShm /f \
  >/dev/null 2>&1 || true
wine reg add 'HKCU\Software\Wine\X11 Driver' \
  /v UseXShm /t REG_SZ /d N /f >/dev/null

echo "=== Building AlphaMTG ==="
dotnet restore "$project"
dotnet build "$project" -c Debug --no-restore

# Run the Windows-targeted assembly with the Windows .NET runtime inside Wine.
win_dotnet=/home/wine/windows-dotnet/dotnet.exe
win_output=$(winepath -w "$output")
win_dll="${win_output}\\AlphaMTG.dll"

# Wine sets WINDIR but not SystemRoot; .NET and MTGOSDK expect both.
export SystemRoot=C:\\windows
export DOTNET_ROOT='Z:\home\wine\windows-dotnet'

echo "=== Starting AlphaMTG ==="
exec wine "$win_dotnet" "$win_dll"
