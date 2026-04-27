# Security Role Tools

Security role tools manage Dataverse security roles and their privileges using the **Dataverse Web API** (`/api/data/v9.2/roles`).

## Tools

### `role_list`

Lists Security Roles in the environment.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filter` | string | No | OData filter (e.g. `"name eq 'System Administrator'"`) |

**Example prompt:** "List all security roles in the environment"

---

### `role_get`

Gets a Security Role including its privilege list.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `roleId` | GUID string | Yes | The role's unique identifier |

**Example prompt:** "Show me all privileges assigned to role abc123"

---

### `role_create`

Creates a new Security Role.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Role name |
| `businessUnitId` | GUID string | Yes | Business unit to create the role in |
| `description` | string | No | Optional description |

**Example prompt:** "Create a security role named 'Sales Read-Only' in business unit abc123"

---

### `role_update`

Adds or removes privileges on a Security Role.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `roleId` | GUID string | Yes | The role to modify |
| `privilegesToAddJson` | JSON array | No | Privileges to add: `[{"privilegeId": "...", "privilegeName": "...", "depth": 4}]` |
| `privilegeIdsToRemoveJson` | JSON array | No | Privilege GUIDs to remove: `["guid1", "guid2"]` |

Depth values: 0=None, 1=User, 2=Business Unit, 3=Parent-Child Business Unit, 4=Organization

**Example prompt:** "Add the prvReadAccount privilege at organization depth to role abc123"
