"""Start an MCP server binary, send `initialize` and `tools/list` over stdio, and check the answers.

Usage: python3 smoke_mcp.py <expected-min-tools> <command> [args...]
Exits non-zero when the server does not answer within 60 s or answers wrongly. No Dataverse
access is needed: neither call touches the environment.
"""
import json
import subprocess
import sys
import threading

min_tools = int(sys.argv[1])
command = sys.argv[2:]

proc = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=sys.stderr, text=True)
timer = threading.Timer(60, proc.kill)
timer.start()

def send(message):
    proc.stdin.write(json.dumps(message) + "\n")
    proc.stdin.flush()

def receive(request_id):
    for line in proc.stdout:
        message = json.loads(line)
        if message.get("id") == request_id:
            return message
    sys.exit(f"server closed stdout before answering request {request_id}")

send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
      "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "1"}}})
info = receive(1)["result"]["serverInfo"]
send({"jsonrpc": "2.0", "method": "notifications/initialized"})
send({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
tools = receive(2)["result"]["tools"]

timer.cancel()
proc.kill()
print(f"{info['name']} {info['version']}: {len(tools)} tools")
if info["name"] != "dataverse-modelling-mcp" or len(tools) < min_tools:
    sys.exit("unexpected answer")
