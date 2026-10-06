# Business Process Flow Tools

Business process flows (BPF) put a process bar on top of model-driven forms and guide users through
stages. They are `workflow` rows with `category = 4`. These tools author them from a JSON definition
— no designer needed — and manage their lifecycle and running instances through the Dataverse Web API
v9.2. The definition shape and every validation code: `skills/business-process-flows/SKILL.md`. The
XAML format: `docs/business-process-flows-reference.md`.

## Authoring

### `bpf_list`

Lists business process flows, ordered by table and process order.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `primaryEntity` | string | No | Only processes on this table |
| `includeTaskFlows` | bool | No | Include task flows (business process type 1). Default false |

Returns id, name, `uniqueName` (= logical name of the instance table), table, activation state,
process order, type, managed flag.

---

### `bpf_get_definition`

Reads a process as an editable definition. Write it back only when `fullyUnderstood` is true.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `processId` | GUID | Yes | The process |

---

### `bpf_validate_definition`

Validates a definition without writing: model rules, the designer's rules, and tables, columns,
relationships and referenced workflows/actions/flows against live metadata.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `definitionJson` | string | One of both | The definition inline |
| `definitionFile` | string | One of both | Path to a `.json` file with the definition — preferred for anything large |

---

### `bpf_create`

Creates a process. Nothing is created while validation reports an error.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | string | Yes | Display name |
| `definitionJson` / `definitionFile` | string | Yes (one) | The definition |
| `uniqueName` | string | No | `<prefix>_<name>`; becomes the instance table's logical name. Derived from `name` when omitted |
| `description` | string | No | Description |
| `solutionUniqueName` | string | No | Create it in this solution; its publisher prefix is used for a derived `uniqueName` |
| `activate` | bool | No | Activate right away. The first activation takes about two minutes |

**Example prompt:** "Create a business process flow on lead with the stages Qualify, Develop and Close, where Develop moves to opportunity."

---

### `bpf_set_definition`

Rewrites a process — also an activated one, in place. Stages, steps and triggers without an id are
matched to the existing ones so running instances keep their stage.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `processId` | GUID | Yes | The process |
| `definitionJson` / `definitionFile` | string | Yes (one) | The definition |
| `dryRun` | bool | No | Validate and report the change in `diff` without writing |
| `backupFile` | string | No | Write the previous XAML here instead of returning it |

---

### `bpf_update`

Renames a process or changes its description.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `processId` | GUID | Yes | The process |
| `name` | string | No | New display name |
| `description` | string | No | New description |

---

### `bpf_set_state`

Activates or deactivates a process. The first activation creates the instance table (about two
minutes, synchronous). An active process is applied to new records of its table automatically.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `processId` | GUID | Yes | The process |
| `activate` | bool | Yes | `true` to activate |

---

### `bpf_delete`

Deletes a process with its instance table and all instances. Irreversible.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `processId` | GUID | Yes | The process |
| `deactivateFirst` | bool | No | Deactivate an activated process first (otherwise refused with `0x8004500f`) |

---

### `bpf_set_order`

Sets the process order of a table. A new record gets the first process its user has access to.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `primaryEntity` | string | Yes | The table |
| `processIds` | string | Yes | Process GUIDs in the wanted order, comma-separated. Unlisted ones follow |

---

### `bpf_grant_access`

Grants security roles the privileges on the process's instance table (organisation depth) — what the
designer's "Edit security roles" does. The process must have been activated once.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `processId` | GUID | Yes | The process |
| `roleIds` | string | Yes | Role GUIDs, comma-separated |
| `readOnly` | bool | No | Grant Read only |

---

### `bpf_export_xaml`

Exports the raw XAML — a restore point.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `processId` | GUID | Yes | The process |
| `file` | string | No | Write the XAML to this path |

---

### `bpf_restore_xaml`

Writes a previously exported XAML back verbatim.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `processId` | GUID | Yes | The process |
| `xaml` / `xamlFile` | string | Yes (one) | The XAML, or a path to it |

## Instances

### `bpf_instance_list`

Lists the instances running over a record, across all processes — the most recently touched first,
which is the one the form shows.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `entity` | string | Yes | The record's table |
| `recordId` | GUID | Yes | The record |

---

### `bpf_instance_start`

Starts a process on a record, or switches the record to it.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `processId` | GUID | Yes | The process |
| `recordId` | GUID | Yes | A record of the primary table |
| `stageId` | GUID | No | Start stage on the main path; the first stage by default |

---

### `bpf_instance_move`

Moves an instance: back to any passed stage, or forward to the stage following the active one (its
next stage or a branch target). Keeps `traversedpath` consistent. Branch conditions are not evaluated.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `processId` | GUID | Yes | The process |
| `instanceId` | GUID | Yes | The instance |
| `stageId` | GUID | Yes | The target stage |
| `recordId` | GUID | For another table | The record of the target stage's table |

---

### `bpf_instance_set_status`

| Parameter | Type | Required | Description |
|---|---|---|---|
| `processId` | GUID | Yes | The process |
| `instanceId` | GUID | Yes | The instance |
| `status` | string | Yes | `active`, `finished` (last stage only) or `aborted` |
