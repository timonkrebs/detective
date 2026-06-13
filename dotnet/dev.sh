#!/usr/bin/env bash
# Local dev: runs the .NET API (:3334) and the Angular dev server together.
# The Angular app's proxy.conf.json already forwards /api -> :3334, so no
# frontend change is needed. Dependency-free (uses the repo's existing nx).
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"

( cd "$root/dotnet" && dotnet run --project src/Detective.Api ) &
api=$!
trap 'kill "$api" 2>/dev/null || true' EXIT

( cd "$root" && npx nx serve frontend )
