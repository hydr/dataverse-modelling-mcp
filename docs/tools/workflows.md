# Classic Workflow Tools

Classic Workflows (also called Background Workflows) are the legacy automation type in Dataverse. They run XAML-defined logic on entity records. These tools use the **Dataverse Web API v9.2** `/workflows` endpoint (category = 0).

## Tools

### `workflow_list`

Lists Classic Workflows in the environment.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filter` | string | No | OData filter expression (e.g. `"name eq 'My Workflow'"`) |
| `top` | int | No | Max results to return (default: 50) |

**Example prompt:** "List all classic workflows for the account entity"

---

### `workflow_get`

Gets the full definition of a workflow, including its raw XAML.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `workflowId` | GUID string | Yes | The workflow's unique identifier |

**Example prompt:** "Show me the XAML of workflow 3fa85f64-5717-4562-b3fc-2c963f66afa6"

---

### `workflow_create`

Creates a new Classic Workflow in draft state.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Display name |
| `primaryEntity` | string | Yes | Logical name of the target entity (e.g. `account`) |
| `description` | string | No | Optional description |

**Example prompt:** "Create a workflow named 'Send Welcome Email' for the contact entity"

---

### `workflow_update`

Updates properties of an existing workflow via OData PATCH.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `workflowId` | GUID string | Yes | The workflow to update |
| `propertiesJson` | JSON object | Yes | Properties to change (e.g. `{"name": "New Name"}`) |

**Example prompt:** "Rename workflow abc123 to 'Updated Workflow Name'"

---

### `workflow_set_state`

Activates or deactivates a workflow.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `workflowId` | GUID string | Yes | The workflow to change |
| `activate` | bool | Yes | `true` to activate, `false` to deactivate |

**Example prompt:** "Activate the workflow with ID abc123"

---

### `workflow_delete`

Deletes one or more workflows, **irreversibly**. Export the XAML first if the logic might be needed
again (`workflow_export_xaml`).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `workflowIds` | string | Yes | One workflow GUID, or several separated by commas |
| `deactivateFirst` | bool | No | Deactivate an activated workflow before deleting it (default `false`) |

Activating a workflow makes Dataverse store a second row (`type=2`, the **activation copy**) that
neither deactivating nor deleting the definition removes — which is how an environment used for
testing fills up with leftovers. This tool deletes those copies along with the definition.

An activated workflow is skipped unless `deactivateFirst=true`, so a running process cannot be removed
by a typo. The result lists what was deleted and what was skipped, with the reason.

**Example prompt:** "Delete all the ZZ test workflows"

---

### `workflow_assign`

Reassigns a workflow to a different user or team.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `workflowId` | GUID string | Yes | The workflow to reassign |
| `ownerType` | string | Yes | `"user"` or `"team"` |
| `ownerId` | GUID string | Yes | The new owner's GUID |

**Example prompt:** "Assign workflow abc123 to user def456"

---

### `workflow_validate`

Checks a workflow for common issues and returns a validation report.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `workflowId` | GUID string | Yes | The workflow to validate |

Checks performed:
- Has XAML definition
- Has a primary entity set
- Is in activated state

**Example prompt:** "Validate the workflow abc123 and tell me if there are any issues"

---

## Authoring: die Logik als Definition

Diese Tools arbeiten nicht mit XAML, sondern mit dem deklarativen JSON-Modell. Die Formatdetails
stehen in `skills/classic-workflows/SKILL.md`, das XAML dahinter in
`docs/classic-workflows-reference.md`.

### `workflow_explain`

Erklärt einen Workflow in Prosa: Trigger, Modus, Scope und die Logik als eingerückte Struktur.
Der Einstieg, wenn man einen fremden Prozess verstehen will.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `workflowId` | GUID string | Yes | Der Workflow |

Am Ende steht ein Abschnitt **„Not understood"**, falls der Parser Konstrukte nicht abbilden konnte.
Dann ist die Erklärung unvollständig und der Workflow darf nicht überschrieben werden.

**Example prompt:** „Was macht der Workflow Zahlungserinnerung-Email verschicken?"

---

### `workflow_get_definition`

Gibt die Logik als editierbares JSON zurück.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `workflowId` | GUID string | Yes | Der Workflow |

**`fullyUnderstood` ist der entscheidende Wert.** Ist er `false`, nennt `unrecognised` die nicht
abbildbaren Teile — ein Rückschreiben würde sie verlieren. Dann im Designer ändern.

**Example prompt:** „Gib mir die Definition von Workflow abc123 als JSON"

---

### `workflow_validate_definition`

Prüft eine Definition, **ohne** etwas zu schreiben: Modellregeln, Existenz von Tabellen und
Attributen, und die Signatur der referenzierten Codeaktivitäten.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `definitionJson` | string | Eines von beiden | Die Definition als JSON |
| `definitionFile` | string | Eines von beiden | Pfad zu einer lokalen `.json`-Datei mit der Definition |

Jeder Befund trägt `code`, `path`, `problem` und `fix`. Die Codetabelle steht im Skill.

**Example prompt:** „Prüfe diese Workflow-Definition, bevor wir sie schreiben"

---

### `workflow_set_definition`

Schreibt die Logik als XAML. Schreibt **nur**, wenn alle drei Prüfungen sauber sind
(Modell, Metadaten, Selbsttest des erzeugten XAML).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `workflowId` | GUID string | Yes | Der Zielworkflow |
| `definitionJson` | string | Eines von beiden | Die Definition als JSON |
| `definitionFile` | string | Eines von beiden | Pfad zu einer lokalen `.json`-Datei mit der Definition |
| `reactivate` | bool | No | Aktivierten Workflow deaktivieren, schreiben, wieder aktivieren |
| `dryRun` | bool | No | Nur prüfen und in `diff` berichten, nichts schreiben |
| `backupFile` | string | No | Pfad, unter dem das vorherige XAML abgelegt wird |

Die Antwort enthält die vergebenen `stepIds` und das vorherige XAML — in `backup`, oder als Pfad in
`backupFile`, wenn du einen angegeben hast. Ein aktivierter Workflow wird ohne `reactivate=true`
abgelehnt (`WF210`). Den **Modus vorher setzen** — er bestimmt, ob das XAML Persistenzpunkte enthalten
darf.

**Bei echten Workflows `definitionFile` und `backupFile` nehmen, nicht die Inline-Varianten.** Eine
Definition mit eingebettetem Signaturbild liegt bei ~28.000 Zeichen, das XAML dahinter bei ~180.000.
Inline übergeben heißt: jemand tippt das ab, und dabei kippen Zeichen. Der Fehler ist still — das JSON
bleibt gültig, alle drei Prüfungen laufen sauber durch, kaputt ist nur der base64-Block, den es
transportiert. Genau so ist im August 2026 in zwei Workflows das Logo in der E-Mail-Signatur
zerstört worden; aufgefallen ist es erst beim Nachrechnen der PNG-Prüfsummen.

**Example prompt:** „Schreibe diese Logik in Workflow abc123"

---

### `workflow_restore_xaml`

Setzt ein früher exportiertes XAML unverändert zurück — das Rückgängig zu einem misslungenen
Schreibvorgang.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `workflowId` | GUID string | Yes | Der Workflow |
| `xaml` | string | Eines von beiden | Das wiederherzustellende XAML |
| `xamlFile` | string | Eines von beiden | Pfad zu der Datei, in der das XAML liegt |

`xamlFile` ist hier der Normalfall: „unverändert zurücksetzen" überlebt das Abschreiben eines
sechsstelligen Strings nicht. Es passt direkt auf `backupFile` von `workflow_set_definition` und auf
die Datei, in die ein zu großer Export ausgelagert wurde.

**Example prompt:** „Stelle das XAML von vorhin in Workflow abc123 wieder her"

---

### `workflow_diagnose_activation`

Findet heraus, **welcher Teil** einer Definition sich nicht aktivieren lässt.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `primaryEntity` | string | Yes | Logischer Name der Primärentität |
| `definitionJson` | string | Eines von beiden | Die Definition, die nicht aktiviert |
| `definitionFile` | string | Eines von beiden | Pfad zu einer lokalen `.json`-Datei mit der Definition |
| `isRealtime` | bool | No | `true` bei einem Echtzeitprozess (Standard `false`) |

Aktivierungsfehler sind praktisch nutzlos: `0x80040216` heißt wörtlich „unerwarteter Fehler". Das Tool
schreibt daher Teilmengen der Definition in Wegwerf-Workflows und aktiviert jede — erst Schritt für
Schritt, dann bei einer Bedingung Fall für Fall — bis der Verursacher isoliert ist. Die
Wegwerf-Workflows werden wieder gelöscht.

Die Antwort enthält `culprit` (den Befund in Worten) und `attempts` (alle Versuche in Reihenfolge, als
Beleg). Findet sich kein einzelner Schritt, sagt das Tool auch das — dann liegt es am Zusammenspiel,
typischerweise an einem Verweis auf einen Datensatz, den ein anderer Schritt anlegt.

**Example prompt:** „Warum lässt sich diese Definition nicht aktivieren?"

---

## Codeaktivitäten

### `workflow_list_activities`

Listet die verfügbaren Custom Workflow Activities der Umgebung.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `nameFilter` | string | No | Teilstring des Namens, z. B. `"msdyncrmWorkflowTools"` |

**Example prompt:** „Welche Codeaktivitäten gibt es für Teams?"

---

### `workflow_get_activity_parameters`

Die Parameter einer Aktivität: technischer Name (`dependencyPropertyName`), `dataType` und bei
Lookups die erlaubten Zielentitäten (`entityNames`).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `pluginTypeId` | GUID string | Yes | Aus `workflow_list_activities` |

Den `assemblyQualifiedName` **wörtlich** übernehmen — er trägt den echten `PublicKeyToken`.

**Example prompt:** „Welche Parameter hat CheckUserInTeam?"
