# Solution Tools

Solution tools manage ALM (Application Lifecycle Management) using the **Dataverse Web API** solution endpoints and standard Dataverse actions.

## Tools

### `solution_list`

Lists all visible solutions in the environment.

**Example prompt:** "List all solutions in the environment"

---

### `solution_get`

Gets a solution by unique name, including its components.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `uniqueName` | string | Yes | Solution unique name |

**Example prompt:** "Show me all components in the MySolution solution"

---

### `solution_create`

Creates a new unmanaged solution.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `uniqueName` | string | Yes | Unique name (no spaces) |
| `displayName` | string | Yes | Friendly display name |
| `publisherUniqueName` | string | Yes | Publisher unique name |
| `version` | string | Yes | Version string (e.g. `1.0.0.0`) |

**Example prompt:** "Create an unmanaged solution called 'My Feature' with publisher 'mycompany'"

---

### `solution_export`

Exports a solution as a base64-encoded zip.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `uniqueName` | string | Yes | Solution unique name |
| `managed` | bool | No | `true` for managed export (default: `false`) |

**Example prompt:** "Export the MySolution solution as an unmanaged zip"

---

### `solution_import`

Imports a solution from a base64-encoded zip.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `zipBase64` | string | Yes | Base64-encoded solution zip |
| `overwriteUnmanaged` | bool | No | Whether to overwrite unmanaged customizations (default: `false`) |

**Example prompt:** "Import this solution zip: [base64 content]"

---

### `solution_add_component`

Adds a component to a solution.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `solutionUniqueName` | string | Yes | Target solution |
| `componentId` | GUID string | Yes | Component GUID |
| `componentType` | int | Yes | Component type code (1=Entity, 24=Workflow, 92=Role, etc.) |

**Example prompt:** "Add workflow abc123 (type 24) to the MySolution solution"

---

### `solution_remove_component`

Removes a component from a solution.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `solutionUniqueName` | string | Yes | Target solution |
| `componentId` | GUID string | Yes | Component GUID |
| `componentType` | int | Yes | Component type code |

**Example prompt:** "Remove the account entity (type 1) from MySolution"

---

### `solution_check_layers`

Checks the solution layers for a component.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `componentId` | GUID string | Yes | Component GUID |
| `componentType` | int | Yes | Component type code |

**Example prompt:** "Show me the solution layers for component abc123"

---

### `solution_remove_active_layer`

Removes the active customization layer for a component.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `componentId` | GUID string | Yes | Component GUID |
| `componentType` | int | Yes | Component type code |

**Example prompt:** "Remove the active layer for component abc123"

---

## Solution Pipelines (read-only)

Power Platform Pipeline **deployment** is not exposed as a tool — a headless MCP cannot mint the
Power-Apps-Maker token the pipeline backend requires, so committing a deploy is a Maker-UI (or PAC
CLI) operation. Use the file-based `solution_export` + `solution_import` path instead. The read-only
pipeline tools below remain available for discovery and monitoring. See the
[`solution-pipelines`](../../skills/solution-pipelines/SKILL.md) skill for the full background.

### `pipeline_list`

Lists all Power Platform Pipelines visible on a Pipeline-Host environment.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `pipelineHostOrgUrl` | string | Yes | Org URL of the Pipeline-Host environment |

### `pipeline_stages`

Lists the stages of a pipeline, including each stage's target deployment environment.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `pipelineHostOrgUrl` | string | Yes | Org URL of the Pipeline-Host environment |
| `pipelineId` | string | Yes | Pipeline GUID |

### `pipeline_environments`

Lists the deployment-environment mappings on a Pipeline-Host (Power-Platform env GUID → pipeline-internal mapping row).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `pipelineHostOrgUrl` | string | Yes | Org URL of the Pipeline-Host environment |

### `pipeline_run_status`

Gets the status of a deployment stage run (status, operation, validation results, error message).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `pipelineHostOrgUrl` | string | Yes | Org URL of the Pipeline-Host environment |
| `stageRunId` | string | Yes | Deployment stage-run GUID |

**Example prompt:** "List the pipelines on the host org and show the status of run abc123"
