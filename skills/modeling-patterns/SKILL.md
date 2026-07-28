---
name: modeling-patterns
description: Patterns fuer das Anlegen und Aendern von Dataverse-Tabellen, Spalten und Relationships (One-to-Many, Many-to-Many, Lookup-Strategien, Naming-Konventionen). Use when the user is designing or modifying a Dataverse / Dynamics 365 schema, adding tables/columns, planning a data model, or considering relationship choices.
---

# Dataverse-Modellierungs-Patterns

Das Plugin stellt Tools bereit, um Dataverse-Schemas zu lesen, zu vergleichen
und zu veraendern. Default-Vorgehen: erst lesen, dann aendern.

## Workflow fuer Neuanlagen

1. **Schema verstehen** — vorhandene Tabellen und Naming-Konventionen pruefen, bevor neue angelegt werden.
2. **Naming-Konvention** — Crossvertise-Standard ist Praefix `cv_` (bzw. `sample_` historisch) fuer Custom-Entitaeten und -Spalten. Tabellen Singular, Spalten in `lower_snake_case_logical_name`.
3. **Primary Column** waehlen — fuer Lookup-fokussierte Tabellen oft `cv_name`, fuer Order-/Transaktions-Tabellen ein business-relevanter Identifier.
4. **Relationships planen** — One-to-Many vs Many-to-Many bewusst entscheiden:
   - One-to-Many: 1 Parent referenziert von N Children. Default.
   - Many-to-Many: nur wenn beide Seiten symmetrisch und keine Zusatzattribute pro Relation. Sonst lieber Junction-Entity.
5. **Cascade-Behaviors** beruecksichtigen — bei `delete: cascade` immer pruefen ob Children wirklich mitgeloescht werden sollen.

## Empfehlungen

- Vor dem Schema-Change ein *Trockenlauf* mit "diff"-Tool, falls vorhanden.
- Neue Tabellen sollten in der Standard-Solution `Crossvertise` landen, nicht im Default Solution.
- `picklist`-Felder (Choices) global anlegen, nicht lokal pro Tabelle, wenn Wiederverwendung absehbar.

## Lokales MCP

Dieses MCP laeuft als lokale C#-Konsolenanwendung. Die Binary wird nicht direkt
gestartet, sondern ueber den Launcher `scripts/run-server.ps1`, der sie bei jedem
Serverstart auf die in `scripts/BINARY_VERSION` gepinnte Version bringt und aus
dem passenden GitHub-Release laedt. Falls der Start fehlschlaegt, pruefe
`release_repo` und den `${CLAUDE_PLUGIN_DATA}/bin/`-Pfad.
