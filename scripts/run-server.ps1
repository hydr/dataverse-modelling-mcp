# run-server.ps1 - MCP launcher (Windows). Referenced as the 'command' in .mcp.json.
#
# Runs on every server start, i.e. also on "Reconnect" in the /mcp menu. It first
# makes sure the binary for the expected version is installed and then execs it,
# handing stdin/stdout/stderr through untouched. That is what makes a new release
# available without restarting the whole Claude Code session.
#
# stdout belongs to the MCP stdio stream: this script writes NOTHING to it. All
# diagnostics go to stderr (see Write-Note in binary-common.ps1).

param(
    [string] $PluginRoot,
    [string] $PluginData
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'binary-common.ps1')

if ([string]::IsNullOrWhiteSpace($PluginRoot)) { $PluginRoot = $env:CLAUDE_PLUGIN_ROOT }
if ([string]::IsNullOrWhiteSpace($PluginRoot)) { $PluginRoot = Split-Path -Parent $PSScriptRoot }
if ([string]::IsNullOrWhiteSpace($PluginData)) { $PluginData = $env:CLAUDE_PLUGIN_DATA }
if ([string]::IsNullOrWhiteSpace($PluginData))
{
    Write-Note 'CLAUDE_PLUGIN_DATA is not set and no -PluginData argument was passed - cannot locate the server binary.'
    exit 1
}

try
{
    # -AllowOffline: an unreachable GitHub must not take the server down; the
    # already installed binary is started instead (warning on stderr).
    $exe = Resolve-DataverseBinary -PluginRoot $PluginRoot -PluginData $PluginData -AllowOffline
}
catch
{
    Write-Note $_.Exception.Message
    exit 1
}

# Start without any redirection so the child inherits this process' stdin, stdout
# and stderr handles verbatim. Piping through PowerShell instead would buffer and
# re-encode the JSON-RPC stream.
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.UseShellExecute = $false      # inherit handles + the current environment
$psi.RedirectStandardInput = $false
$psi.RedirectStandardOutput = $false
$psi.RedirectStandardError = $false

$proc = [System.Diagnostics.Process]::Start($psi)
$proc.WaitForExit()
exit $proc.ExitCode
