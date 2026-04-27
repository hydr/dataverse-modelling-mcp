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
