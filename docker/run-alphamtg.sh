#!/usr/bin/env bash
set -euo pipefail

project=/workspace/AlphaMTG/AlphaMTG.csproj
nuget_config=/workspace/AlphaMTG/NuGet.config
output=/workspace/AlphaMTG/bin/Debug/net10.0-windows
alphamtg_pid=

cleanup() {
  if [[ -n "$alphamtg_pid" ]]; then
    kill "$alphamtg_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT

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

# Run the Windows-targeted assembly with the Windows .NET runtime inside Wine.
win_dotnet=/home/wine/windows-dotnet/dotnet.exe
win_output=$(winepath -w "$output")
win_dll="${win_output}\\AlphaMTG.dll"

# Wine sets WINDIR but not SystemRoot; .NET and MTGOSDK expect both.
export SystemRoot=C:\\windows
export DOTNET_ROOT='Z:\home\wine\windows-dotnet'

echo "=== Starting AlphaMTG ==="
wine "$win_dotnet" "$win_dll" &
alphamtg_pid=$!

wait "$alphamtg_pid"
