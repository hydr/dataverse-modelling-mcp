# Publish Tool

Nearly every customization written through the Web API — forms, views, ribbons, modern commands, web
resources, the site map — exists in the database but is invisible to clients until it is **published**.

## `publish_customizations`

Runs `PublishXml` with a `ParameterXml` assembled from the arguments, or `PublishAllXml` when
`all = true`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `entities` | string | No | Comma-separated table logical names |
| `webResources` | string | No | Comma-separated web resource names or GUIDs |
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

Prefer a targeted publish: `PublishAllXml` can run for minutes on a large org.

**Example prompt:** "Publish sample_purchaseorder and sample_purchaseorder_correct_price.js"
