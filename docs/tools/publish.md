# Publish Tool

Nearly every customization written through the Web API — forms, views, ribbons, modern commands, web
resources, the site map — exists in the database but is invisible to clients until it is **published**.

## `publish_customizations`

Runs `PublishXml` with a `ParameterXml` assembled from the arguments, or `PublishAllXml` when
`all = true`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `entities` | string | No | Comma-separated table logical names. Publishing a table also publishes its forms, views and ribbons. |
| `webResources` | string | No | Comma-separated web resource names or GUIDs |
| `dashboards` | string | No | Comma-separated dashboard GUIDs (`systemform` ids). **Ids only** — a name publishes nothing, so one is rejected up front. |
| `optionSets` | string | No | Comma-separated unique names of global choices |
| `siteMap` | bool | No | Publish the site map (a singleton — no value needed) |
| `ribbons` | bool | No | Publish the application ribbon (a singleton) |
| `all` | bool | No | Run `PublishAllXml` instead (slow) |

Web resource **names** are resolved to ids first, because `PublishXml` only accepts ids inside
`<webresources>`.

The generated document looks like this:

```xml
<importexportxml>
  <entities>
    <entity>sample_purchaseorder</entity>
  </entities>
  <webresources>
    <webresource>123355b4-8d89-f111-8077-002248997467</webresource>
  </webresources>
</importexportxml>
```

Those six elements — `entities`, `dashboards`, `optionsets`, `webresources`, `sitemaps`, `ribbons` —
are the **whole** documented vocabulary. Anything else is ignored rather than rejected, so an
invented element would look like it worked.

**There is no element for code components.** `PublishXml` cannot publish a PCF control; those travel
by solution import or `pac pcf push`. See
[solution_import](solutions.md#code-components-pcf-are-checked-afterwards).

Prefer a targeted publish: `PublishAllXml` can run for minutes on a large org.

**Example prompt:** "Publish sample_purchaseorder and sample_purchaseorder_correct_price.js"

---

## When a publish is required, and when it only looks like it

Two different things get conflated here, and they behave differently. Measured on a live
environment:

| Write | Read-back without a publish |
|---|---|
| `systemform.formxml` (`PATCH`, HTTP 204) | **Never becomes current.** Still the old `formxml` and old `versionnumber` after 120 seconds. |
| the same form after `publish_customizations` | current immediately; `versionnumber` jumped 55888235 → 64095408 |
| table/column metadata — create | visible after ~3 s |
| table/column metadata — update (`PUT`) | current after ~3 s |
| table/column metadata — delete | gone after ~16 s |

**A form is not a caching problem.** A `PATCH` writes the *unpublished* form while a `GET` returns
the *published* one, so the two simply are different rows until you publish. A read-back that still
shows the old XML is not evidence that the write was rejected — and no amount of waiting will change
it.

**Metadata is eventual consistency.** `EntityDefinitions` catches up on its own within seconds, with
no publish involved. Clients still need the publish to pick the change up, but a stale read straight
after the write means nothing.

The practical rule for both: **a read-back immediately after a write proves nothing.** For a form,
publish and then read. For metadata, wait a moment and read again.

`table_create`, `table_update`, `column_add`, `column_update` and `column_delete` all take a
`publish` flag for the one-step case, and say in their result when they did not publish.
