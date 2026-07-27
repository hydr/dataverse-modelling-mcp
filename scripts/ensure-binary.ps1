# ensure-binary.ps1 - SessionStart hook (Windows).
#
# Makes sure the self-contained MCP server binary for the version in
# scripts/BINARY_VERSION is installed under ${CLAUDE_PLUGIN_DATA}\bin.
# Idempotent: returns immediately (no network) when it is already present.
#
# The actual work lives in scripts/binary-common.ps1, which the MCP launcher
# (scripts/run-server.ps1) shares - so a reconnect picks up a new release without
# restarting the session.
#
# See docs/GH-RELEASE-SETUP.md for the publish flow.

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'binary-common.ps1')

$pluginRoot = $env:CLAUDE_PLUGIN_ROOT
$pluginData = $env:CLAUDE_PLUGIN_DATA
if ([string]::IsNullOrWhiteSpace($pluginRoot)) { $pluginRoot = Split-Path -Parent $PSScriptRoot }
if ([string]::IsNullOrWhiteSpace($pluginData))
{
    Write-Note 'CLAUDE_PLUGIN_DATA is not set.'
    exit 1
}

try
{
    $exe = Resolve-DataverseBinary -PluginRoot $pluginRoot -PluginData $pluginData
    Write-Note "binary ready: $exe"
    exit 0
}
catch
{
    Write-Note $_.Exception.Message
    exit 1
}
