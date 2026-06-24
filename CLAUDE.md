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
   - `.claude-plugin/plugin.json` → `version`
   - `src/Dataverse.Setup/Dataverse.Setup.csproj` → `<Version>`
3. PR mergen
4. Git-Tag setzen (`git tag v<Version> && git push origin v<Version>`) → löst den Release-Workflow aus (NuGet-Publish + GitHub Release)
