#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
output="$PWD/test-results-$(date +%Y%m%d-%H%M%S)-$$"
mkdir -p "$output"
chmod +x tests/fake-wsl.sh tests/scanimage
dotnet run --project tests/CoreTests.csproj -c Release -- "$output/core" "$PWD/tests"
pwsh -NoProfile -File tests/settings-tests.ps1 -PackageRoot "$PWD" -Output "$output/settings"
echo "Checks passed. Results: $output"
