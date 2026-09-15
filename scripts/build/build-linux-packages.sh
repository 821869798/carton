#!/usr/bin/env bash
set -euo pipefail

# Usage:
#   scripts/build/build-linux-packages.sh [rid] [version] [publish-dir] [out-dir]
#
# Builds the .deb and .rpm for a package-managed (non self-updating) install from an
# existing publish directory.
#
# Examples:
#   scripts/build/build-linux-packages.sh linux-x64 1.2.3
#   scripts/build/build-linux-packages.sh linux-arm64 1.2.3-beta.1 artifacts/publish/linux-arm64-appimage artifacts/pack/linux-arm64-beta
#
# Environment:
#   NFPM               Path to an existing nfpm binary (otherwise PATH, then a download)
#   CARTON_MAINTAINER  Maintainer field, defaults to the repository owner

RID="${1:-linux-x64}"
VERSION="${2:-0.0.0}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PUBLISH_DIR="${3:-${REPO_ROOT}/artifacts/publish/${RID}-appimage}"
OUT_DIR="${4:-${REPO_ROOT}/artifacts/pack/${RID}-release}"
APP_NAME="carton"
PACKAGING_DIR="${REPO_ROOT}/packaging/linux"
STAGE_DIR="${REPO_ROOT}/artifacts/stage/${RID}-package"
DESKTOP_SOURCE="${PACKAGING_DIR}/carton.desktop"
NFPM_CONFIG="${PACKAGING_DIR}/nfpm.yaml"
ICON_SOURCE="${REPO_ROOT}/src/carton.GUI/Assets/carton_icon.png"
NFPM_VERSION="2.47.0"
NFPM_BIN="${NFPM:-}"

usage() {
  cat <<EOF
Usage: $(basename "$0") [rid] [version] [publish-dir] [out-dir]

  rid          linux-x64 or linux-arm64 (default: linux-x64)
  version      Release version without the leading v, e.g. 1.2.3 or 1.2.3-beta.1
  publish-dir  Directory produced by scripts/build/test-publish-linux-aot.sh
               (default: artifacts/publish/<rid>-appimage)
  out-dir      Where the .deb/.rpm land (default: artifacts/pack/<rid>-release)
EOF
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  usage
  exit 0
fi

case "$RID" in
  linux-x64)
    DEB_ARCH="amd64"
    RPM_ARCH="x86_64"
    ;;
  linux-arm64)
    DEB_ARCH="arm64"
    RPM_ARCH="aarch64"
    ;;
  *)
    echo "Unsupported rid: ${RID} (expected linux-x64 or linux-arm64)" >&2
    exit 1
    ;;
esac

VERSION="${VERSION#v}"
if [[ ! "$VERSION" =~ ^[0-9] ]]; then
  echo "Unsupported version: ${VERSION}" >&2
  exit 1
fi

# Debian sorts prereleases with '~' (1.2.3~beta.1 is older than 1.2.3 and newer than
# 1.2.2); rpm has no such marker, it carries the prerelease in the release field instead.
BASE_VERSION="${VERSION%%-*}"
if [[ "$VERSION" == *-* ]]; then
  PRERELEASE="${VERSION#*-}"
  DEB_VERSION="${BASE_VERSION}~${PRERELEASE//-/.}"
  RPM_RELEASE="0.1.${PRERELEASE//-/.}"
else
  DEB_VERSION="${BASE_VERSION}"
  RPM_RELEASE="1"
fi

require_file() {
  if [[ ! -e "$1" ]]; then
    echo "Missing required file: $1" >&2
    exit 1
  fi
}

if [[ ! -d "$PUBLISH_DIR" ]]; then
  echo "Publish directory not found: $PUBLISH_DIR" >&2
  echo "Run scripts/build/test-publish-linux-aot.sh ${RID} Release <dir> INSTALLER_BUILD first." >&2
  exit 1
fi

require_file "${PUBLISH_DIR}/carton"
require_file "$DESKTOP_SOURCE"
require_file "$ICON_SOURCE"
require_file "$NFPM_CONFIG"

echo "Staging ${PUBLISH_DIR} for ${RID} (${VERSION})..."
rm -rf "$STAGE_DIR"
install -d \
  "${STAGE_DIR}/usr/lib/carton" \
  "${STAGE_DIR}/usr/share/applications" \
  "${STAGE_DIR}/usr/share/icons/hicolor/256x256/apps"

# Copy the payload minus everything that only makes sense for the self-updating
# portable/AppImage builds:
#   carton-helper          its presence alone turns on in-app direct updates
#   .carton_portable_data  makes the app keep config/kernel/logs next to the binary,
#                          which is root-owned under /usr
#   *.pdb / *.dbg          debug symbols
(
  cd "$PUBLISH_DIR"
  tar -cf - \
    --exclude='carton-helper' \
    --exclude='.carton_portable_data' \
    --exclude='*.pdb' \
    --exclude='*.dbg' \
    .
) | (
  cd "${STAGE_DIR}/usr/lib/carton"
  tar -xf -
)

chmod +x "${STAGE_DIR}/usr/lib/carton/carton"
if [[ -f "${STAGE_DIR}/usr/lib/carton/sing-box" ]]; then
  chmod +x "${STAGE_DIR}/usr/lib/carton/sing-box"
fi

install -Dm644 "$DESKTOP_SOURCE" "${STAGE_DIR}/usr/share/applications/carton.desktop"
install -Dm644 "$ICON_SOURCE" "${STAGE_DIR}/usr/share/icons/hicolor/256x256/apps/carton.png"

resolve_nfpm() {
  if [[ -n "$NFPM_BIN" ]]; then
    if [[ ! -x "$NFPM_BIN" ]]; then
      echo "NFPM=${NFPM_BIN} is not executable" >&2
      exit 1
    fi
    return
  fi

  if command -v nfpm >/dev/null 2>&1; then
    NFPM_BIN="$(command -v nfpm)"
    return
  fi

  local machine asset tools_dir archive url
  machine="$(uname -m)"
  case "$machine" in
    x86_64 | amd64) asset="Linux_x86_64" ;;
    aarch64 | arm64) asset="Linux_arm64" ;;
    *)
      echo "Unsupported build host architecture for nfpm: ${machine}" >&2
      exit 1
      ;;
  esac

  tools_dir="${REPO_ROOT}/artifacts/tools"
  NFPM_BIN="${tools_dir}/nfpm"
  if [[ -x "$NFPM_BIN" ]]; then
    return
  fi

  install -d "$tools_dir"
  archive="${tools_dir}/nfpm_${NFPM_VERSION}.tar.gz"
  url="https://github.com/goreleaser/nfpm/releases/download/v${NFPM_VERSION}/nfpm_${NFPM_VERSION}_${asset}.tar.gz"
  echo "Downloading nfpm ${NFPM_VERSION} (${asset})..."
  curl -fsSL --retry 3 --connect-timeout 15 -o "$archive" "$url"
  tar -xzf "$archive" -C "$tools_dir" nfpm
  chmod +x "$NFPM_BIN"
}

resolve_nfpm
install -d "$OUT_DIR"

maintainer="${CARTON_MAINTAINER:-821869798 <821869798@users.noreply.github.com>}"
homepage="https://github.com/821869798/carton"
license="GPL-3.0-only"

# nfpm does not expand ${...} in its config, and version/arch differ per packager, so render
# one concrete config for each. Values must stay free of the sed delimiter '|'.
render_config() {
  local out="$1"
  shift
  local -a sed_args=()
  local pair
  for pair in "$@"; do
    sed_args+=(-e "s|\${${pair%%=*}}|${pair#*=}|g")
  done
  sed "${sed_args[@]}" "$NFPM_CONFIG" > "$out"
}

tmp_dir="$(mktemp -d)"
trap 'rm -rf "${tmp_dir}"' EXIT
common=( "CARTON_STAGE=${STAGE_DIR}" "CARTON_MAINTAINER=${maintainer}" "CARTON_HOMEPAGE=${homepage}" "CARTON_LICENSE=${license}" )

deb_config="${tmp_dir}/nfpm-deb.yaml"
rpm_config="${tmp_dir}/nfpm-rpm.yaml"
render_config "$deb_config" "${common[@]}" \
  "CARTON_VERSION=${DEB_VERSION}" "CARTON_RELEASE=" "CARTON_ARCH=${DEB_ARCH}"
render_config "$rpm_config" "${common[@]}" \
  "CARTON_VERSION=${BASE_VERSION}" "CARTON_RELEASE=${RPM_RELEASE}" "CARTON_ARCH=${RPM_ARCH}"

# Release asset names follow the same APP_NAME-VERSION-RID pattern as the AppImage and the
# portable archives (the .github/release-template.md links are built from __VERSION__).
# The versions *inside* the packages still use each format's convention.
deb_target="${OUT_DIR}/${APP_NAME}-${VERSION}-${RID}.deb"
rpm_target="${OUT_DIR}/${APP_NAME}-${VERSION}-${RID}.rpm"

echo "Building ${deb_target}..."
"$NFPM_BIN" package --config "$deb_config" --packager deb --target "$deb_target"

echo "Building ${rpm_target}..."
"$NFPM_BIN" package --config "$rpm_config" --packager rpm --target "$rpm_target"

# The two files below must never reach a package: the first would let the app rewrite
# root-owned files under /usr, the second would send its data directory there too.
if command -v dpkg-deb >/dev/null 2>&1; then
  if dpkg-deb --contents "$deb_target" | grep -qE 'carton-helper|carton_portable_data'; then
    echo "Refusing to ship ${deb_target}: payload still contains self-update files." >&2
    exit 1
  fi
  echo "--- dpkg-deb --info"
  dpkg-deb --info "$deb_target"
  echo "--- dpkg-deb --contents"
  dpkg-deb --contents "$deb_target"
fi

if command -v rpm >/dev/null 2>&1; then
  echo "--- rpm -qpl"
  rpm -qpl "$rpm_target"
fi

echo "Packages written to ${OUT_DIR}:"
ls -1 "${OUT_DIR}"/*.deb "${OUT_DIR}"/*.rpm
