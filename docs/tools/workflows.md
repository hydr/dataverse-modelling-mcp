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

## Authoring: the logic as a definition

These tools do not work with XAML but with the declarative JSON model. The format details are in
`skills/classic-workflows/SKILL.md`, the XAML behind it in `docs/classic-workflows-reference.md`.

### `workflow_explain`

Explains a workflow in prose: trigger, mode, scope and the logic as an indented structure. The
starting point when you want to understand a process someone else wrote.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `workflowId` | GUID string | Yes | The workflow |

At the end there is a **"Not understood"** section if the parser could not map some constructs. The
explanation is then incomplete, and the workflow must not be overwritten.

**Example prompt:** "What does the payment reminder workflow do?"

---

### `workflow_get_definition`

Returns the logic as editable JSON.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `workflowId` | GUID string | Yes | The workflow |

**`fullyUnderstood` is the value that matters.** If it is `false`, `unrecognised` names the parts that
cannot be mapped — writing back would lose them. Make the change in the designer instead.

**Example prompt:** "Give me the definition of workflow abc123 as JSON"

---

### `workflow_validate_definition`

Checks a definition **without** writing anything: model rules, existence of tables and attributes,
and the signature of the referenced code activities.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `definitionJson` | string | One of the two | The definition as JSON |
| `definitionFile` | string | One of the two | Path to a local `.json` file containing the definition |

Every issue carries `code`, `path`, `problem` and `fix`. The code table is in the skill.

**Example prompt:** "Check this workflow definition before we write it"

---

### `workflow_set_definition`

Writes the logic as XAML. Writes **only** if all three checks are clean (model, metadata, self-check
of the generated XAML).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `workflowId` | GUID string | Yes | The target workflow |
| `definitionJson` | string | One of the two | The definition as JSON |
| `definitionFile` | string | One of the two | Path to a local `.json` file containing the definition |
| `reactivate` | bool | No | Deactivate an activated workflow, write, then activate it again |
| `dryRun` | bool | No | Only check and report in `diff`; write nothing |
| `backupFile` | string | No | Path where the previous XAML is stored |

The response contains the assigned `stepIds` and the previous XAML — in `backup`, or as a path in
`backupFile` if you supplied one. An activated workflow is rejected without `reactivate=true`
(`WF210`). **Set the mode beforehand** — it determines whether the XAML may contain persistence
points.

**For real workflows use `definitionFile` and `backupFile`, not the inline variants.** A definition
with an embedded signature image runs to ~28,000 characters, the XAML behind it to ~180,000. Passing it
inline means someone retypes it, and characters flip in the process. The error is silent — the JSON
stays valid, all three checks pass cleanly, and only the base64 block it transports is broken. That is
exactly how, in August 2026, the logo in the e-mail signature of two workflows was destroyed; it only
surfaced when the PNG checksums were recomputed.

**Example prompt:** "Write this logic into workflow abc123"

---

### `workflow_restore_xaml`

Restores a previously exported XAML unchanged — the undo for a failed write.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `workflowId` | GUID string | Yes | The workflow |
| `xaml` | string | One of the two | The XAML to restore |
| `xamlFile` | string | One of the two | Path to the file containing the XAML |

`xamlFile` is the normal case here: "restore unchanged" does not survive retyping a string whose
length runs to six digits. It fits directly onto the `backupFile` of `workflow_set_definition` and onto
the file that an oversized export was offloaded to.

**Example prompt:** "Restore the earlier XAML in workflow abc123"

---

### `workflow_diagnose_activation`

Finds out **which part** of a definition cannot be activated.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `primaryEntity` | string | Yes | Logical name of the primary entity |
| `definitionJson` | string | One of the two | The definition that will not activate |
| `definitionFile` | string | One of the two | Path to a local `.json` file containing the definition |
| `isRealtime` | bool | No | `true` for a real-time process (default `false`) |

Activation errors are practically useless: `0x80040216` literally means "unexpected error". The tool
therefore writes subsets of the definition into throwaway workflows and activates each one — first
step by step, then, for a condition, case by case — until the culprit is isolated. The throwaway
workflows are deleted again afterwards.

The response contains `culprit` (the finding in words) and `attempts` (all attempts in order, as
evidence). If no single step is to blame, the tool says so as well — then the cause lies in the
interaction, typically a reference to a record that another step creates.

**Example prompt:** "Why can't this definition be activated?"

---

## Code activities

### `workflow_list_activities`

Lists the custom workflow activities available in the environment.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `nameFilter` | string | No | Substring of the name, e.g. `"msdyncrmWorkflowTools"` |

**Example prompt:** "Which code activities are there for teams?"

---

### `workflow_get_activity_parameters`

The parameters of an activity: technical name (`dependencyPropertyName`), `dataType` and, for
lookups, the allowed target entities (`entityNames`).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `pluginTypeId` | GUID string | Yes | From `workflow_list_activities` |

Take the `assemblyQualifiedName` **verbatim** — it carries the real `PublicKeyToken`.

**Example prompt:** "What parameters does CheckUserInTeam have?"
