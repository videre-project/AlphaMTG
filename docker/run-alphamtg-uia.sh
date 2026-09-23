#!/usr/bin/env bash
set -euo pipefail

export PATH="/opt/wine/bin:${PATH}"
export SystemRoot=C:\\windows
export DOTNET_ROOT='Z:\home\wine\windows-dotnet'

cd /workspace

win_dotnet=/home/wine/windows-dotnet/dotnet.exe
win_dll=$(winepath -w /workspace/AlphaMTG/bin/Debug/net10.0-windows/AlphaMTG.dll)

exec wine "$win_dotnet" "$win_dll" uia "$@"
