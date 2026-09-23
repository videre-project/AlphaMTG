#!/usr/bin/env bash
set -euo pipefail

inspect_dir=/opt/FlaUInspect
export SystemRoot=C:\\windows

echo "=== Starting FlaUIInspect UIA2 ==="
cd "$inspect_dir"
# Use the published Windows apphost rather than invoking the DLL through
# dotnet.exe. Under Wine, the apphost initializes the WPF UI correctly while
# the DLL launch can leave FlaUInspect with a blank client area.
exec wine "$inspect_dir/FlaUInspect.UIA2.exe"
