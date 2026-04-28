#!/usr/bin/env bash
set -euo pipefail

RID="${1:-}"
DESTINATION="${2:-}"

if [[ -z "$RID" || -z "$DESTINATION" ]]; then
  echo "Usage: $(basename "$0") <rid> <destination>" >&2
  echo "Example: $(basename "$0") linux-x64 artifacts/publish/linux-x64" >&2
  exit 1
fi

GITHUB_API_LATEST="https://api.github.com/repos/SagerNet/sing-box/releases/latest"
GITHUB_DOWNLOAD_BASE="https://github.com/SagerNet/sing-box/releases/download"

download_file() {
  local url="$1"
  local output="$2"

  if command -v curl >/dev/null 2>&1; then
    curl -fsSL --retry 3 --connect-timeout 15 -o "$output" "$url"
    return
  fi

  if command -v wget >/dev/null 2>&1; then
    wget -q -O "$output" "$url"
    return
  fi

  echo "Neither curl nor wget is available." >&2
  exit 1
}

resolve_latest_tag() {
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL --connect-timeout 15 "$GITHUB_API_LATEST" \
      | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
      | head -n 1
    return
  fi

  if command -v wget >/dev/null 2>&1; then
    wget -q -O - "$GITHUB_API_LATEST" \
      | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
      | head -n 1
    return
  fi

  echo ""
}

build_asset_candidates() {
  local rid="$1"
  local version="$2"

  case "$rid" in
    win-x64)
      printf '%s\n' \
        "sing-box-${version}-windows-amd64.zip" \
        "sing-box-${version}-windows-amd64v3.zip"
      ;;
    win-arm64)
      printf '%s\n' \
        "sing-box-${version}-windows-arm64.zip"
      ;;
    linux-x64)
      printf '%s\n' \
        "sing-box-${version}-linux-amd64-glibc.tar.gz" \
        "sing-box-${version}-linux-amd64-purego.tar.gz" \
        "sing-box-${version}-linux-amd64-musl.tar.gz" \
        "sing-box-${version}-linux-amd64v3-glibc.tar.gz" \
        "sing-box-${version}-linux-amd64v3-purego.tar.gz" \
        "sing-box-${version}-linux-amd64v3-musl.tar.gz"
      ;;
    linux-arm64)
      printf '%s\n' \
        "sing-box-${version}-linux-arm64-glibc.tar.gz" \
        "sing-box-${version}-linux-arm64-purego.tar.gz" \
        "sing-box-${version}-linux-arm64-musl.tar.gz"
      ;;
    *)
      echo "Unsupported RID for built-in sing-box kernel: ${rid}" >&2
      exit 1
      ;;
  esac
}

mkdir -p "$DESTINATION"
tmp_root="$(mktemp -d "${TMPDIR:-/tmp}/carton-singbox.XXXXXX")"
cleanup() {
  rm -rf "$tmp_root"
}
trap cleanup EXIT

echo "Resolving latest sing-box release for ${RID}..."
tag="$(resolve_latest_tag)"
if [[ -z "$tag" ]]; then
  echo "Failed to resolve latest sing-box tag from GitHub API." >&2
  exit 1
fi

version="${tag#v}"
asset_name=""
archive_path=""

while IFS= read -r candidate; do
  [[ -n "$candidate" ]] || continue
  url="${GITHUB_DOWNLOAD_BASE}/${tag}/${candidate}"
  path="${tmp_root}/${candidate}"
  echo "Trying ${candidate}..."
  if download_file "$url" "$path"; then
    asset_name="$candidate"
    archive_path="$path"
    break
  fi
done < <(build_asset_candidates "$RID" "$version")

if [[ -z "$asset_name" || -z "$archive_path" ]]; then
  echo "Failed to download any matching sing-box asset for ${RID} @ ${tag}." >&2
  exit 1
fi

extract_dir="${tmp_root}/extract"
mkdir -p "$extract_dir"

if [[ "$asset_name" == *.zip ]]; then
  if ! command -v unzip >/dev/null 2>&1; then
    echo "'unzip' is required to extract ${asset_name}" >&2
    exit 1
  fi
  unzip -q -o "$archive_path" -d "$extract_dir"
  binary_name="sing-box.exe"
else
  tar -xzf "$archive_path" -C "$extract_dir"
  binary_name="sing-box"
fi

binary_path="$(find "$extract_dir" -type f -name "$binary_name" | head -n 1 || true)"
if [[ -z "$binary_path" ]]; then
  echo "${binary_name} was not found in ${asset_name}" >&2
  exit 1
fi

if [[ "$binary_name" == "sing-box.exe" ]]; then
  cp -f "$binary_path" "$DESTINATION/sing-box.exe"
else
  cp -f "$binary_path" "$DESTINATION/sing-box"
  chmod +x "$DESTINATION/sing-box"
fi

find "$extract_dir" -type f -name '*.dll' -exec cp -f {} "$DESTINATION/" \;
find "$extract_dir" -type f -name '*.so*' -exec cp -f {} "$DESTINATION/" \;

echo "Included built-in sing-box kernel ${tag} into: ${DESTINATION}"
