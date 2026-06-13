#!/usr/bin/env bash
# Builds and tests the cross-platform projects (everything except the WPF shell,
# which targets net8.0-windows and builds on Windows only). Safe to run on Linux/CI.
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

projects=(
  src/Detective.Core
  src/Detective.ViewModels
  src/Detective.Api
  src/Detective.Cli
  test/Detective.Tests
)

for p in "${projects[@]}"; do
  echo "==> building $p"
  dotnet build "$p" -c Release --nologo
done

echo "==> running tests"
dotnet test test/Detective.Tests -c Release --nologo

echo
echo "Cross-platform build OK."
echo "Note: src/Detective.Wpf targets net8.0-windows and is built on Windows"
echo "      (or with the official .NET SDK and EnableWindowsTargeting)."
