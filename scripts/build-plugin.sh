#!/usr/bin/env bash
set -euo pipefail

alpha_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
tracker_root=${TrackerRoot:-"${alpha_root}/../Tracker"}
plugin_output=${1:-"${alpha_root}/publish/alphamtg"}

dotnet publish "${alpha_root}/AlphaMTG/AlphaMTG.csproj" \
  -c Release \
  -p:TrackerRoot="${tracker_root}" \
  --configfile "${tracker_root}/NuGet.config" \
  -o "${plugin_output}"
