#!/usr/bin/env bash
# ensure-binary.sh — SessionStart hook (cross-platform).
#
# Makes sure the self-contained MCP server binary for the version in
# scripts/BINARY_VERSION is installed under ${CLAUDE_PLUGIN_DATA}/bin.
# Idempotent: returns immediately (no network) when it is already present.
#
# The actual work lives in scripts/binary-common.sh, which the MCP launcher
# (scripts/run-server.sh) shares — so a reconnect picks up a new release without
# restarting the session. Running hook and launcher back to back is free: the
# second call hits the "already installed" fast path.
#
# On Windows this runs under git-bash and does the full download itself, so it is
# independent of ensure-binary.ps1 (which serves hosts that dispatch hooks via
# PowerShell instead of bash). Both scripts are idempotent — running both is safe.
#
# See docs/GH-RELEASE-SETUP.md for the publish flow.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./binary-common.sh
. "${SCRIPT_DIR}/binary-common.sh"

PLUGIN_ROOT="${CLAUDE_PLUGIN_ROOT:-$(dirname "$SCRIPT_DIR")}"
: "${CLAUDE_PLUGIN_DATA:?CLAUDE_PLUGIN_DATA is not set}"

if ! is_windows; then
  # Real Unix: the plugin's .mcp.json launches DataverseMcp.exe, which is a
  # Windows binary, and the release currently ships win-x64 only. Treat Unix as
  # out of scope rather than failing the session loudly.
  note "non-Windows host detected ($(uname -s)); this plugin is currently Windows-only. See docs/GH-RELEASE-SETUP.md."
  exit 0
fi

EXE="$(resolve_binary "$PLUGIN_ROOT" "$CLAUDE_PLUGIN_DATA")" || exit 1
note "binary ready: ${EXE}"
exit 0
