#!/usr/bin/env bash
set -euo pipefail

export PATH="/opt/wine/bin:${PATH}"
export SystemRoot=C:\\windows
export DOTNET_ROOT='Z:\home\wine\windows-dotnet'

project=/workspace/AlphaMTG/FlaUIInspector/FlaUIInspector.csproj
output=/workspace/AlphaMTG/FlaUIInspector/mtgo-ui.json
win_output=$(winepath -w "$output")

cd /workspace
echo "=== Building FlaUIInspector CLI ==="
dotnet restore "$project"
dotnet build "$project" -c Debug --no-restore

win_dotnet=/home/wine/windows-dotnet/dotnet.exe
win_dll=$(winepath -w /workspace/AlphaMTG/FlaUIInspector/bin/Debug/net8.0-windows7.0/FlaUIInspector.dll)

echo "=== Running MTGO UIA2 CLI ==="
exec wine "$win_dotnet" "$win_dll" --output "$win_output" "$@"
