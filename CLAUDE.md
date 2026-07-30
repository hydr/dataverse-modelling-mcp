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

## Skills

Skills gehören nach **`skills/`** — dieses Verzeichnis wird mit dem Plugin ausgeliefert und ist damit
in jedem Repo aktiv, in dem das Plugin installiert ist. **Nicht** nach `.claude/skills/`: das
beschränkt sie auf dieses Repository, also ausgerechnet auf die eine Umgebung, in der Dataverse-Wissen
am wenigsten gebraucht wird.

Skills sind reine Markdown-Dateien ohne Binary-Bezug. Eine Änderung an ihnen braucht deshalb weder
eine neue Binary noch einen Release-Tag — nur `.claude-plugin/plugin.json` hochzählen und mergen.

## Doku gehört zum Fix

Eine Erkenntnis, die nur im Code steht, ist für den nächsten Agenten nicht vorhanden. Deshalb bei
jeder Änderung mitziehen:

| Was sich ändert | Wohin |
|---|---|
| Neuer Validierungscode | Codetabelle in `skills/classic-workflows/SKILL.md` |
| Neues Feld im Definitionsmodell | `SKILL.md` |
| Neues Tool | Tool-Map in `SKILL.md` **und** `docs/tools/<bereich>.md` |
| Erkenntnis über das XAML-Format | `docs/classic-workflows-reference.md` |

`DocumentationCoverageTests` prüft die ersten drei automatisch und schlägt fehl, wenn etwas fehlt —
Verlassen muss man sich also nur beim vierten Punkt auf Disziplin.

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
