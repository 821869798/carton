#!/usr/bin/env bash
set -euo pipefail

# Usage: scripts/test-publish-linux-aot.sh [rid] [configuration] [output] [build_macro]
# Examples:
#   scripts/test-publish-linux-aot.sh linux-x64 Release
#   scripts/test-publish-linux-aot.sh linux-x64 Release artifacts/publish/linux-x64-appimage INSTALLER_BUILD

RID="${1:-linux-x64}"
CONFIG="${2:-Release}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
PROJECT="${REPO_ROOT}/src/carton.GUI/carton.GUI.csproj"
HELPER_PROJECT="${REPO_ROOT}/src/carton.Helper"
OUTPUT="${3:-${REPO_ROOT}/artifacts/publish/${RID}}"
HELPER_OUTPUT="${REPO_ROOT}/artifacts/publish/${RID}-helper"
BUILD_MACRO="${4:-}"
INCLUDE_KERNEL_SCRIPT="${SCRIPT_DIR}/include-singbox-kernel.sh"

cargo_target_for_rid() {
  case "$RID" in
    linux-x64) printf '%s' "x86_64-unknown-linux-gnu" ;;
    linux-arm64) printf '%s' "aarch64-unknown-linux-gnu" ;;
    *)
      echo "Unsupported helper RID: $RID" >&2
      exit 1
      ;;
  esac
}

prepare_cargo_target() {
  local target="$1"
  if command -v rustup >/dev/null 2>&1; then
    rustup target add "$target"
  fi

  if [[ "$target" == "aarch64-unknown-linux-gnu" ]]; then
    export CARGO_TARGET_AARCH64_UNKNOWN_LINUX_GNU_LINKER="${CARGO_TARGET_AARCH64_UNKNOWN_LINUX_GNU_LINKER:-aarch64-linux-gnu-gcc}"
  fi
}

echo "Publishing ${PROJECT} as ${RID} (${CONFIG}) with NativeAOT..."
pushd "${REPO_ROOT}" >/dev/null

if ! command -v cargo >/dev/null 2>&1; then
  echo "cargo not found. Install Rust toolchain before building carton-helper." >&2
  popd >/dev/null
  exit 1
fi

props=(
  -c "${CONFIG}"
  -r "${RID}"
  -o "${OUTPUT}"
  /p:PublishAot=true
  /p:SelfContained=true
  /p:StripSymbols=true
  /p:DebugSymbols=false
  /p:DebugType=None
  /p:IncludeNativeLibrariesForSelfExtract=true
  /p:EnableCompressionInSingleFile=true
  /p:InvariantGlobalization=true
)

if [[ "$RID" == "linux-arm64" ]]; then
  props+=(/p:ObjCopyName=aarch64-linux-gnu-objcopy)
fi

if [[ -n "$BUILD_MACRO" ]]; then
  props+=(/p:CartonBuildMacro="$BUILD_MACRO")
fi

if ! dotnet publish "${PROJECT}" "${props[@]}"; then
  echo "NativeAOT publish failed."
  popd >/dev/null
  exit 1
fi

if [[ ! -f "${HELPER_PROJECT}/Cargo.toml" ]]; then
  echo "Helper project not found: ${HELPER_PROJECT}" >&2
  popd >/dev/null
  exit 1
fi

helper_target="$(cargo_target_for_rid)"
prepare_cargo_target "$helper_target"
mkdir -p "$HELPER_OUTPUT"
echo "Building carton-helper as ${RID} (${CONFIG})..."
if ! cargo build --manifest-path "${HELPER_PROJECT}/Cargo.toml" --release --target "$helper_target"; then
  echo "carton-helper Rust build failed."
  popd >/dev/null
  exit 1
fi

helper_bin="${HELPER_PROJECT}/target/${helper_target}/release/carton-helper"
if [[ ! -f "$helper_bin" ]]; then
  echo "Built helper not found: ${helper_bin}" >&2
  popd >/dev/null
  exit 1
fi

cp -f "$helper_bin" "$HELPER_OUTPUT/"
cp -f "$helper_bin" "$OUTPUT/"
chmod +x "${OUTPUT}/carton-helper"

if [[ ! -f "$INCLUDE_KERNEL_SCRIPT" ]]; then
  echo "Kernel include script not found: ${INCLUDE_KERNEL_SCRIPT}" >&2
  popd >/dev/null
  exit 1
fi

bash "$INCLUDE_KERNEL_SCRIPT" "$RID" "$OUTPUT"

popd >/dev/null
echo "Output written to ${OUTPUT}"
