# Web Resource Tools

Web resource tools manage `webresource` rows using the **Dataverse Web API**.

> **Entity set gotcha:** the entity set name is `webresourceset`, not `webresources`.
> `/api/data/v9.2/webresources` fails with `0x80060888 — "Resource not found for the segment 'webresources'"`.

## Web resource types

| Value | Type | Extension | Text? |
|-------|------|-----------|-------|
| 1 | HTML | `.html`, `.htm` | yes |
| 2 | CSS | `.css` | yes |
| 3 | JScript | `.js` | yes |
| 4 | XML | `.xml` | yes |
| 5 | PNG | `.png` | no |
| 6 | JPG | `.jpg`, `.jpeg` | no |
| 7 | GIF | `.gif` | no |
| 8 | XAP (Silverlight) | `.xap` | no |
| 9 | XSL | `.xsl`, `.xslt` | yes |
| 10 | ICO | `.ico` | no |
| 11 | SVG | `.svg` | yes |
| 12 | RESX | `.resx` | yes |

## Tools

### `webresource_list`

Lists web resources, optionally filtered by name prefix.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `tableOrPrefix` | string | No | Name prefix, e.g. `sample_` or `sample_purchaseorder` |

**Example prompt:** "List all web resources starting with sample_purchaseorder"

---

### `webresource_get`

Gets a web resource by name or GUID. Text formats are returned decoded; binary formats report only
`contentBytes`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `nameOrId` | string | Yes | Name (`sample_script.js`) or GUID |

> **Publish gotcha:** `content` read through the Web API is the **published** payload. Immediately
> after an unpublished write, this still returns the previous version.

**Example prompt:** "Show me the content of sample_purchaseorder_correct_price.js"

---

### `webresource_upsert`

Creates or updates a web resource, Base64-encoding the payload.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Unique name, e.g. `sample_purchaseorder_correct_price.js` |
| `filePath` | string | No* | Local file whose bytes become the content |
| `content` | string | No* | Inline text content |
| `displayName` | string | No | Display name (defaults to `name` on create) |
| `description` | string | No | Description |
| `webResourceType` | int | No | Inferred from the extension on create; immutable on update |
| `solutionUniqueName` | string | No | Adds the web resource as component type **61** |
| `publish` | bool | No | Publish afterwards (default `true`) |

\* exactly one of `filePath` / `content` is required.

**Example prompt:** "Upload ./scripts/correct_price.js as sample_purchaseorder_correct_price.js into the
ContosoSales solution and publish it"

---

## `webresource_usages`

Finds where a web resource is referenced — ask this before deleting one.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Web resource name (e.g. `xv_purchaseinvoice_js`) or its GUID |
| `tables` | string | No | Comma-separated table logical names whose **merged** ribbon should also be searched |

**Example prompt:** "Is xv_msalbrowsermin still used anywhere?"

No single place records where a web resource is used, so the answer is assembled from several:

| Source | How |
|---|---|
| Platform dependency tracking | `RetrieveDependenciesForDelete` — authoritative where it applies |
| Forms | `contains(formxml, …)`, reported with the table each form belongs to |
| Ribbon diffs | `contains(rdx, …)` on `ribbondiffs` |
| Site map | `contains(sitemapxml, …)` |
| Modern commands | the `appaction` lookups — exact, not a text match |
| Merged ribbons | `RetrieveEntityRibbon` per table, only for the tables in `tables` |

The text searches are not redundant with the dependency tracking. A ribbon that names a library in a
`<JavaScriptFunction>` creates no dependency row, which is exactly how a web resource ends up
looking unused while a button still calls into it.

**Read `notSearched` before concluding anything can be deleted.** It always names what was skipped —
the content of other web resources (stored base64-encoded, so no server-side text search is
possible), canvas apps, plug-in code — and, when no `tables` were given, the merged ribbons.
