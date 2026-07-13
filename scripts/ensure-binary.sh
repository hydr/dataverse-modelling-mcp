#!/usr/bin/env bash
# ensure-binary.sh — SessionStart hook (cross-platform).
#
# Downloads the self-contained MCP server binary from a GitHub Release and places
# it where .mcp.json expects it (${CLAUDE_PLUGIN_DATA}/bin/DataverseMcp.exe).
# Idempotent: skips the download when the correct version is already present.
#
# On Windows this runs under git-bash and does the full download itself, so it is
# independent of ensure-binary.ps1 (which serves hosts that dispatch hooks via
# PowerShell instead of bash). Both scripts are idempotent — running both is safe.
#
# See docs/GH-RELEASE-SETUP.md for the publish flow.

set -euo pipefail

fail() { echo "[dataverse-modelling-mcp] $*" >&2; exit 1; }
note() { echo "[dataverse-modelling-mcp] $*" >&2; }

: "${CLAUDE_PLUGIN_ROOT:?CLAUDE_PLUGIN_ROOT is not set}"
: "${CLAUDE_PLUGIN_DATA:?CLAUDE_PLUGIN_DATA is not set}"

VERSION_FILE="${CLAUDE_PLUGIN_ROOT}/scripts/BINARY_VERSION"
[ -f "$VERSION_FILE" ] || fail "BINARY_VERSION not found at $VERSION_FILE."
VERSION="$(tr -d '[:space:]' < "$VERSION_FILE")"
[ -n "$VERSION" ] && [ "$VERSION" != "0.0.0" ] || \
  fail "BINARY_VERSION is '$VERSION' — no published binary to download. Publish a release first."

REPO="${CLAUDE_PLUGIN_OPTION_RELEASE_REPO:-hydr/dataverse-modelling-mcp}"
TAG="v${VERSION}"

# Runtime name is fixed by .mcp.json; the release asset is RID-suffixed.
RUNTIME_NAME="DataverseMcp.exe"
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) ASSET="DataverseMcp-win-x64.exe" ;;
  *)
    # Real Unix: the plugin's .mcp.json launches DataverseMcp.exe, which is a
    # Windows binary, and the release currently ships win-x64 only. Treat Unix as
    # out of scope rather than failing the session loudly.
    note "non-Windows host detected ($(uname -s)); this plugin is currently Windows-only. See docs/GH-RELEASE-SETUP.md."
    exit 0
    ;;
esac

BIN_DIR="${CLAUDE_PLUGIN_DATA}/bin"
TARGET="${BIN_DIR}/${RUNTIME_NAME}"
MARKER="${BIN_DIR}/.version"

# Already up to date?
if [ -f "$TARGET" ] && [ -f "$MARKER" ] && [ "$(tr -d '[:space:]' < "$MARKER")" = "$VERSION" ]; then
  note "binary v${VERSION} already present."
  exit 0
fi

mkdir -p "$BIN_DIR"
TMP="${TARGET}.download"
rm -f "$TMP"

note "fetching ${ASSET} (${TAG}) from ${REPO} ..."

downloaded=0

# Preferred: gh CLI (reuses the user's existing GitHub auth).
if command -v gh >/dev/null 2>&1; then
  if gh release download "$TAG" --repo "$REPO" --pattern "$ASSET" --output "$TMP" --clobber >/dev/null 2>&1; then
    downloaded=1
  fi
fi

# Fallback: REST API with a token — resolve asset id, then download by id.
if [ "$downloaded" -ne 1 ]; then
  TOKEN="${GITHUB_TOKEN:-${GH_TOKEN:-}}"
  [ -n "$TOKEN" ] || fail "download failed: gh CLI unavailable/unauthenticated and neither GITHUB_TOKEN nor GH_TOKEN is set. Run 'gh auth login' or set a token."
  command -v curl >/dev/null 2>&1 || fail "download failed: curl not found."

  ASSET_ID="$(curl -fsSL \
      -H "Authorization: Bearer $TOKEN" \
      -H "Accept: application/vnd.github+json" \
      -H "User-Agent: dataverse-modelling-mcp" \
      "https://api.github.com/repos/${REPO}/releases/tags/${TAG}" \
    | grep -B3 "\"name\": \"${ASSET}\"" | grep '"id":' | head -1 | grep -o '[0-9]\+' || true)"
  [ -n "$ASSET_ID" ] || fail "download failed: release ${TAG} in ${REPO} has no asset named '${ASSET}'."

  curl -fSL \
      -H "Authorization: Bearer $TOKEN" \
      -H "Accept: application/octet-stream" \
      -H "User-Agent: dataverse-modelling-mcp" \
      -o "$TMP" \
      "https://api.github.com/repos/${REPO}/releases/assets/${ASSET_ID}" \
    || fail "download failed while fetching asset id ${ASSET_ID}."
  downloaded=1
fi

[ -s "$TMP" ] || fail "download produced an empty file — aborting."

mv -f "$TMP" "$TARGET"
chmod +x "$TARGET" 2>/dev/null || true
printf '%s' "$VERSION" > "$MARKER"
note "installed ${RUNTIME_NAME} v${VERSION}."
exit 0
