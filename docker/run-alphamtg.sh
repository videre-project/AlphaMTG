#!/usr/bin/env bash
set -euo pipefail

project=/workspace/AlphaMTG/AlphaMTG.csproj
output=/workspace/AlphaMTG/bin/Debug/net10.0-windows
alphamtg_pid=
flauinspect_launcher_pid=

cleanup() {
  for pid in "$flauinspect_launcher_pid" "$alphamtg_pid"; do
    if [[ -n "$pid" ]]; then
      kill "$pid" 2>/dev/null || true
    fi
  done
}
trap cleanup EXIT

wait_for_mtgo() {
  local timeout=${FLAUINSPECT_MTGO_WAIT_TIMEOUT:-120}

  echo "Waiting for MTGO before starting FlaUIInspect..."
  for ((seconds = 0; seconds < timeout; seconds++)); do
    if ps -eo args= | grep -q '[M]TGO.exe'; then
      return 0
    fi
    sleep 1
  done

  echo "MTGO did not appear within ${timeout}s; starting FlaUIInspect anyway."
}

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
wine "$win_dotnet" "$win_dll" &
alphamtg_pid=$!

if [[ "${FLAUINSPECT_AUTOSTART:-1}" =~ ^(1|true|yes|on)$ ]]; then
  (
    wait_for_mtgo
    sleep "${FLAUINSPECT_START_DELAY:-10}"
    if kill -0 "$alphamtg_pid" 2>/dev/null; then
      /usr/local/bin/run-flauinspect
    fi
  ) &
  flauinspect_launcher_pid=$!
fi

wait "$alphamtg_pid"
