namespace Dataverse.Core.Services;

using System.Text.Json;
using System.Xml.Linq;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// What to put in a cell.
/// </summary>
/// <param name="CustomControlName">
/// The <c>customcontrol.name</c> as stored, <b>including</b> the publisher prefix — the manifest's
/// <c>Contoso.DocumentViewer</c> is <c>sample_Contoso.DocumentViewer</c>
/// here. Null for a plain field control.
/// </param>
/// <param name="DataFieldName">
/// The column the control is bound to, or null for an unbound control. A bound column must already
/// be published, or the form write fails with <c>0x80160051</c>.
/// </param>
/// <param name="ClassId">
/// Only for a plain field control; ignored when <paramref name="CustomControlName"/> is set, because
/// a code component always uses the generic custom-control classid.
/// </param>
public sealed record FormControlSpec(
    string? DataFieldName = null,
    string? CustomControlName = null,
    string? Label = null,
    string? ClassId = null,
    int? RowSpan = null,
    int? ColSpan = null,
    IReadOnlyDictionary<string, FormControlParameter>? Parameters = null)
{
    /// <summary>
    /// Parse a spec from JSON. A parameter may be a bare string — a fixed configuration value, which
    /// is the common case — or an object with <c>value</c>, <c>static</c> and <c>type</c>.
    /// </summary>
    public static FormControlSpec Parse(string controlJson)
    {
        using var doc = JsonDocument.Parse(controlJson);
        var root = doc.RootElement;

        Dictionary<string, FormControlParameter>? parameters = null;
        if (root.TryGetProperty("parameters", out var parametersElement)
            && parametersElement.ValueKind == JsonValueKind.Object)
        {
            parameters = [];
            foreach (var property in parametersElement.EnumerateObject())
            {
                parameters[property.Name] = property.Value.ValueKind == JsonValueKind.Object
                    ? new FormControlParameter(
                        Value: property.Value.TryGetProperty("value", out var v) ? v.ToString() : string.Empty,
                        Static: !property.Value.TryGetProperty("static", out var st)
                                || st.ValueKind != JsonValueKind.False,
                        Type: property.Value.TryGetProperty("type", out var ty) ? ty.GetString() : null)
                    : new FormControlParameter(property.Value.ToString());
            }
        }

        return new FormControlSpec(
            DataFieldName: Text(root, "dataFieldName"),
            CustomControlName: Text(root, "customControlName"),
            Label: Text(root, "label"),
            ClassId: Text(root, "classId"),
            RowSpan: Number(root, "rowSpan"),
            ColSpan: Number(root, "colSpan"),
            Parameters: parameters);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;
}

/// <param name="Static">
/// True for a configuration value (rendered as <c>static="true"</c>), false for a property bound to
/// a column. Getting this wrong is one of the ways a form silently renders an empty control.
/// </param>
public sealed record FormControlParameter(string Value, bool Static = true, string? Type = null);

public sealed partial class FormService
{
    /// <summary>Add a control to a section of a form.</summary>
    /// <param name="tab">Tab id, name or label.</param>
    /// <param name="section">Section id, name or label.</param>
    public async Task<FormEditResult> AddControlAsync(
        string orgUrl,
        Guid formId,
        string tab,
        string section,
        FormControlSpec spec,
        bool publish = true,
        CancellationToken ct = default)
    {
        var form = await LoadForEditAsync(orgUrl, formId, ct);
        var doc = XDocument.Parse(form.FormXml);

        var tabElement = FindTab(doc, tab)
                         ?? throw new InvalidOperationException(
                             $"No tab matching '{tab}' on form '{form.Name}'. Available: "
                             + DescribeTabs(doc));

        var sectionElement = FindSection(tabElement, section)
                             ?? throw new InvalidOperationException(
                                 $"No section matching '{section}' in tab '{tab}'. Available: "
                                 + DescribeSections(tabElement));

        var rows = sectionElement.Elements().FirstOrDefault(e => e.Name.LocalName == "rows");
        if (rows is null)
        {
            rows = new XElement("rows");
            sectionElement.Add(rows);
        }

        var (cell, uniqueId) = BuildCell(spec, LanguageCodeOf(doc));
        rows.Add(new XElement("row", cell));

        if (spec.CustomControlName is not null && uniqueId is not null)
            AddControlDescription(doc, uniqueId, spec);

        return await SaveAsync(
            orgUrl, form, doc, publish,
            $"Added control {Describe(spec)} to section '{section}' of tab '{tab}'", ct);
    }

    /// <summary>
    /// Replace a control in place, keeping its cell — the way an existing preview or a foreign
    /// control gets swapped for a code component.
    /// </summary>
    /// <param name="controlId">The control's <c>id</c>, or its <c>uniqueid</c>.</param>
    public async Task<FormEditResult> ReplaceControlAsync(
        string orgUrl,
        Guid formId,
        string controlId,
        FormControlSpec spec,
        bool publish = true,
        CancellationToken ct = default)
    {
        var form = await LoadForEditAsync(orgUrl, formId, ct);
        var doc = XDocument.Parse(form.FormXml);

        var control = doc.Descendants()
            .Where(e => e.Name.LocalName == "control")
            .FirstOrDefault(e =>
                string.Equals((string?)e.Attribute("id"), controlId, StringComparison.OrdinalIgnoreCase)
                || string.Equals((string?)e.Attribute("uniqueid"), controlId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No control with id or uniqueid '{controlId}' on form '{form.Name}'.");

        // The old description would otherwise stay behind and keep rendering the old component.
        var oldUniqueId = (string?)control.Attribute("uniqueid");
        if (oldUniqueId is not null)
            RemoveControlDescription(doc, oldUniqueId);

        var (newCell, uniqueId) = BuildCell(spec, LanguageCodeOf(doc));
        var newControl = newCell.Elements().First(e => e.Name.LocalName == "control");
        control.ReplaceWith(newControl);

        if (spec.CustomControlName is not null && uniqueId is not null)
            AddControlDescription(doc, uniqueId, spec);

        return await SaveAsync(
            orgUrl, form, doc, publish,
            $"Replaced control '{controlId}' with {Describe(spec)}", ct);
    }

    /// <summary>Remove a whole tab, and any control description its controls leave behind.</summary>
    /// <param name="tab">Tab id, name or label.</param>
    public async Task<FormEditResult> RemoveTabAsync(
        string orgUrl,
        Guid formId,
        string tab,
        bool publish = true,
        CancellationToken ct = default)
    {
        var form = await LoadForEditAsync(orgUrl, formId, ct);
        var doc = XDocument.Parse(form.FormXml);

        var tabElement = FindTab(doc, tab)
                         ?? throw new InvalidOperationException(
                             $"No tab matching '{tab}' on form '{form.Name}'. Available: "
                             + DescribeTabs(doc));

        // A form must keep at least one tab. Writing an empty <tabs> gets rejected by schema
        // validation with 0x80048425 "The element 'tabs' has incomplete content", which says
        // nothing about the actual problem.
        var tabCount = doc.Descendants().Count(e => e.Name.LocalName == "tab");
        if (tabCount <= 1)
        {
            throw new InvalidOperationException(
                $"'{tab}' is the only tab on form '{form.Name}', and a form cannot have none — the "
                + "platform rejects the FormXML with 0x80048425. Add another tab first, or delete "
                + "the form instead of emptying it.");
        }

        var label = FirstLabel(tabElement) ?? (string?)tabElement.Attribute("name") ?? tab;
        tabElement.Remove();
        RemoveOrphanedControlDescriptions(doc);

        return await SaveAsync(orgUrl, form, doc, publish, $"Removed tab '{label}'", ct);
    }

    // ---------------------------------------------------------------------------------------------
    // FormXML assembly
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Build the cell and its control. A code component gets the generic classid plus a
    /// <c>uniqueid</c>; the component itself is named in a separate control description.
    /// </summary>
    public static (XElement Cell, string? UniqueId) BuildCell(FormControlSpec spec, int languageCode)
    {
        var isCustomControl = !string.IsNullOrWhiteSpace(spec.CustomControlName);
        var uniqueId = isCustomControl ? $"{{{Guid.NewGuid()}}}" : null;

        var controlId = spec.DataFieldName
                        ?? (isCustomControl
                            ? "cc_" + Guid.NewGuid().ToString("N")[..8]
                            : throw new ArgumentException(
                                "A control needs either a dataFieldName or a customControlName.",
                                nameof(spec)));

        var control = new XElement("control",
            new XAttribute("id", controlId),
            new XAttribute("classid", isCustomControl
                ? CustomControlClassId
                : spec.ClassId ?? throw new ArgumentException(
                    "A plain field control needs a classId.", nameof(spec))));

        if (!string.IsNullOrWhiteSpace(spec.DataFieldName))
            control.Add(new XAttribute("datafieldname", spec.DataFieldName));
        else
            control.Add(new XAttribute("isunbound", "true"));

        if (uniqueId is not null)
            control.Add(new XAttribute("uniqueid", uniqueId));

        var cell = new XElement("cell",
            new XAttribute("id", $"{{{Guid.NewGuid()}}}"),
            new XAttribute("showlabel", spec.Label is null ? "false" : "true"));

        if (spec.RowSpan is { } rowSpan)
            cell.Add(new XAttribute("rowspan", rowSpan));
        if (spec.ColSpan is { } colSpan)
            cell.Add(new XAttribute("colspan", colSpan));

        cell.Add(new XElement("labels",
            new XElement("label",
                new XAttribute("description", spec.Label ?? string.Empty),
                new XAttribute("languagecode", languageCode))));

        cell.Add(control);
        return (cell, uniqueId);
    }

    /// <summary>
    /// Append the control description — one <c>customControl</c> per form factor, because a missing
    /// one makes the designer reject the whole form.
    /// </summary>
    public static void AddControlDescription(XDocument doc, string uniqueId, FormControlSpec spec)
    {
        var form = doc.Root ?? throw new InvalidOperationException("FormXML has no root element.");
        var descriptions = form.Elements().FirstOrDefault(e => e.Name.LocalName == "controlDescriptions");
        if (descriptions is null)
        {
            descriptions = new XElement("controlDescriptions");
            form.Add(descriptions);
        }

        var description = new XElement("controlDescription", new XAttribute("forControl", uniqueId));

        foreach (var formFactor in FormFactors)
        {
            var customControl = new XElement("customControl",
                new XAttribute("name", spec.CustomControlName!),
                new XAttribute("formFactor", formFactor));

            if (spec.Parameters is { Count: > 0 })
            {
                var parameters = new XElement("parameters");
                foreach (var (name, parameter) in spec.Parameters)
                {
                    var element = new XElement(name, parameter.Value);
                    if (parameter.Static)
                        element.Add(new XAttribute("static", "true"));
                    if (!string.IsNullOrWhiteSpace(parameter.Type))
                        element.Add(new XAttribute("type", parameter.Type));
                    parameters.Add(element);
                }
                customControl.Add(parameters);
            }

            description.Add(customControl);
        }

        descriptions.Add(description);
    }

    private static void RemoveControlDescription(XDocument doc, string uniqueId) =>
        doc.Descendants()
            .Where(e => e.Name.LocalName == "controlDescription"
                        && string.Equals((string?)e.Attribute("forControl"), uniqueId, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .ForEach(e => e.Remove());

    /// <summary>
    /// Drop descriptions whose control is gone. A dangling description is not a cosmetic problem —
    /// it is a reference to a control the form no longer has.
    /// </summary>
    public static void RemoveOrphanedControlDescriptions(XDocument doc)
    {
        var live = doc.Descendants()
            .Where(e => e.Name.LocalName == "control")
            .Select(e => (string?)e.Attribute("uniqueid"))
            .Where(id => id is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        doc.Descendants()
            .Where(e => e.Name.LocalName == "controlDescription"
                        && !live.Contains((string?)e.Attribute("forControl")))
            .ToList()
            .ForEach(e => e.Remove());
    }

    // ---------------------------------------------------------------------------------------------
    // Lookup helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>Test seam for the tab lookup, which decides whether an edit lands at all.</summary>
    public static XElement? FindTabForTest(XDocument doc, string key) => FindTab(doc, key);

    /// <summary>Match a tab by id, name or label — whichever the caller happened to have.</summary>
    private static XElement? FindTab(XDocument doc, string key) =>
        doc.Descendants().Where(e => e.Name.LocalName == "tab").FirstOrDefault(t => Matches(t, key));

    private static XElement? FindSection(XElement tab, string key) =>
        tab.Descendants().Where(e => e.Name.LocalName == "section").FirstOrDefault(s => Matches(s, key));

    private static bool Matches(XElement element, string key) =>
        string.Equals((string?)element.Attribute("id"), key, StringComparison.OrdinalIgnoreCase)
        || string.Equals((string?)element.Attribute("id"), $"{{{key.Trim('{', '}')}}}", StringComparison.OrdinalIgnoreCase)
        || string.Equals((string?)element.Attribute("name"), key, StringComparison.OrdinalIgnoreCase)
        || string.Equals(FirstLabel(element), key, StringComparison.OrdinalIgnoreCase);

    private static string DescribeTabs(XDocument doc) =>
        string.Join(", ", doc.Descendants().Where(e => e.Name.LocalName == "tab")
            .Select(t => $"'{FirstLabel(t) ?? (string?)t.Attribute("name") ?? (string?)t.Attribute("id")}'"));

    private static string DescribeSections(XElement tab) =>
        string.Join(", ", tab.Descendants().Where(e => e.Name.LocalName == "section")
            .Select(s => $"'{FirstLabel(s) ?? (string?)s.Attribute("name") ?? (string?)s.Attribute("id")}'"));

    /// <summary>
    /// Reuse the language code the form already labels things in, rather than assuming 1033 and
    /// producing a label nobody sees.
    /// </summary>
    private static int LanguageCodeOf(XDocument doc)
    {
        var existing = doc.Descendants()
            .Where(e => e.Name.LocalName == "label")
            .Select(e => (string?)e.Attribute("languagecode"))
            .FirstOrDefault(c => int.TryParse(c, out _));

        return int.TryParse(existing, out var languageCode) ? languageCode : 1033;
    }

    private static string Describe(FormControlSpec spec) =>
        spec.CustomControlName is not null
            ? $"'{spec.CustomControlName}'" + (spec.DataFieldName is null ? " (unbound)" : $" bound to {spec.DataFieldName}")
            : $"field '{spec.DataFieldName}'";

    // ---------------------------------------------------------------------------------------------
    // Persistence
    // ---------------------------------------------------------------------------------------------

    private sealed record FormForEdit(Guid FormId, string Name, string? Table, string FormXml, long? VersionNumber);

    private async Task<FormForEdit> LoadForEditAsync(string orgUrl, Guid formId, CancellationToken ct)
    {
        var raw = await _client.GetRawAsync(
            orgUrl,
            $"api/data/v9.2/systemforms({formId:D})?$select=formid,name,objecttypecode,formxml,versionnumber",
            ct: ct);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var formXml = root.GetStringOrNull("formxml");
        if (string.IsNullOrWhiteSpace(formXml))
            throw new InvalidOperationException($"Form {formId:D} has no formxml.");

        return new FormForEdit(
            FormId: root.TryGetGuid("formid"),
            Name: root.GetStringOrEmpty("name"),
            Table: root.GetStringOrNull("objecttypecode"),
            FormXml: formXml!,
            VersionNumber: root.TryGetProperty("versionnumber", out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt64()
                : null);
    }

    /// <summary>
    /// Write the form back and, unless told otherwise, publish it — then read the version to say
    /// whether the change actually landed.
    /// </summary>
    /// <remarks>
    /// Without the publish the change is invisible even to a read-back: a <c>PATCH</c> writes the
    /// unpublished form, a <c>GET</c> returns the published one. Measured on a live org, the old XML
    /// and the old <c>versionnumber</c> came back unchanged for 120 seconds and then jumped the
    /// moment the table was published. That is why the publish defaults to on here, and why an
    /// unpublished write says so instead of looking finished.
    /// </remarks>
    private async Task<FormEditResult> SaveAsync(
        string orgUrl,
        FormForEdit form,
        XDocument doc,
        bool publish,
        string change,
        CancellationToken ct)
    {
        var formXml = doc.ToString(SaveOptions.DisableFormatting);
        await _client.PatchAsync(
            orgUrl, $"api/data/v9.2/systemforms({form.FormId:D})", new { formxml = formXml }, ct);

        if (!publish)
        {
            return new FormEditResult(
                Success: true,
                FormId: form.FormId,
                Name: form.Name,
                TableLogicalName: form.Table,
                Change: change,
                Published: false,
                VersionNumberBefore: form.VersionNumber,
                VersionNumberAfter: null,
                Note: "Written but NOT published, so the change is invisible — including to a "
                      + "read-back, which returns the published form and will keep showing the old "
                      + $"XML and the old versionnumber. Publish with publish_customizations(entities='{form.Table}').");
        }

        if (string.IsNullOrWhiteSpace(form.Table))
        {
            return new FormEditResult(
                Success: true, FormId: form.FormId, Name: form.Name, TableLogicalName: form.Table,
                Change: change, Published: false,
                VersionNumberBefore: form.VersionNumber, VersionNumberAfter: null,
                Note: "Written, but the form has no objecttypecode (a dashboard, for example), so "
                      + "there is no table to publish. Publish the relevant component yourself.");
        }

        await _publish.PublishAsync(orgUrl, entities: [form.Table!], ct: ct);

        long? after = null;
        try
        {
            var raw = await _client.GetRawAsync(
                orgUrl, $"api/data/v9.2/systemforms({form.FormId:D})?$select=versionnumber", ct: ct);
            using var verify = JsonDocument.Parse(raw);
            if (verify.RootElement.TryGetProperty("versionnumber", out var v) && v.ValueKind == JsonValueKind.Number)
                after = v.GetInt64();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the version of form {FormId} after publishing.", form.FormId);
        }

        var landed = after is not null && after != form.VersionNumber;

        return new FormEditResult(
            Success: true,
            FormId: form.FormId,
            Name: form.Name,
            TableLogicalName: form.Table,
            Change: change,
            Published: true,
            VersionNumberBefore: form.VersionNumber,
            VersionNumberAfter: after,
            Note: landed
                ? null
                : "Published, but versionnumber did not change. Either the edit produced identical "
                  + "XML, or it did not take — compare the form with form_get before relying on it.");
    }
}
