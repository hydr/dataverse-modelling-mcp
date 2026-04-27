# Environment Variable Tools

Environment variable tools manage Dataverse environment variables (solution-aware configuration values) using the **Dataverse Web API** (`/api/data/v9.2/environmentvariabledefinitions`).

## Tools

### `envvar_list`

Lists all environment variable definitions and their current values.

**Example prompt:** "List all environment variables and their current values"

---

### `envvar_get`

Gets a single environment variable by schema name, including its current value.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `schemaName` | string | Yes | Schema name of the environment variable (e.g. `new_MyVariable`) |

**Example prompt:** "What is the current value of the new_ApiEndpoint environment variable?"

---

### `envvar_set`

Sets the current value of an environment variable. Creates a new value record if none exists, or updates the existing one.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `schemaName` | string | Yes | Schema name of the variable |
| `value` | string | Yes | New value to set |

**Example prompt:** "Set the new_ApiEndpoint environment variable to 'https://api.example.com'"
