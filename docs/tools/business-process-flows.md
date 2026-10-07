# Business Process Flow Tools

Business process flows (BPF) put a process bar on top of model-driven forms and guide users through
stages. They are `workflow` rows with `category = 4`. These tools author them from a JSON definition
— no designer needed — and manage their lifecycle and running instances through the Dataverse Web API
v9.2. The definition shape and every validation code: `skills/business-process-flows/SKILL.md`. The
XAML format: `docs/business-process-flows-reference.md`.

## Authoring

### `bpf_list`

Lists business process flows, per table in the order the platform applies them to new records:
by process order, processes without one last, equal orders by name (the platform defines no order
between those).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `primaryEntity` | string | No | Only processes on this table |
| `includeTaskFlows` | bool | No | Include task flows (business process type 1). Default false |

Returns id, name, `uniqueName` (= logical name of the instance table), table, activation state,
process order, type, managed flag.

---

### `bpf_get_definition`

Reads a process as an editable definition. Write it back only when `fullyUnderstood` is true.
Besides `definition`, the response has `path`: every stage in order with its id, its table (also
where the definition leaves it implicit), its `next` and its branch targets.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `processId` | GUID | Yes | The process |

---

### `bpf_validate_definition`

Validates a definition without writing: model rules, the designer's rules, and tables, columns,
literal types, relationships and referenced workflows/actions/flows against live metadata. With
`processId` it checks the definition as a replacement for that process, exactly as
`bpf_set_definition` would: existing ids adopted, removed stages checked for instances.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `definitionJson` | string | One of both | The definition inline |
| `definitionFile` | string | One of both | Path to a `.json` file with the definition — preferred for anything large |
| `processId` | GUID | No | The process the definition is meant to replace |

---

### `bpf_find_relationships`

Lists the 1:N relationships a stage on `toEntity` can be reached through from a stage on
`fromEntity` — the lookups on `toEntity` that point at `fromEntity`. `name` goes into the stage's
`relationship.name`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `fromEntity` | string | Yes | Table the process comes from, e.g. `lead` |
| `toEntity` | string | Yes | Table of the new stage, e.g. `opportunity` |

**Example prompt:** "Which relationship gets a lead stage over to opportunity?"

---

### `bpf_create`

Creates a process. Nothing is created while validation reports an error.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | string | Yes | Display name |
| `definitionJson` / `definitionFile` | string | Yes (one) | The definition |
| `uniqueName` | string | No | `<prefix>_<name>`; becomes the instance table's logical name. Derived from `name` when omitted |
| `description` | string | No | Description |
| `solutionUniqueName` | string | No | Create it in this solution; its publisher prefix is used for a derived `uniqueName` (a given one with another prefix is a warning). On activation the instance table is added as well — the solution exports only with it |
| `activate` | bool | No | Activate right away. The first activation takes about two minutes. If it fails, the draft stays and the response still carries its `processId` |

**Example prompt:** "Create a business process flow on lead with the stages Qualify, Develop and Close, where Develop moves to opportunity."

---

### `bpf_set_definition`

Rewrites a process — also an activated one, in place. Stages, steps and triggers without an id are
matched to the existing ones (stages by name and table) so running instances keep their stage. **To
rename a stage, keep its `stageId`** — otherwise it counts as removed and added (BPF062). Removing a
stage that active instances stand on is refused (BPF060) unless `allowStageRemoval` is set.

`diff` lists every change, one line each (`+` added, `-` removed, `~` changed): stages, renames,
tables, `next`, branches, relationships, steps, labels, required flags, triggers.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `processId` | GUID | Yes | The process |
| `definitionJson` / `definitionFile` | string | Yes (one) | The definition |
| `dryRun` | bool | No | Validate and report the change in `diff` without writing. New stages and steps show no id — they get one on the real write. No backup is returned |
| `backupFile` | string | No | Write the previous XAML here instead of returning it. Written before the change; if the path cannot be written, nothing is changed |
| `allowStageRemoval` | bool | No | Remove stages even if active instances stand on them |

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
| `solutionUniqueName` | string | No | On activation, add the instance table to this solution (the one holding the process) — needed for its export |

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
Listed processes come first; the others follow in their current order as `bpf_list` shows it. Every
process gets a distinct number, so processes without an order or with equal ones get a definite place.
A process created with `bpf_create` is placed after the existing ones.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `primaryEntity` | string | Yes | The table |
| `processIds` | string | Yes | Process GUIDs in the wanted order, comma-separated. Unlisted ones follow |

---

### `bpf_grant_access`

Grants security roles the privileges on the process's instance table (organisation depth) — what the
designer's "Edit security roles" does. The process must have been activated once. Users who create
records need Create, or the automatic start of the process fails. Additive: privileges a role already
has stay, `readOnly` takes none away, and there is no revoke.

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

Starts a process on a record. A record holds one instance per process: if it already has one, that
one is returned with `created: false` and nothing changes. An active process usually starts itself
on new records, shortly after they are created.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `processId` | GUID | Yes | The process |
| `recordId` | GUID | Yes | A record of the primary table |
| `stageId` | GUID | No | Start stage on the main path within the primary table; the first stage by default |

---

### `bpf_instance_move`

Moves an active instance: back to any passed stage, or forward to the stage following the active one
(its next stage or a branch target). Keeps `traversedpath` consistent. Branch conditions are not
evaluated. A finished or aborted instance has to be reactivated first.

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
