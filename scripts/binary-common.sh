#!/usr/bin/env bash
# binary-common.sh — shared binary resolution logic (git-bash / Unix).
#
# Sourced by:
#   scripts/ensure-binary.sh  (SessionStart hook)
#   scripts/run-server.sh     (MCP launcher — runs on every server start/reconnect)
#
# Mirrors scripts/binary-common.ps1, including the versioned layout:
#   <plugin-data>/bin/<version>/DataverseMcp.exe   <- what actually gets executed
#   <plugin-data>/bin/DataverseMcp.exe             <- legacy path, best-effort copy
#   <plugin-data>/bin/.version                     <- legacy marker
#
# Versioned directories exist because Windows cannot overwrite a running .exe.
#
# IMPORTANT: every diagnostic goes to stderr — the launcher shares stdout with the
# MCP stdio stream, a single stray line there breaks the protocol.

ASSET_NAME="DataverseMcp-win-x64.exe"   # release asset (RID-suffixed)
RUNTIME_NAME="DataverseMcp.exe"         # on-disk name

note() { echo "[dataverse-modelling-mcp] $*" >&2; }

# Echoes the expected version, or fails with a message on stderr.
expected_version() {
  local plugin_root="$1"
  local version_file="${plugin_root}/scripts/BINARY_VERSION"
  [ -f "$version_file" ] || { note "BINARY_VERSION not found at $version_file."; return 1; }

  local version
  version="$(tr -d '[:space:]' < "$version_file")"
  if [ -z "$version" ] || [ "$version" = "0.0.0" ]; then
    note "BINARY_VERSION is '$version' — no published binary to download. Publish a release first."
    return 1
  fi
  echo "$version"
}

is_windows() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) return 0 ;;
    *) return 1 ;;
  esac
}

# Newest usable binary already on disk — used when the download is impossible.
installed_fallback() {
  local bin_dir="$1"
  [ -d "$bin_dir" ] || return 1

  local candidate
  candidate="$(find "$bin_dir" -mindepth 2 -maxdepth 2 -name "$RUNTIME_NAME" -size +0 2>/dev/null \
    | sort -V | tail -1)"
  if [ -n "$candidate" ]; then
    echo "$candidate"
    return 0
  fi

  if [ -s "${bin_dir}/${RUNTIME_NAME}" ]; then
    echo "${bin_dir}/${RUNTIME_NAME}"
    return 0
  fi
  return 1
}

download_binary() {
  local repo="$1" tag="$2" destination="$3"
  local tmp="${destination}.download"
  rm -f "$tmp"

  note "fetching ${ASSET_NAME} (${tag}) from ${repo} ..."
  local downloaded=0

  # Preferred: gh CLI (reuses the user's existing GitHub auth).
  if command -v gh >/dev/null 2>&1; then
    if gh release download "$tag" --repo "$repo" --pattern "$ASSET_NAME" --output "$tmp" --clobber >/dev/null 2>&1; then
      downloaded=1
    fi
  fi

  # Fallback: REST API with a token — resolve asset id, then download by id.
  if [ "$downloaded" -ne 1 ]; then
    local token="${GITHUB_TOKEN:-${GH_TOKEN:-}}"
    if [ -z "$token" ]; then
      note "gh CLI unavailable/unauthenticated and neither GITHUB_TOKEN nor GH_TOKEN is set. Run 'gh auth login' or set a token."
      return 1
    fi
    command -v curl >/dev/null 2>&1 || { note "curl not found."; return 1; }

    local asset_id
    asset_id="$(curl -fsSL \
        -H "Authorization: Bearer $token" \
        -H "Accept: application/vnd.github+json" \
        -H "User-Agent: dataverse-modelling-mcp" \
        "https://api.github.com/repos/${repo}/releases/tags/${tag}" \
      | grep -B3 "\"name\": \"${ASSET_NAME}\"" | grep '"id":' | head -1 | grep -o '[0-9]\+' || true)"
    [ -n "$asset_id" ] || { note "release ${tag} in ${repo} has no asset named '${ASSET_NAME}'."; return 1; }

    curl -fSL \
        -H "Authorization: Bearer $token" \
        -H "Accept: application/octet-stream" \
        -H "User-Agent: dataverse-modelling-mcp" \
        -o "$tmp" \
        "https://api.github.com/repos/${repo}/releases/assets/${asset_id}" \
      || { note "download failed while fetching asset id ${asset_id}."; return 1; }
  fi

  [ -s "$tmp" ] || { note "download produced an empty file."; return 1; }

  mv -f "$tmp" "$destination"
  chmod +x "$destination" 2>/dev/null || true
}

# resolve_binary <plugin-root> <plugin-data> [allow-offline]
# Echoes the path of the binary to execute. Fast path: returns immediately (no
# network) when the expected version is already installed.
resolve_binary() {
  local plugin_root="$1" plugin_data="$2" allow_offline="${3:-0}"

  local version
  version="$(expected_version "$plugin_root")" || return 1

  local bin_dir="${plugin_data}/bin"
  local version_dir="${bin_dir}/${version}"
  local target="${version_dir}/${RUNTIME_NAME}"

  if [ -s "$target" ]; then
    echo "$target"
    return 0
  fi

  local repo="${CLAUDE_PLUGIN_OPTION_RELEASE_REPO:-hydr/dataverse-modelling-mcp}"
  mkdir -p "$version_dir"

  if ! download_binary "$repo" "v${version}" "$target"; then
    # Do not leave an empty version directory behind — it would look like an install.
    rmdir "$version_dir" 2>/dev/null || true
    if [ "$allow_offline" = "1" ]; then
      local fallback
      if fallback="$(installed_fallback "$bin_dir")"; then
        note "could not install v${version}; starting the already installed binary instead."
        echo "$fallback"
        return 0
      fi
    fi
    return 1
  fi

  # Legacy flat path kept in sync for installs whose .mcp.json still points at
  # <plugin-data>/bin/DataverseMcp.exe. Fails while that file is executed by
  # another session — harmless, the versioned copy is what we run.
  cp -f "$target" "${bin_dir}/${RUNTIME_NAME}" 2>/dev/null \
    && printf '%s' "$version" > "${bin_dir}/.version" \
    || note "note: legacy bin/${RUNTIME_NAME} not refreshed (in use by another session)."

  # Best-effort cleanup of older versions; locked directories are skipped.
  find "$bin_dir" -mindepth 1 -maxdepth 1 -type d ! -name "$version" -exec rm -rf {} + 2>/dev/null || true

  note "installed ${RUNTIME_NAME} v${version}."
  echo "$target"
}
