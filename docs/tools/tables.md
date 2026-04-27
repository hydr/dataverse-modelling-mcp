# Table Tools

Table tools manage Dataverse entity (table) definitions using the **Dataverse Metadata API** (`/api/data/v9.2/EntityDefinitions`).

## Tools

### `table_list`

Lists tables in the environment.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filter` | string | No | OData filter (e.g. `"IsCustomEntity eq true"`) |
| `solution` | string | No | Filter to tables in a specific solution (unique name) |

**Example prompt:** "List all custom tables in the environment"

---

### `table_get`

Gets the full metadata for a table, including all columns.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `logicalName` | string | Yes | Table logical name (e.g. `account`) |

**Example prompt:** "Show me all columns on the contact table"

---

### `table_create`

Creates a new custom Dataverse table.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `logicalName` | string | Yes | Schema name (e.g. `new_myentity`) |
| `displayName` | string | Yes | Singular display name |
| `pluralDisplayName` | string | Yes | Plural display name |
| `description` | string | No | Optional description |

**Example prompt:** "Create a new table called 'Project' with plural name 'Projects'"

---

### `table_update`

Updates metadata properties of a table.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `logicalName` | string | Yes | Table logical name |
| `propertiesJson` | JSON | Yes | Properties to update |

**Example prompt:** "Enable auditing on the account table"

---

### `column_add`

Adds a new column to a table.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `tableLogicalName` | string | Yes | The target table |
| `attributeJson` | JSON | Yes | Full attribute definition (must include `@odata.type`) |

**Example prompt:** "Add a text column named 'Project Code' to the account table"

---

### `column_update`

Updates properties of an existing column.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `tableLogicalName` | string | Yes | The table |
| `columnLogicalName` | string | Yes | The column logical name |
| `propertiesJson` | JSON | Yes | Properties to update |

**Example prompt:** "Change the max length of the new_projectcode column on account to 50"
