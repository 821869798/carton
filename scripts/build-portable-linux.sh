#!/usr/bin/env bash
set -euo pipefail

# Usage:
#   scripts/build-portable-linux.sh [rid] [configuration] [--skip-kernel]
#   scripts/build-portable-linux.sh --rid linux-arm64 --configuration Release

RID="linux-x64"
CONFIG="Release"
SKIP_KERNEL=0

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
APP_NAME="carton"
GUI_PROJECT="${REPO_ROOT}/src/carton.GUI/carton.GUI.csproj"
HELPER_PROJECT="${REPO_ROOT}/src/carton.Helper"
PUBLISH_DIR="${REPO_ROOT}/artifacts/publish/${RID}-portable"
HELPER_DIR="${REPO_ROOT}/artifacts/publish/${RID}-helper"
PACK_DIR="${REPO_ROOT}/artifacts/pack/${RID}-portable"
INCLUDE_KERNEL_SCRIPT="${SCRIPT_DIR}/include-singbox-kernel.sh"

usage() {
  cat <<EOF
Usage: $(basename "$0") [rid] [configuration] [--skip-kernel]

Build an update-capable Linux portable package for Carton.

Arguments:
  rid             Runtime identifier: linux-x64 or linux-arm64. Defaults to linux-x64.
  configuration   Build configuration. Defaults to Release.

Options:
  --rid <rid>              Runtime identifier.
  --configuration <config> Build configuration.
  --skip-kernel            Do not include the built-in sing-box runtime.
  -h, --help               Show this help.

Examples:
  $(basename "$0")
  $(basename "$0") linux-arm64 Release
  $(basename "$0") --rid linux-x64 --configuration Release --skip-kernel
EOF
}

parse_args() {
  local positional=()

  while [[ $# -gt 0 ]]; do
    case "$1" in
      --rid)
        if [[ $# -lt 2 || "${2:-}" == -* ]]; then
          echo "Missing value for --rid." >&2
          exit 1
        fi
        RID="${2:-}"
        shift 2
        ;;
      --configuration|-c)
        if [[ $# -lt 2 || "${2:-}" == -* ]]; then
          echo "Missing value for $1." >&2
          exit 1
        fi
        CONFIG="${2:-}"
        shift 2
        ;;
      --skip-kernel)
        SKIP_KERNEL=1
        shift
        ;;
      -h|--help)
        usage
        exit 0
        ;;
      -*)
        echo "Unknown option: $1" >&2
        usage >&2
        exit 1
        ;;
      *)
        positional+=("$1")
        shift
        ;;
    esac
  done

  if [[ ${#positional[@]} -gt 0 ]]; then
    RID="${positional[0]}"
  fi

  if [[ ${#positional[@]} -gt 1 ]]; then
    CONFIG="${positional[1]}"
  fi

  if [[ ${#positional[@]} -gt 2 ]]; then
    echo "Too many positional arguments." >&2
    usage >&2
    exit 1
  fi

  if [[ -z "$RID" || -z "$CONFIG" ]]; then
    echo "RID and configuration cannot be empty." >&2
    exit 1
  fi

  case "$RID" in
    linux-x64|linux-arm64) ;;
    *)
      echo "Unsupported RID: $RID" >&2
      echo "Supported values: linux-x64, linux-arm64" >&2
      exit 1
      ;;
  esac

  PUBLISH_DIR="${REPO_ROOT}/artifacts/publish/${RID}-portable"
  HELPER_DIR="${REPO_ROOT}/artifacts/publish/${RID}-helper"
  PACK_DIR="${REPO_ROOT}/artifacts/pack/${RID}-portable"
}

require_command() {
  local cmd="$1"
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "Required command not found: $cmd" >&2
    exit 1
  fi
}

resolve_version() {
  local version
  version="$(
    sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$GUI_PROJECT" | head -n 1
  )"

  if [[ -z "$version" ]]; then
    echo "Unable to resolve application version from $GUI_PROJECT" >&2
    exit 1
  fi

  printf '%s' "$version"
}

publish_project() {
  local project="$1"
  local output="$2"
  local label="$3"
  local props=(
    -c "$CONFIG"
    -r "$RID"
    -o "$output"
    /p:PublishAot=true
    /p:SelfContained=true
    /p:StripSymbols=true
    /p:DebugSymbols=false
    /p:DebugType=None
    /p:InvariantGlobalization=true
    /p:IncludeNativeLibrariesForSelfExtract=true
    /p:EnableCompressionInSingleFile=true
  )

  if [[ "$RID" == "linux-arm64" ]]; then
    props+=(/p:ObjCopyName=aarch64-linux-gnu-objcopy)
  fi

  echo "Publishing ${label} (${RID}, ${CONFIG})..."
  dotnet publish "$project" "${props[@]}"
}

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

publish_helper() {
  local output="$1"
  local target
  target="$(cargo_target_for_rid)"
  prepare_cargo_target "$target"

  echo "Building carton-helper (${RID}, ${CONFIG})..."
  cargo build --manifest-path "${HELPER_PROJECT}/Cargo.toml" --release --target "$target"

  local helper_bin="${HELPER_PROJECT}/target/${target}/release/carton-helper"
  if [[ ! -f "$helper_bin" ]]; then
    echo "Built helper not found: $helper_bin" >&2
    exit 1
  fi

  cp -f "$helper_bin" "$output/"
}

main() {
  parse_args "$@"
  require_command dotnet
  require_command cargo
  require_command sed
  require_command tar
  require_command mktemp

  if [[ ! -f "$GUI_PROJECT" ]]; then
    echo "GUI project not found: $GUI_PROJECT" >&2
    exit 1
  fi

  if [[ ! -f "${HELPER_PROJECT}/Cargo.toml" ]]; then
    echo "Helper project not found: $HELPER_PROJECT" >&2
    exit 1
  fi

  export DOTNET_ROLL_FORWARD=Major
  cd "$REPO_ROOT"

  local version
  version="$(resolve_version)"

  echo "==== Carton Linux Portable Build ===="
  echo "Version:       $version"
  echo "RID:           $RID"
  echo "Configuration: $CONFIG"
  echo "Repo Root:     $REPO_ROOT"
  echo "====================================="

  rm -rf "$PUBLISH_DIR" "$HELPER_DIR" "$PACK_DIR"
  mkdir -p "$PUBLISH_DIR" "$HELPER_DIR" "$PACK_DIR"

  publish_project "$GUI_PROJECT" "$PUBLISH_DIR" "$APP_NAME portable app"

  if [[ "$SKIP_KERNEL" -eq 1 ]]; then
    echo "Skipping built-in sing-box runtime."
  else
    if [[ ! -f "$INCLUDE_KERNEL_SCRIPT" ]]; then
      echo "Kernel include script not found: $INCLUDE_KERNEL_SCRIPT" >&2
      exit 1
    fi

    echo "Including built-in sing-box runtime..."
    bash "$INCLUDE_KERNEL_SCRIPT" "$RID" "$PUBLISH_DIR"
  fi

  publish_helper "$HELPER_DIR"
  if [[ ! -f "${HELPER_DIR}/carton-helper" ]]; then
    echo "Published helper not found: ${HELPER_DIR}/carton-helper" >&2
    exit 1
  fi

  cp -f "${HELPER_DIR}/carton-helper" "$PUBLISH_DIR/"
  chmod +x "${PUBLISH_DIR}/carton-helper"
  [[ -f "${PUBLISH_DIR}/${APP_NAME}" ]] && chmod +x "${PUBLISH_DIR}/${APP_NAME}"
  [[ -f "${PUBLISH_DIR}/sing-box" ]] && chmod +x "${PUBLISH_DIR}/sing-box"

  find "$PUBLISH_DIR" "$HELPER_DIR" -type f -name '*.pdb' -delete

  local stage_dir
  stage_dir="$(mktemp -d "${TMPDIR:-/tmp}/carton-portable-stage.XXXXXX")"
  cleanup() {
    rm -rf "$stage_dir"
  }
  trap cleanup EXIT

  cp -a "${PUBLISH_DIR}/." "$stage_dir/"
  touch "${stage_dir}/.carton_portable_data"

  if [[ -z "$(find "$stage_dir" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
    echo "Portable staging directory is empty: $stage_dir" >&2
    exit 1
  fi

  local portable_name="${APP_NAME}-${version}-${RID}-portable.tar.gz"
  local portable_path="${PACK_DIR}/${portable_name}"

  echo "Compressing to ${portable_path}..."
  rm -f "$portable_path"
  tar -czf "$portable_path" -C "$stage_dir" .

  echo "Portable package created: ${portable_path}"
}

main "$@"
