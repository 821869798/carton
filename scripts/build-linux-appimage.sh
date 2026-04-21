#!/usr/bin/env bash
set -euo pipefail

# Usage: scripts/build-linux-appimage.sh [rid] [configuration]
# Example: scripts/build-linux-appimage.sh linux-x64 Release

RID="${1:-linux-x64}"
CONFIG="${2:-Release}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
PUBLISH_SCRIPT="${SCRIPT_DIR}/test-publish-linux-aot.sh"
PROJECT="${REPO_ROOT}/src/carton.GUI/carton.GUI.csproj"
PUBLISH_OUTPUT="${REPO_ROOT}/artifacts/publish/${RID}"
APPIMAGE_ROOT="${REPO_ROOT}/artifacts/appimage/${RID}"
APPDIR="${APPIMAGE_ROOT}/Carton.AppDir"
APPIMAGE_OUTPUT_DIR="${REPO_ROOT}/artifacts/dist"
APP_NAME="Carton"
APP_BINARY_NAME="carton"
DESKTOP_FILE_NAME="carton.desktop"
ICON_SOURCE="${REPO_ROOT}/src/carton.GUI/Assets/carton_icon.png"
APPIMAGETOOL_RELEASES_URL="https://github.com/AppImage/appimagetool/releases"
APPIMAGETOOL_DOWNLOAD_BASE_URL="https://github.com/AppImage/appimagetool/releases/download/continuous"

usage() {
  cat <<EOF
Usage: $(basename "$0") [rid] [configuration]

Build a Linux AppImage for Carton.

Arguments:
  rid            Runtime identifier, one of: linux-x64, linux-arm64
  configuration  Build configuration, defaults to Release

Environment:
  APPIMAGETOOL   Optional explicit path to appimagetool

Examples:
  $(basename "$0")
  $(basename "$0") linux-x64 Release
EOF
}

require_command() {
  local cmd="$1"
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "Required command not found: $cmd" >&2
    exit 1
  fi
}

download_file() {
  local url="$1"
  local output="$2"

  if command -v curl >/dev/null 2>&1; then
    curl -fL --retry 3 --connect-timeout 15 -o "$output" "$url"
    return
  fi

  if command -v wget >/dev/null 2>&1; then
    wget -O "$output" "$url"
    return
  fi

  echo "Neither curl nor wget is available to download ${url}" >&2
  exit 1
}

resolve_version() {
  local version
  version="$(
    sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -n 1
  )"

  if [[ -z "$version" ]]; then
    echo "Unable to resolve application version from $PROJECT" >&2
    exit 1
  fi

  printf '%s' "$version"
}

map_arch() {
  case "$1" in
    linux-x64) printf 'x86_64' ;;
    linux-arm64) printf 'aarch64' ;;
    *)
      echo "Unsupported RID: $1" >&2
      echo "Supported values: linux-x64, linux-arm64" >&2
      exit 1
      ;;
  esac
}

resolve_appimagetool_asset_name() {
  case "$1" in
    linux-x64) printf 'appimagetool-x86_64.AppImage' ;;
    linux-arm64) printf 'appimagetool-aarch64.AppImage' ;;
    *)
      echo "Unsupported RID: $1" >&2
      echo "Supported values: linux-x64, linux-arm64" >&2
      exit 1
      ;;
  esac
}

download_appimagetool_to_scripts() {
  local asset_name="$1"
  local target_path="${SCRIPT_DIR}/${asset_name}"
  local download_url="${APPIMAGETOOL_DOWNLOAD_BASE_URL}/${asset_name}"

  echo "Downloading ${asset_name} to ${target_path}..." >&2
  download_file "$download_url" "$target_path"
  chmod +x "$target_path"
  printf '%s' "$target_path"
}

resolve_appimagetool() {
  local candidate
  local asset_name

  if [[ -n "${APPIMAGETOOL:-}" ]]; then
    printf '%s' "$APPIMAGETOOL"
    return
  fi

  asset_name="$(resolve_appimagetool_asset_name "$RID")"

  if command -v appimagetool >/dev/null 2>&1; then
    command -v appimagetool
    return
  fi

  for candidate in \
    "${SCRIPT_DIR}"/appimagetool*.AppImage \
    "${REPO_ROOT}"/appimagetool*.AppImage \
    "${REPO_ROOT}"/tools/appimagetool*.AppImage; do
    if [[ -f "$candidate" ]]; then
      chmod +x "$candidate"
      printf '%s' "$candidate"
      return
    fi
  done

  download_appimagetool_to_scripts "$asset_name"
}

generate_desktop_file() {
  local desktop_file="$1"
  cat >"$desktop_file" <<EOF
[Desktop Entry]
Type=Application
Name=${APP_NAME}
Comment=Cross-platform proxy client powered by sing-box
Exec=${APP_BINARY_NAME}
Icon=carton
Terminal=false
Categories=Network;
StartupNotify=false
StartupWMClass=carton
EOF
}

generate_apprun() {
  local app_run="$1"
  cat >"$app_run" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "${HERE}/usr/bin/carton" "$@"
EOF
  chmod +x "$app_run"
}

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    usage
    exit 0
  fi

  require_command dotnet

  if [[ ! -x "$PUBLISH_SCRIPT" ]]; then
    chmod +x "$PUBLISH_SCRIPT"
  fi

  if [[ ! -f "$ICON_SOURCE" ]]; then
    echo "Icon file not found: $ICON_SOURCE" >&2
    exit 1
  fi

  local arch
  local version
  local appimagetool
  arch="$(map_arch "$RID")"
  version="$(resolve_version)"
  appimagetool="$(resolve_appimagetool)"

  echo "Publishing ${APP_NAME} (${RID}, ${CONFIG})..."
  "$PUBLISH_SCRIPT" "$RID" "$CONFIG"

  if [[ ! -f "${PUBLISH_OUTPUT}/${APP_BINARY_NAME}" ]]; then
    echo "Published binary not found: ${PUBLISH_OUTPUT}/${APP_BINARY_NAME}" >&2
    exit 1
  fi

  echo "Preparing AppDir at ${APPDIR}..."
  rm -rf "$APPDIR"
  mkdir -p "${APPDIR}/usr/bin"

  cp -a "${PUBLISH_OUTPUT}/." "${APPDIR}/usr/bin/"
  cp "$ICON_SOURCE" "${APPDIR}/carton.png"
  cp "$ICON_SOURCE" "${APPDIR}/.DirIcon"

  generate_desktop_file "${APPDIR}/${DESKTOP_FILE_NAME}"
  generate_apprun "${APPDIR}/AppRun"

  mkdir -p "$APPIMAGE_OUTPUT_DIR"
  local output_name="${APP_NAME}-${version}-${RID}.AppImage"
  local output_path="${APPIMAGE_OUTPUT_DIR}/${output_name}"

  echo "Building AppImage ${output_path}..."
  rm -f "$output_path"
  ARCH="$arch" "$appimagetool" "$APPDIR" "$output_path"

  echo "AppImage written to ${output_path}"
}

main "$@"
