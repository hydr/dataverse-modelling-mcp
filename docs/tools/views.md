# View Tools

View tools manage Dataverse saved queries (views) using the **Dataverse Web API** (`/api/data/v9.2/savedqueries`).

## Tools

### `view_list`

Lists saved views for a table.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `tableLogicalName` | string | Yes | Logical name of the table |
| `viewType` | string | No | Filter by type: `public`, `advancedfind`, `associated`, `quickfind` |

**Example prompt:** "List all public views on the account table"

---

### `view_get`

Gets the full definition of a view, including FetchXml and LayoutXml.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `viewId` | GUID string | Yes | The view's unique identifier |

**Example prompt:** "Show me the FetchXml of view abc123"

---

### `view_update`

Updates properties of a view.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `viewId` | GUID string | Yes | The view to update |
| `propertiesJson` | JSON | Yes | Properties to update (e.g. `{"name": "New Name"}`) |

**Example prompt:** "Rename view abc123 to 'All Active Accounts'"

---

### `view_add_column`

Adds a column to a view's layout grid.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `viewId` | GUID string | Yes | The view to modify |
| `attributeLogicalName` | string | Yes | Logical name of the column to add |
| `width` | int | No | Column width in pixels (default: 100) |

**Example prompt:** "Add the 'telephone1' column to view abc123"

---

### `view_set_sort`

Sets the sort order on a view's FetchXml.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `viewId` | GUID string | Yes | The view to modify |
| `attributeLogicalName` | string | Yes | Column to sort by |
| `descending` | bool | No | `true` for descending, `false` for ascending (default) |

**Example prompt:** "Sort view abc123 by createdon descending"
