# TODO: Implement GitHub Release download (Windows path).
#
# Responsibilities:
#   1. Read expected version from "${env:CLAUDE_PLUGIN_ROOT}\scripts\BINARY_VERSION".
#   2. If "${env:CLAUDE_PLUGIN_DATA}\bin\DataverseMcp-win-x64.exe" exists AND
#      "${env:CLAUDE_PLUGIN_DATA}\bin\.version" matches, exit 0.
#   3. Else: download from
#        https://github.com/$env:CLAUDE_PLUGIN_OPTION_RELEASE_REPO/releases/download/v<version>/DataverseMcp-win-x64.exe
#      using $env:GITHUB_TOKEN or $env:GH_TOKEN. Preferred via gh CLI if installed:
#        gh release download "v$version" --repo $repo --pattern $binary --dir $dest
#   4. Write .version marker. No chmod on Windows.
#   5. Fail loudly if download fails — do NOT swallow errors.
#
# Status: PLACEHOLDER — see docs/GH-RELEASE-SETUP.md.

$ErrorActionPreference = 'Stop'

Write-Error '[dataverse-modelling-mcp] ensure-binary.ps1: TODO not implemented. MCP server will fail to start until this hook downloads the binary.'
exit 0
