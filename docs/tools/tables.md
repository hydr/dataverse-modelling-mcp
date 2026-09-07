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

Managed properties may be written as plain values — see
[Managed properties](#managed-properties) below. The result reports what was rewritten under
`normalizedManagedProperties`.

---

### `column_update`

Updates properties of an existing column.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `tableLogicalName` | string | Yes | The table |
| `columnLogicalName` | string | Yes | The column logical name |
| `propertiesJson` | JSON | Yes | Properties to update |

**Example prompt:** "Change the max length of the new_projectcode column on account to 50"

---

## Managed properties

A managed property is not a boolean on the wire — it is a `BooleanManagedProperty` object. Writing

```json
{ "IsValidForAdvancedFind": false }
```

used to reach Dataverse unchanged and come back as a raw OData deserializer complaint that never
mentions managed properties:

```
0x80048d19 … A 'PrimitiveValue' node with non-null value was found when trying to read the value of
the property 'IsValidForAdvancedFind'; however, a 'StartArray' node, a 'StartObject' node, or a
'PrimitiveValue' node with null value was expected.
```

`column_add`, `column_update` and `table_update` now rewrite a plain value into the object shape and
list what they changed under `normalizedManagedProperties`. A value already given as a full object is
left alone, including a `CanBeChanged` of your own.

**The lists differ per scope, and that matters.** `AttributeMetadata.IsValidForAdvancedFind` is a
`BooleanManagedProperty`, while `EntityMetadata.IsValidForAdvancedFind` is a plain `Edm.Boolean` — a
column and a table behave differently for the same property name.

Columns (`column_add`, `column_update`), with the `ManagedPropertyLogicalName` Dataverse uses. Note
that the logical name follows "who may change this", not the property name:

| Property | `ManagedPropertyLogicalName` |
|---|---|
| `IsValidForAdvancedFind` | `canmodifysearchsettings` |
| `IsAuditEnabled` | `canmodifyauditsettings` |
| `IsCustomizable` | `iscustomizable` |
| `IsRenameable` | `isrenameable` |
| `IsGlobalFilterEnabled` | `canmodifyglobalfiltersettings` |
| `IsSortableEnabled` | `canmodifyissortablesettings` |
| `CanModifyAdditionalSettings` | `canmodifyadditionalsettings` |
| `RequiredLevel` (a string, not a boolean) | `canmodifyrequirementlevelsettings` |

Tables (`table_update`): `IsAuditEnabled`, `IsCustomizable`, `IsRenameable`, `IsMappable`,
`IsValidForQueue`, `IsConnectionsEnabled`, `IsDuplicateDetectionEnabled`, `IsMailMergeEnabled`,
`CanCreateForms`, `CanCreateViews`, `CanCreateCharts`, `CanCreateAttributes`,
`CanModifyAdditionalSettings`, `CanBeRelatedEntityInRelationship`,
`CanBePrimaryEntityInRelationship`, `CanBeInManyToMany`, `CanBeInCustomEntityAssociation`,
`CanChangeHierarchicalRelationship`, `CanChangeTrackingBeEnabled`,
`CanEnableSyncToExternalSearchIndex`, `IsVisibleInMobile`, `IsVisibleInMobileClient`,
`IsReadOnlyInMobileClient`, `IsOfflineInMobileClient`. These get `{ "Value": … }` without a
`ManagedPropertyLogicalName`: no documented write payload covers them, and a guessed logical name is
worse than none — the shape alone is what the deserializer needs.

Columns that look like managed properties but are plain booleans, and are therefore passed through
untouched: `IsValidForGrid`, `IsValidForForm`, `IsSecured`, `CanBeSecuredForRead`,
`CanBeSecuredForCreate`, `CanBeSecuredForUpdate`.
