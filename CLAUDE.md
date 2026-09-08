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

## Zwei Repositories

Das Projekt liegt in zwei Repos, und beide Bäume sollen inhaltlich gleich sein:

- **`hydr/dataverse-modelling-mcp`** (öffentlich) — die Veröffentlichung. Hier entstehen die
  Releases samt Binary- und NuGet-Asset.
- **`crossvertise/dataverse-modelling-mcp`** (privat) — internes Arbeitsrepo. Es trägt die
  unbereinigte Commit-Historie und die PR-Diskussionen mit internen Bezügen; deshalb bleibt es
  privat.

**Releases werden nur im öffentlichen Repo gebaut.** `scripts/BINARY_VERSION` und der Default der
`release_repo`-Option zeigen in **beiden** Repos auf das öffentliche, der Launcher lädt die Binary
also immer von dort. Ein Tag im privaten Repo würde ein zweites, ungenutztes Release derselben
Version erzeugen — dort also nicht taggen.

## Keine Firmen-interna im öffentlichen Repo

Das öffentliche Repo ist bewusst anonymisiert (`#26`, `#35`). **Kein Bezeichner aus einer echten
Umgebung darf hinein** — weder in Doku, Code-Kommentare und Tool-Beschreibungen noch in Tests,
Commit-Messages oder PR-Texte. Das ist schon einmal passiert (v1.19.0 trug 77 `xv_*`- und 46
`Crossvertise*`-Vorkommen, bereinigt in 1.20.0), und ein Release ist danach nicht mehr
zurückzuholen.

| Statt | Nimm |
|---|---|
| Publisher-Prefix einer echten Org, z. B. `xv_` | `sample_` |
| Echte Solution-Namen | `Contoso…` (z. B. `ContosoForms`, `ContosoOrders`) |
| Echte Tabellen und Spalten | `sample_widget`, `sample_score`, … |
| Echte Namespaces von Code-Components | `Contoso.DocumentViewer` |
| GUIDs aus einer Umgebung | erkennbar synthetische (`11111111-1111-1111-1111-111111110001`) |
| Echte `versionnumber`-Werte, Abhängigkeitszahlen, Solution-Landschaften | synthetische Werte oder gar keine Zahl |

Standard-Dataverse-Tabellen (`account`, `contact`, `invoice`, `salesorder`, `product`) sind
unverfänglich und bleiben. Ebenso die **Publisher-Identität** des Repos in `LICENSE`, `SECURITY.md`,
`README.md`, `.claude-plugin/plugin.json` und `Dataverse.Setup.csproj` — die ist gewollt.

Vor dem Commit prüfen:

```bash
git grep -I -i -E "xv_|crossvertise|xvdev|xvstaging" -- docs src tests skills README.md
```

Treffer außerhalb der oben genannten Identitätsdateien sind ein Fehler. Dasselbe gilt für
`git log -p` auf die eigenen Commits, weil Commit-Messages nicht mehr korrigierbar sind, sobald
getaggt wurde.

Für Live-Tests gegen eine echte Umgebung heißt das: **Ergebnisse anonymisieren, bevor sie in eine
Datei wandern.** Die Testumgebung selbst darf natürlich echt sein — nur ihre Bezeichner gehören
nicht ins Repo. Eine Ausnahme ist der vorbestehende Integrationstest
`SolutionIntegrationTests.cs`, der eine echte MetadataId als Fixture braucht.

## Skills

Skills gehören nach **`skills/`** — dieses Verzeichnis wird mit dem Plugin ausgeliefert und ist damit
in jedem Repo aktiv, in dem das Plugin installiert ist. **Nicht** nach `.claude/skills/`: das
beschränkt sie auf dieses Repository, also ausgerechnet auf die eine Umgebung, in der Dataverse-Wissen
am wenigsten gebraucht wird.

Skills sind reine Markdown-Dateien ohne Binary-Bezug — eine Änderung an ihnen braucht keine neue
Binary. Damit Clients sie ziehen, muss aber die Plugin-Version steigen, und die ist an Binary und
Tool gekoppelt (siehe [Release-Prozess](#release-prozess-bei-jeder-änderung)). Also: alle drei
Versionsdateien hochzählen, mergen, taggen. Der Release baut dann eine funktional identische Binary
neu — der Preis dafür, dass die Versionsnummer eindeutig bleibt.

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

## Git-Konventionen

- **Feature-Branches**, nie direkt auf `master`. Namensschema: `feature/<thema>` für Features,
  `chore/<thema>` für Aufräumarbeiten, `fix/<thema>` für Bugfixes.
- Änderungen landen über **Pull Requests** gegen `master`. Ein logischer Schritt pro Commit,
  ein Thema pro Branch/PR.
- **Regelmäßig committen und pushen** — kleine Commits während der Arbeit statt eines großen
  Abwurfs am Ende.
- **Vor dem Start den aktuellen Branch prüfen** (`git branch --show-current`) und entscheiden, ob
  dort weitergearbeitet oder ein neuer Branch aufgemacht wird.
- Lokale Dev-Artefakte (`ws-log.json`, Screenshots) nicht committen — sie sind gitignored.

## Release-Prozess (bei jeder Änderung)

Bei Änderungen am MCP-Server immer — Schritte 1 bis 3 in **beiden** Repos, Schritt 4 nur im
öffentlichen (siehe [Zwei Repositories](#zwei-repositories)):
1. Feature-Branch erstellen und PR öffnen (gegen `master`)
2. Version erhöhen (minor bei neuen Features, patch bei Bugfixes) — in **allen drei** Dateien auf
   **exakt denselben** Wert, das ist gleichzeitig der Release-Tag:
   - `src/Dataverse.Setup/Dataverse.Setup.csproj` → `<Version>`
   - `scripts/BINARY_VERSION` (Hook und Launcher laden die Binary von `releases/download/v<BINARY_VERSION>/…`)
   - `.claude-plugin/plugin.json` → `version`

   Der Release-Workflow vergleicht alle drei mit dem Tag und bricht bei jeder Abweichung ab.
3. PR mergen
4. Git-Tag **im öffentlichen Repo** setzen (`git tag v<Version> && git push origin v<Version>`) → löst den Release-Workflow aus (NuGet-Publish + GitHub Release inkl. Server-Binary-Asset)

## Neue Version in einer laufenden Session nutzen

Kein Session-Neustart nötig: `.mcp.json` startet nicht die Binary direkt, sondern den Launcher
`scripts/run-server.ps1`, der bei jedem Serverstart die Binary auf die in `scripts/BINARY_VERSION`
gepinnte Version bringt und sie dann startet (stdio unverändert durchgereicht).

Nach einem Release also: Plugin aktualisieren (damit `BINARY_VERSION` stimmt), dann im
**`/mcp`-Menü** den Server `dataverse-modelling` **neu verbinden (Reconnect)** — fertig.

Details zur Binary-Auslieferung: siehe `docs/GH-RELEASE-SETUP.md`.
