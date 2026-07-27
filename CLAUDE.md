# dataverse-modelling-mcp

## Build
dotnet build

## Test
dotnet test

## Run (local MCP server)
dotnet run --project src/Dataverse.Server

## Setup (first time or after update)
dotnet run --project src/Dataverse.Setup

## Pack (dotnet tool)
dotnet pack src/Dataverse.Setup -c Release

## Release-Prozess (bei jeder Änderung)

Bei Änderungen am MCP-Server immer:
1. Feature-Branch erstellen und PR öffnen (gegen `master`)
2. Versionen erhöhen (minor bei neuen Features, patch bei Bugfixes):
   - `src/Dataverse.Setup/Dataverse.Setup.csproj` → `<Version>` (bestimmt den Release-Tag)
   - `scripts/BINARY_VERSION` → **exakt dieselbe** Version wie Setup.csproj (der SessionStart-Hook lädt die Binary von `releases/download/v<BINARY_VERSION>/…`; der Release-Workflow bricht ab, wenn beide abweichen)
   - `.claude-plugin/plugin.json` → `version` (eigene Plugin-Spur)
3. PR mergen
4. Git-Tag setzen (`git tag v<Version> && git push origin v<Version>`) → löst den Release-Workflow aus (NuGet-Publish + GitHub Release inkl. Server-Binary-Asset)

## Neue Version in einer laufenden Session nutzen

Kein Session-Neustart nötig: `.mcp.json` startet nicht die Binary direkt, sondern den Launcher
`scripts/run-server.ps1`, der bei jedem Serverstart die Binary auf die in `scripts/BINARY_VERSION`
gepinnte Version bringt und sie dann startet (stdio unverändert durchgereicht).

Nach einem Release also: Plugin aktualisieren (damit `BINARY_VERSION` stimmt), dann im
**`/mcp`-Menü** den Server `dataverse-modelling` **neu verbinden (Reconnect)** — fertig.

Details zur Binary-Auslieferung: siehe `docs/GH-RELEASE-SETUP.md`.
