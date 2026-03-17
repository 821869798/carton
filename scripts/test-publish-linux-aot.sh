#!/usr/bin/env bash
set -euo pipefail

# Usage: scripts/publish-linux-aot.sh [rid] [configuration]
# Example: scripts/publish-linux-aot.sh linux-x64 Release

RID="${1:-linux-x64}"
CONFIG="${2:-Release}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
PROJECT="${REPO_ROOT}/src/carton.GUI/carton.GUI.csproj"
OUTPUT="${REPO_ROOT}/artifacts/publish/${RID}"

echo "Publishing ${PROJECT} as ${RID} (${CONFIG}) with NativeAOT..."
pushd "${REPO_ROOT}" >/dev/null

if ! dotnet publish "${PROJECT}" \
  -c "${CONFIG}" \
  -r "${RID}" \
  -o "${OUTPUT}" \
  /p:PublishAot=true \
  /p:SelfContained=true \
  /p:StripSymbols=true \
  /p:IncludeNativeLibrariesForSelfExtract=true \
  /p:EnableCompressionInSingleFile=true \
  /p:InvariantGlobalization=true; then
  echo "NativeAOT publish failed."
  popd >/dev/null
  exit 1
fi

popd >/dev/null
echo "Output written to ${OUTPUT}"
