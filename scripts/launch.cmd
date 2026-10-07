@echo off
rem launch.cmd - the MCP server command in .mcp.json, Windows side.
rem
rem .mcp.json names "${CLAUDE_PLUGIN_ROOT}/scripts/launch" without an extension; on Windows
rem Claude Code resolves it to this file, on macOS/Linux to the shell script "launch".
rem stdin/stdout pass straight through to PowerShell and from there to the server binary.
rem
rem Usage: launch <plugin-root> <plugin-data>
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0run-server.ps1" -PluginRoot "%~1" -PluginData "%~2"
