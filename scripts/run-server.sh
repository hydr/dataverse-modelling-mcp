#!/usr/bin/env bash
# run-server.sh — MCP launcher (git-bash / Unix pendant of run-server.ps1).
#
# Runs on every server start, i.e. also on "Reconnect" in the /mcp menu. It first
# makes sure the binary for the expected version is installed and then execs it,
# handing stdin/stdout/stderr through untouched. That is what makes a new release
# available without restarting the whole Claude Code session.
#
# stdout belongs to the MCP stdio stream: this script writes NOTHING to it — all
# diagnostics go to stderr (see note() in binary-common.sh).
#
# Usage: run-server.sh [<plugin-root> <plugin-data>]
#        (falls back to CLAUDE_PLUGIN_ROOT / CLAUDE_PLUGIN_DATA)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./binary-common.sh
. "${SCRIPT_DIR}/binary-common.sh"

PLUGIN_ROOT="${1:-${CLAUDE_PLUGIN_ROOT:-$(dirname "$SCRIPT_DIR")}}"
PLUGIN_DATA="${2:-${CLAUDE_PLUGIN_DATA:-}}"

if [ -z "$PLUGIN_DATA" ]; then
  note "CLAUDE_PLUGIN_DATA is not set and no plugin-data argument was passed — cannot locate the server binary."
  exit 1
fi

# allow-offline=1: an unreachable GitHub must not take the server down; the
# already installed binary is started instead (warning on stderr).
if ! EXE="$(resolve_binary "$PLUGIN_ROOT" "$PLUGIN_DATA" 1)"; then
  exit 1
fi

# exec replaces this shell, so the binary owns stdin/stdout/stderr directly and
# its exit code becomes ours.
exec "$EXE"
