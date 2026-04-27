# Architecture

## Overview

```
┌─────────────────────────────────────────────────────────────┐
│  AI Client (Claude Code, GitHub Copilot, etc.)              │
│                      MCP (stdio)                            │
└─────────────────────┬───────────────────────────────────────┘
                       │
┌─────────────────────▼───────────────────────────────────────┐
│  Dataverse.Server  (net8.0, MCP stdio server)               │
│  - Tools/*.cs  (McpServerToolType classes)                  │
│  - Program.cs  (Host + DI + MCP registration)               │
└────────┬────────────────────────────────────────────────────┘
         │  injects
┌────────▼────────────────────────────────────────────────────┐
│  Dataverse.Core  (net8.0, business logic)                   │
│                                                             │
│  Auth/                                                      │
│    DataverseTokenProvider  — MSAL PublicClientApplication   │
│    SecureTokenCache        — OS keychain / DPAPI            │
│                                                             │
│  Clients/                                                   │
│    DataverseHttpClient     — Dataverse Web API v9.2         │
│    PowerAutomateHttpClient — Flow API / BAP API             │
│                                                             │
│  Services/                                                  │
│    WorkflowService, CloudFlowService, TableService,         │
│    ViewService, SolutionService, SecurityRoleService,       │
│    EnvironmentVariableService                               │
│                                                             │
│  Config/                                                    │
│    ConfigProvider          — reads config.json              │
└─────────────────────────────────────────────────────────────┘
         │  HTTP
┌────────▼────────────────────────────────────────────────────┐
│  Microsoft Dataverse Web API (v9.2)                         │
│  Power Automate Flow API                                    │
│  Business Application Platform (BAP) API                   │
└─────────────────────────────────────────────────────────────┘
```

## Project layout

| Project | Role |
|---------|------|
| `Dataverse.Core` | Domain logic, HTTP clients, token management, models |
| `Dataverse.Server` | MCP server host; thin tool wrappers that delegate to Core |
| `Dataverse.Setup` | dotnet global tool; interactive setup wizard |
| `Dataverse.Tests` | NUnit unit tests for Core services |

## Design principles

- **No client secrets.** All auth is delegated (interactive browser or cached token).
- **Error isolation.** All MCP tool methods catch all exceptions and return a JSON error object — they never throw into the MCP runtime.
- **Config over code.** Environment details (org URL, region, env ID) come from `config.json`; the server never has hard-coded URLs.
- **Single active environment.** The `activeEnvironment` key in config selects which environment block to use at runtime. Switch environments by editing config.json or re-running setup.
