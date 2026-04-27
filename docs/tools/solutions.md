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

### `solution_deploy_pipeline`

Triggers a Power Platform Pipeline deployment.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `pipelineId` | string | Yes | Pipeline GUID |
| `stageId` | string | Yes | Target stage GUID |

**Example prompt:** "Deploy pipeline abc123 to stage def456"
