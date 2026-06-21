#!/usr/bin/env bash
# TODO: Implement cross-platform GitHub Release download.
#
# Responsibilities:
#   1. Detect OS / arch:
#        Windows  -> DataverseMcp-win-x64.exe
#        Linux    -> DataverseMcp-linux-x64
#        macOS    -> DataverseMcp-osx-arm64 (or osx-x64)
#   2. Read expected version from "${CLAUDE_PLUGIN_ROOT}/scripts/BINARY_VERSION".
#   3. If "${CLAUDE_PLUGIN_DATA}/bin/<binary>" exists AND ".version" matches, exit 0.
#   4. Else: fetch the release asset from:
#        https://github.com/${CLAUDE_PLUGIN_OPTION_RELEASE_REPO}/releases/download/v${VERSION}/<binary>
#      using the user's GITHUB_TOKEN / GH_TOKEN (same auth that powers
#      `claude plugin marketplace add` for private repos).
#   5. chmod +x on Unix, write .version marker.
#   6. Fail loudly with a clear message if download fails — do NOT exit 0 on failure.
#
# Implementation notes:
#   - On Windows hosts SessionStart hooks need git-bash on PATH for this .sh
#     wrapper. The robust path is to dispatch to either ensure-binary.ps1
#     (Windows) or this script (Unix) from a tiny detector.
#   - Use:
#       curl -fSL -H "Authorization: Bearer $GITHUB_TOKEN" \
#            -H "Accept: application/octet-stream" \
#            "https://api.github.com/repos/$REPO/releases/tags/v$VERSION"
#     to resolve the asset id, then download by id with the same Authorization.
#   - Or simpler with gh CLI if installed:
#       gh release download "v$VERSION" --repo "$REPO" --pattern "$BINARY" --dir "$DEST"
#
# Status: PLACEHOLDER — see docs/GH-RELEASE-SETUP.md for the binary publish flow
# and CONTRIBUTING.md for the script-completion checklist.

set -euo pipefail

echo "[dataverse-modelling-mcp] ensure-binary.sh: TODO not implemented" >&2
echo "[dataverse-modelling-mcp] The MCP server will fail to start until this hook downloads the binary." >&2
exit 0
