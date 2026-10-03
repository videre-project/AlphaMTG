#!/usr/bin/env bash
set -euo pipefail

project=/workspace/AlphaMTG/AlphaMTG.csproj
nuget_config=/workspace/AlphaMTG/NuGet.config
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
# The local SDK feed can contain a rebuilt package with the same MinVer
# version. Clear restore metadata so NuGet does not reuse a stale package
# hash from the previous container iteration.
rm -rf "$(dirname "$project")/obj"
# Same-version SDK rebuilds also leave a stale extracted package in the
# container-local NuGet cache. Remove only the MTGOSDK entries so the next
# restore reinstalls them from the mounted local feed.
nuget_packages="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
rm -rf "$nuget_packages/mtgosdk" \
  "$nuget_packages/mtgosdk.msbuild" \
  "$nuget_packages/mtgosdk.win32"
dotnet restore "$project" --configfile "$nuget_config" --force-evaluate --no-cache
dotnet build "$project" -c Debug --no-restore

# AlphaMTG is a stateful server: this entrypoint builds it, then runs it
# in the foreground. It serves HTTP (ALPHAMTG_HTTP_PORT, default 8080)
# and, when stdin is piped (docker exec -i), MCP over stdio.
echo "=== Starting AlphaMTG ==="
win_dotnet=/home/wine/windows-dotnet/dotnet.exe
win_output=$(winepath -w "$output")
win_dll="${win_output}\\AlphaMTG.dll"

# Wine sets WINDIR but not SystemRoot; .NET and MTGOSDK expect both.
export SystemRoot=C:\\windows
export DOTNET_ROOT='Z:\home\wine\windows-dotnet'
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

exec wine "$win_dotnet" "$win_dll"
