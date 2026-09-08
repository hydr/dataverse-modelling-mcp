# Form Tools

Model-driven forms (`systemform`). FormXML is not workable by hand at any useful size, so the read
side returns a structure and the write side assembles the XML for you.

## `form_list`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `table` | string | No | Table logical name |
| `type` | int | No | Form type code |

Type codes: `2` Main, `6` QuickViewForm, `7` QuickCreate, `8` Dialog, `0` Dashboard, `11` Card.

**Example prompt:** "List the main forms of the salesorder table"

> `systemform` has **no `modifiedon`** and no `modifiedby` — selecting either fails with
> `0x80060888 Could not find a property named 'modifiedon'`. Compare states with `versionnumber`
> (there is also `version`, `overwritetime` and `publishedon`).

---

## `form_get`

Returns the form as a tree — tabs → columns → sections → cells → controls — instead of raw XML.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `formId` | GUID string | Yes | The form |
| `includeFormXml` | bool | No | Also return the raw XML (default `false`) |

**Example prompt:** "Show me the structure of the salesorder main form"

Each control comes back with its `id`, `classid` and a resolved `className`, the column it is bound
to, and whether it is a code component. **The join is the useful part:** a cell hosting a code
component carries only a generic classid and a `uniqueid`, and which component actually renders is
decided by a separate `controlDescription` elsewhere in the document. `form_get` resolves that, so
the component's name appears at the cell where it lives.

---

## Writing to a form

### `form_add_control`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `formId` | GUID string | Yes | The form |
| `tab` | string | Yes | Tab id, name or label |
| `section` | string | Yes | Section id, name or label |
| `controlJson` | JSON | Yes | The control — see below |
| `publish` | bool | No | Publish the table afterwards (**default `true`**) |

```json
{
  "customControlName": "sample_Contoso.DocumentViewer",
  "dataFieldName": "sample_documentviewer",
  "label": "Dokumente",
  "rowSpan": 8,
  "colSpan": 2,
  "parameters": {
    "authMode": { "value": "auto", "static": true, "type": "Enum" },
    "boundField": { "value": "sample_documentviewer", "static": false }
  }
}
```

A parameter given as a bare string is treated as a static value, which is the common case:
`"parameters": { "authMode": "auto" }`.

For a plain field control pass `dataFieldName` and `classId` instead of `customControlName`.

### `form_replace_control`

Replaces a control in place, keeping its cell — how an old preview or a foreign control gets swapped
for a code component. Identify the old control by its `id` **or** its `uniqueid`, both of which come
from `form_get`. Any `controlDescription` belonging to the old control is removed, so it cannot keep
rendering.

### `form_remove_tab`

Removes a whole tab and cleans up the control descriptions its controls leave behind — a dangling
description points at a control the form no longer has.

A form cannot have zero tabs. Removing the last one is refused up front, because the platform's own
answer is `0x80048425 The element 'tabs' has incomplete content`, which says nothing about the real
problem.

---

## The details these tools encapsulate

Each of these produces an error a long way from its cause. The first three were verified against
forms the Maker portal generated; the fourth is as reported from a live session.

**1. A code component's name in FormXML carries the publisher prefix.** The manifest says
`Contoso.DocumentViewer`; `customcontrol.name` — and therefore FormXML — says
`sample_Contoso.DocumentViewer`. The manifest name alone gives:

```
0x80160007 Custom control with name Contoso.DocumentViewer does not exist.
```

**2. The control description must declare all three form factors** (0 web, 1 tablet, 2 phone), or
the designer reports `Custom control declaration for form factor(s) 0,1,2 is missing`.

**3. The cell holds a generic control, not the component.** It needs the custom-control classid
`{F9A8A302-114E-466A-B582-6771B2AE0D92}` plus a `uniqueid` that the `controlDescription` points at
with `forControl`. An unbound control additionally carries `isunbound="true"`.

**4. A bound column must already be published** before the form write will accept it:

```
0x80160051 Property boundField is bound to a non-existent attribute
sample_documentviewer in current entity SalesOrder
```

`column_add` takes a `publish` flag for exactly this sequence.

**5. A component whose manifest declares a required bound property cannot go on a form unbound.**
Read the manifest (`customcontrol.manifest`) and look for a property with `usage="bound"` and
`required="true"`; pass `dataFieldName` plus the matching parameter. Otherwise:

```
0x80160032 Property boundField is required, but the declaration is missing.
```

The platform validates before writing, so a form that fails any of these is left untouched.

---

## Why writes publish by default

Unlike table metadata, a form write is invisible without a publish — **including to a read-back**. A
`PATCH` writes the *unpublished* form while a `GET` returns the *published* one, so they are simply
different states until you publish. Measured on a live environment: the old `formxml` and the old
`versionnumber` still came back 120 seconds after a successful `PATCH`, then went current the moment
the table was published.

That is why `publish` defaults to `true` here, why an unpublished write says so in its result, and
why the result reports `versionNumberBefore` / `versionNumberAfter`: a changed version is the
evidence that the edit landed.

Identify a tab by its **id or name** rather than its label where you can. A label read straight
after another write can still be the old one; the error message lists the tabs that were found.
