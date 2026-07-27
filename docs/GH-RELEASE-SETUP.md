# Binary-Auslieferung über GitHub Releases

Das Plugin liefert **keine** vorkompilierte MCP-Server-Binary im Git-Repo aus.
Stattdessen lädt ein `SessionStart`-Hook die passende, self-contained Binary beim
ersten Start aus einem GitHub-Release und legt sie dort ab, wo `.mcp.json` sie
erwartet.

## Beteiligte Teile

| Datei | Rolle |
|---|---|
| `.mcp.json` | Startet `${CLAUDE_PLUGIN_DATA}/bin/DataverseMcp.exe` (fester Runtime-Name) |
| `hooks/hooks.json` | Registriert den `SessionStart`-Hook (PowerShell **und** bash) |
| `scripts/ensure-binary.ps1` | Windows-nativer Download (PowerShell) |
| `scripts/ensure-binary.sh` | git-bash/Unix-Download; auf echtem Unix aktuell No-op |
| `scripts/BINARY_VERSION` | Erwartete Binary-Version — **muss dem Release-Tag entsprechen** |
| `.github/workflows/release.yml` | Baut & veröffentlicht die Binary als Release-Asset |

## Namenskonvention

- **Release-Asset:** `DataverseMcp-win-x64.exe` (RID-Suffix, damit spätere
  Plattformen koexistieren können).
- **Runtime-Name auf Platte:** `DataverseMcp.exe` — so verlangt es `.mcp.json`.
  Der Hook normalisiert den Asset-Namen beim Ablegen auf diesen festen Namen.
- **`.version`-Marker:** Der Hook schreibt neben die Binary eine `.version`-Datei.
  Stimmt sie mit `BINARY_VERSION` überein, wird kein erneuter Download ausgelöst.

## Versionskopplung (wichtig)

Der Hook lädt von `releases/download/v${BINARY_VERSION}/DataverseMcp-win-x64.exe`.
Damit das aufgeht, muss gelten:

```
scripts/BINARY_VERSION  ==  Release-Tag (ohne 'v')  ==  Version in Dataverse.Setup.csproj
```

Der Release-Workflow erzwingt das über einen Guard-Step: Passt `BINARY_VERSION`
nicht zum Tag, bricht der Build ab (verhindert den früheren `0.0.0`-Fehler, bei
dem gar kein Asset existierte).

## Authentifizierung

Das Repo ist privat. Der Hook lädt in dieser Reihenfolge:

1. **`gh` CLI**, falls installiert & authentifiziert (`gh auth status`) — nutzt die
   vorhandene GitHub-Anmeldung, kein manuelles Token nötig. **Empfohlen.**
2. **REST-API mit Token** aus `GITHUB_TOKEN` bzw. `GH_TOKEN` (Bearer). Er löst die
   Asset-ID über `releases/tags/v<version>` auf und lädt sie mit
   `Accept: application/octet-stream`.

Schlägt beides fehl, bricht der Hook **laut** ab (kein stilles `exit 0`).

## Release-Prozess (Binary)

1. Feature-Branch, Änderungen, PR gegen `master`.
2. Versionen erhöhen:
   - `src/Dataverse.Setup/Dataverse.Setup.csproj` → `<Version>` (bestimmt den Tag)
   - `scripts/BINARY_VERSION` → **exakt dieselbe** Version
   - `.claude-plugin/plugin.json` → `version` (eigene Plugin-Spur)
3. PR mergen.
4. `git tag v<Version> && git push origin v<Version>` → der Release-Workflow
   - packt & pusht das NuGet-Tool `Dataverse.Setup`,
   - baut den Server self-contained/single-file für `win-x64`,
   - hängt `DataverseMcp-win-x64.exe` und das `.nupkg` ans GitHub-Release.

## Lokaler Workaround (ohne Release)

Solange kein Release existiert, kann die Binary manuell gebaut und platziert werden:

```bash
dotnet publish src/Dataverse.Server -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
cp ./publish/Dataverse.Server.exe \
  "$HOME/.claude/plugins/data/dataverse-modelling-mcp-crossvertise/bin/DataverseMcp.exe"
# optional, damit der Hook nicht neu lädt:
printf '%s' "<version>" > \
  "$HOME/.claude/plugins/data/dataverse-modelling-mcp-crossvertise/bin/.version"
```

## Bekannte Einschränkung: Plattformen

`.mcp.json` startet fest `DataverseMcp.exe`. Damit ist das Plugin derzeit
**Windows-only**. Für Linux/macOS müssten zusätzlich:

- der Workflow `DataverseMcp-linux-x64` / `DataverseMcp-osx-arm64` bauen,
- die Unix-Branch in `ensure-binary.sh` aktiviert werden,
- `.mcp.json` den plattformabhängigen Binärnamen auflösen.

Bis dahin ist die Unix-Branch der `.sh` ein bewusster No-op mit Hinweis.
