#!/usr/bin/env bash
set -euo pipefail

alpha_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
plugin_output=${ALPHAMTG_PLUGIN_OUTPUT:-"${alpha_root}/publish/alphamtg"}

dotnet publish "${alpha_root}/AlphaMTG/AlphaMTG.csproj" \
  -c Release \
  --configfile "${alpha_root}/AlphaMTG/NuGet.config" \
  -o "${plugin_output}" \
  "$@"
