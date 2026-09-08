---
name: modeling-patterns
description: Patterns fuer das Anlegen und Aendern von Dataverse-Tabellen, Spalten und Relationships (One-to-Many, Many-to-Many, Lookup-Strategien, Naming-Konventionen). Use when the user is designing or modifying a Dataverse / Dynamics 365 schema, adding tables/columns, planning a data model, or considering relationship choices.
---

# Dataverse-Modellierungs-Patterns

Das Plugin stellt Tools bereit, um Dataverse-Schemas zu lesen, zu vergleichen
und zu veraendern. Default-Vorgehen: erst lesen, dann aendern.

## Workflow fuer Neuanlagen

1. **Schema verstehen** — vorhandene Tabellen und Naming-Konventionen pruefen, bevor neue angelegt werden.
2. **Naming-Konvention** — ein konsistenter Publisher-Praefix (hier im Beispiel `sample_`) fuer Custom-Entitaeten und -Spalten. Tabellen Singular, Spalten in `lower_snake_case_logical_name`.
3. **Primary Column** waehlen — fuer Lookup-fokussierte Tabellen oft `sample_name`, fuer Order-/Transaktions-Tabellen ein business-relevanter Identifier.
4. **Relationships planen** — One-to-Many vs Many-to-Many bewusst entscheiden:
   - One-to-Many: 1 Parent referenziert von N Children. Default.
   - Many-to-Many: nur wenn beide Seiten symmetrisch und keine Zusatzattribute pro Relation. Sonst lieber Junction-Entity.
5. **Cascade-Behaviors** beruecksichtigen — bei `delete: cascade` immer pruefen ob Children wirklich mitgeloescht werden sollen.

## Empfehlungen

- Vor dem Schema-Change ein *Trockenlauf* mit "diff"-Tool, falls vorhanden.
- Neue Tabellen sollten in einer dedizierten Standard-Solution landen, nicht im Default Solution.
- `picklist`-Felder (Choices) global anlegen, nicht lokal pro Tabelle, wenn Wiederverwendung absehbar.

## Fallen, die Zeit kosten

Alles hier gegen eine echte Umgebung oder die Microsoft-Doku belegt.

### Managed Properties sind keine Booleans

`IsValidForAdvancedFind`, `IsAuditEnabled`, `IsCustomizable`, `IsRenameable`,
`CanModifyAdditionalSettings`, `IsGlobalFilterEnabled`, `IsSortableEnabled` und `RequiredLevel` sind
auf einer **Spalte** Managed Properties und erwarten ein Objekt. `column_add`, `column_update` und
`table_update` normalisieren einen nackten Wert inzwischen selbst und melden das unter
`normalizedManagedProperties` — die Tabelle mit den `ManagedPropertyLogicalName`-Werten steht in
[`docs/tools/tables.md`](../../docs/tools/tables.md#managed-properties).

Der Unterschied, der leicht übersehen wird: `IsValidForAdvancedFind` ist auf einer **Spalte** eine
Managed Property, auf einer **Tabelle** ein gewöhnliches `Edm.Boolean`.

### `componentId` ist immer die `objectid`, nie die `solutioncomponentid`

Für Tabellen und Spalten also die `MetadataId`. Die `solutioncomponentid` ist der Primärschlüssel der
Mitgliedschaftszeile und führt in `solution_remove_component` zu
`0x8004f021 Cannot find solution component`. `solution_get` liefert beides getrennt.

### `rootcomponentbehavior` entscheidet, welche Solution die Änderung trägt

0 = Unterkomponenten einbinden (Formulare und Spalten reisen automatisch mit und bekommen **keine**
eigene Mitgliedschaftszeile), 1 = nicht einbinden, 2 = nur als Shell. Dieselbe Tabelle liegt
regelmäßig in mehreren Solutions mit unterschiedlichem Verhalten — deshalb ist „die Spalte der
Solution hinzufügen" in der einen ein No-op und in der nächsten nötig. `solution_get` gibt den Wert
pro Root-Komponente aus, `solution_add_component` sagt hinterher, ob wirklich eine Zeile entstanden
ist.

### Ein PCF-Update braucht einen Versionssprung

Ein Solution-Import übernimmt ein Code-Component nur, wenn die Version im `ControlManifest`
**höher** ist als die gespeicherte — und meldet in beiden Fällen Erfolg. `solution_import` vergleicht
das nach dem Import und warnt (`customControlWarnings`). `pac pcf push` umgeht die Versionsprüfung
bewusst, deshalb „funktioniert" es in genau dieser Lage.

### Formulare nicht über rohe FormXML anfassen

`form_get` liefert die Struktur (Tabs → Spalten → Sections → Zellen → Controls) statt XML und löst
dabei auf, welches Code-Component an einer Zelle hängt — in der Zelle steht nur ein generischer
Classid plus `uniqueid`, der Rest in einer separaten `controlDescription`. `form_add_control`,
`form_replace_control` und `form_remove_tab` kapseln die vier Details, an denen man sonst scheitert:
Publisher-Prefix im Component-Namen, alle drei Form-Faktoren, generischer Classid samt `uniqueid`,
und eine gebundene Spalte muss vorher publiziert sein. Einzelheiten in
[`docs/tools/forms.md`](../../docs/tools/forms.md).

### `systemform` hat kein `modifiedon`

Ein `$select=modifiedon` quittiert Dataverse mit
`0x80060888 Could not find a property named 'modifiedon'`. `modifiedby` und `_modifiedby_value`
fehlen ebenfalls. Zum Vergleichen von Ständen gibt es `versionnumber` (BigInt), `version`,
`overwritetime` und `publishedon`.

### Ein Rücklesen direkt nach dem Schreiben beweist nichts

**Ein Rücklesen ohne Wartezeit bzw. ohne Publish ist kein Beweis dafür, dass der Schreibvorgang
verworfen wurde.** Es sind zwei verschiedene Mechanismen, und sie verhalten sich unterschiedlich —
gegen eine echte Umgebung gemessen:

| Schreibvorgang | Rücklesen ohne Publish |
|---|---|
| `systemform.formxml` (`PATCH`, HTTP 204) | **wird nie aktuell** — nach 120 s noch alte `formxml` und alte `versionnumber` |
| dasselbe Formular nach `publish_customizations` | sofort aktuell, `versionnumber` sprang 12345678 → 23456789 |
| Metadaten anlegen | sichtbar nach ~3 s |
| Metadaten ändern (`PUT`) | aktuell nach ~3 s |
| Metadaten löschen | verschwunden nach ~16 s |

**Beim Formular ist es kein Cache.** Ein `PATCH` schreibt die *unpublizierte* Fassung, ein `GET`
liefert die *publizierte* — bis zum Publish sind das schlicht zwei verschiedene Stände. Warten hilft
hier nicht, nur `publish_customizations(entities='<tabelle>')`.

**Bei Metadaten ist es Eventual Consistency.** `EntityDefinitions` zieht von selbst nach, ganz ohne
Publish. Der Publish wird trotzdem gebraucht, damit *Clients* die Änderung sehen.

Praktisch: nach einem Formular-Schreibvorgang publizieren und dann lesen; nach einem
Metadaten-Schreibvorgang kurz warten und erneut lesen. Die metadatenschreibenden Tools haben dafür
ein `publish`-Flag und melden im Ergebnis, wenn sie nicht publiziert haben.

## Lokales MCP

Dieses MCP laeuft als lokale C#-Konsolenanwendung. Die Binary wird nicht direkt
gestartet, sondern ueber den Launcher `scripts/run-server.ps1`, der sie bei jedem
Serverstart auf die in `scripts/BINARY_VERSION` gepinnte Version bringt und aus
dem passenden GitHub-Release laedt. Falls der Start fehlschlaegt, pruefe
`release_repo` und den `${CLAUDE_PLUGIN_DATA}/bin/`-Pfad.
