# Cloud Flow Tools

Cloud Flows are Power Automate flows stored in the Power Platform environment. These tools use the **Power Automate Flow API** (`https://{region}.api.flow.microsoft.com`).

## Tools

### `flow_list`

Lists Cloud Flows in the environment.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filter` | string | No | OData filter string |
| `top` | int | No | Max results (default: 50) |

**Example prompt:** "List all cloud flows in the environment"

---

### `flow_get`

Gets the full definition of a Cloud Flow, including trigger and action definitions.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `flowId` | string | Yes | The flow GUID |

**Example prompt:** "Show me the full definition of flow abc123"

---

### `flow_create`

Creates a new Cloud Flow.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `displayName` | string | Yes | Display name |
| `definitionJson` | JSON | Yes | Flow definition object (triggers + actions JSON) |

**Example prompt:** "Create a new flow named 'Notify on New Lead' with this definition: {...}"

---

### `flow_update`

Updates the definition of an existing Cloud Flow.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `flowId` | string | Yes | The flow GUID |
| `definitionJson` | JSON | Yes | Updated definition object |

**Example prompt:** "Update flow abc123 with this new definition"

---

### `flow_set_state`

Enables or disables a Cloud Flow.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `flowId` | string | Yes | The flow GUID |
| `enable` | bool | Yes | `true` to enable, `false` to disable |

**Example prompt:** "Disable flow abc123"

---

### `flow_get_runs`

Gets the run history of a Cloud Flow.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `flowId` | string | Yes | The flow GUID |
| `top` | int | No | Max runs to return (default: 25) |
| `filter` | string | No | OData filter (e.g. by status) |

**Example prompt:** "Show me the last 10 runs of flow abc123 and whether they succeeded"

---

### `flow_describe`

Returns a human-readable description of a flow's trigger and actions.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `flowId` | string | Yes | The flow GUID |

**Example prompt:** "Describe what flow abc123 does in plain English"
