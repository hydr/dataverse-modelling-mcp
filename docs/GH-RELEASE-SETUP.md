# Binary-Auslieferung über GitHub Releases

Das Plugin liefert **keine** vorkompilierte MCP-Server-Binary im Git-Repo aus.
Stattdessen wird die passende, self-contained Binary aus einem GitHub-Release
geladen — beim `SessionStart` **und** bei jedem Start des MCP-Servers.

## Beteiligte Teile

| Datei | Rolle |
|---|---|
| `.mcp.json` | Startet den Launcher `scripts/run-server.ps1` (nicht mehr die Binary direkt) |
| `scripts/run-server.ps1` / `.sh` | Launcher: aktualisiert die Binary, startet sie, reicht stdio durch |
| `scripts/binary-common.ps1` / `.sh` | Gemeinsame Download-/Auflösungslogik von Hook und Launcher |
| `hooks/hooks.json` | Registriert den `SessionStart`-Hook (PowerShell **und** bash) |
| `scripts/ensure-binary.ps1` | SessionStart-Hook (Windows), dünner Wrapper um `binary-common.ps1` |
| `scripts/ensure-binary.sh` | dito für git-bash; auf echtem Unix weiterhin No-op |
| `scripts/BINARY_VERSION` | Erwartete Binary-Version — **muss dem Release-Tag entsprechen** |
| `.github/workflows/release.yml` | Baut & veröffentlicht die Binary als Release-Asset |

## Update ohne Session-Neustart

Weil der Launcher bei **jedem** Serverstart läuft, genügt nach einem Release ein
**Reconnect im `/mcp`-Menü** — der Serverprozess startet neu, der Launcher zieht die
neue Version und startet sie. Vorher war die Binary nur über den `SessionStart`-Hook
aktualisierbar, d. h. erst in der nächsten Session nutzbar.

Der Hook bleibt erhalten (wärmt den Download vor). Doppelte Downloads gibt es nicht:
beide Pfade nutzen dieselbe Funktion, die sofort zurückkehrt, wenn die erwartete
Version bereits installiert ist.

## Layout auf Platte (versioniert)

```
<plugin-data>/bin/<version>/DataverseMcp.exe   <- wird ausgeführt
<plugin-data>/bin/DataverseMcp.exe             <- Legacy-Pfad, Best-Effort-Kopie
<plugin-data>/bin/.version                     <- Legacy-Marker
```

Versionierte Verzeichnisse, weil Windows eine **laufende `.exe` nicht überschreiben**
lässt: Bei parallelen Claude-Sessions oder einem Reconnect, während der alte Prozess
noch herunterfährt, würde ein In-Place-Update scheitern. Eine neue Version landet in
einem neuen Verzeichnis, alte Verzeichnisse werden per Best-Effort aufgeräumt
(gesperrte werden übersprungen und beim nächsten Lauf erneut versucht).

Die Legacy-Kopie unter `bin/DataverseMcp.exe` bleibt bestehen, damit Installationen
mit älterer `.mcp.json` (die direkt auf diesen Pfad zeigt) weiter starten.

## Namenskonvention

- **Release-Asset:** `DataverseMcp-win-x64.exe` (RID-Suffix, damit spätere
  Plattformen koexistieren können).
- **Runtime-Name auf Platte:** `DataverseMcp.exe`. Der Asset-Name wird beim Ablegen
  auf diesen festen Namen normalisiert.

## stdout-Disziplin (wichtig)

Der Launcher teilt sich stdout mit dem MCP-stdio-Stream. Deshalb schreiben Launcher
und gemeinsame Logik **ausschließlich nach stderr**; die Binary wird ohne Redirection
gestartet und erbt stdin/stdout/stderr unverändert. Eine einzige Zeile auf stdout
(z. B. ein `Write-Host`) würde die MCP-Verbindung zerstören.

Ist GitHub nicht erreichbar, startet der Launcher die bereits installierte Binary und
warnt nur auf stderr — Offline-Betrieb bleibt möglich. Der Hook bricht dagegen
weiterhin laut ab.

## Versionskopplung (wichtig)

Der Hook lädt von `releases/download/v${BINARY_VERSION}/DataverseMcp-win-x64.exe`.
Damit das aufgeht, muss gelten:

```
scripts/BINARY_VERSION  ==  Release-Tag (ohne 'v')
                        ==  Version in Dataverse.Setup.csproj
                        ==  version in .claude-plugin/plugin.json
```

Der Release-Workflow erzwingt das über den Guard-Step „Verify all versions match
the tag": Passt eine der drei Dateien nicht zum Tag, bricht der Build ab und nennt
die abweichende Datei. Der Guard verhindert zweierlei — den früheren
`0.0.0`-Fehler, bei dem gar kein Asset existierte, und das Auseinanderlaufen der
Plugin-Version, die früher eine eigene Spur hatte (0.18.1 gegen Binary 1.16.1),
sodass aus der Plugin-Version nicht ablesbar war, welche Binary installiert wird.

## Authentifizierung

Das Repo ist öffentlich, der Normalfall braucht deshalb **keine** Anmeldung. Der
Hook lädt in dieser Reihenfolge:

1. **Anonym** über `https://github.com/<repo>/releases/download/v<version>/<asset>`.
   Das ist der Regelweg: die meisten Nutzer haben weder `gh` noch ein Token, und
   beides zu verlangen würde sie am Start des Servers hindern.
2. **`gh` CLI**, falls installiert & authentifiziert (`gh auth status`) — greift,
   wenn über die Plugin-Option `release_repo` ein **privates** Repo eingetragen ist.
3. **REST-API mit Token** aus `GITHUB_TOKEN` bzw. `GH_TOKEN` (Bearer). Löst die
   Asset-ID über `releases/tags/v<version>` auf und lädt sie mit
   `Accept: application/octet-stream`.

Ein privates Repo antwortet auf Weg 1 mit 404, der Hook fällt dann auf 2 und 3
durch. Schlägt alles fehl, bricht der Hook **laut** ab (kein stilles `exit 0`) und
nennt den fehlenden Release-Tag.

## Release-Prozess (Binary)

1. Feature-Branch, Änderungen, PR gegen `master`.
2. Version erhöhen — in **allen drei** Dateien auf **exakt denselben** Wert, der zugleich der Tag ist:
   - `src/Dataverse.Setup/Dataverse.Setup.csproj` → `<Version>`
   - `scripts/BINARY_VERSION`
   - `.claude-plugin/plugin.json` → `version`

   Der Workflow-Schritt „Verify all versions match the tag" vergleicht alle drei mit dem Tag und
   bricht bei jeder Abweichung ab, mit Angabe der abweichenden Datei. Früher lief die Plugin-Version
   auf einer eigenen Spur — aus „Plugin 0.18.1" war dann nicht ablesbar, welche Binary drinsteckte.
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
  "$HOME/.claude/plugins/data/dataverse-modelling-mcp-hydr/bin/DataverseMcp.exe"
# optional, damit der Hook nicht neu lädt:
printf '%s' "<version>" > \
  "$HOME/.claude/plugins/data/dataverse-modelling-mcp-hydr/bin/.version"
```

## Bekannte Einschränkung: Plattformen

`.mcp.json` startet fest `DataverseMcp.exe`. Damit ist das Plugin derzeit
**Windows-only**. Für Linux/macOS müssten zusätzlich:

- der Workflow `DataverseMcp-linux-x64` / `DataverseMcp-osx-arm64` bauen,
- die Unix-Branch in `ensure-binary.sh` aktiviert werden,
- `.mcp.json` den plattformabhängigen Binärnamen auflösen.

Bis dahin ist die Unix-Branch der `.sh` ein bewusster No-op mit Hinweis.
