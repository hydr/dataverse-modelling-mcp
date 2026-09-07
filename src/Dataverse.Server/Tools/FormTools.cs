namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Services;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class FormTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "form_list")]
    [Description("List model-driven forms (systemform), optionally filtered to one table and/or " +
                 "form type (2=Main, 6=QuickViewForm, 7=QuickCreate, 8=Dialog, 0=Dashboard, " +
                 "11=Card). Returns formid, name, table, type and label, formactivationstate " +
                 "(0=Inactive, 1=Active), ismanaged and versionnumber. NOTE: systemform has no " +
                 "modifiedon and no modifiedby — selecting either fails with 0x80060888 — so " +
                 "versionnumber is what you compare states with.")]
    public static async Task<string> FormList(
        FormService svc,
        ConfigProvider config,
        [Description("Optional table logical name (e.g. 'salesorder')")] string? table = null,
        [Description("Optional form type code, e.g. 2 for a Main form")] int? type = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ListAsync(env.OrgUrl, table, type, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "form_get")]
    [Description("Get a form as a STRUCTURE rather than as raw FormXML: tabs → columns → sections " +
                 "→ cells → controls, each control with its id, classid and resolved class name, " +
                 "the column it is bound to, and — for a code component — the component name from " +
                 "the control descriptions. A cell hosting a code component only carries a generic " +
                 "classid plus a uniqueid; which component renders it is decided by a separate " +
                 "controlDescription, and this tool joins the two for you. Pass " +
                 "includeFormXml=true if you really need the XML too.")]
    public static async Task<string> FormGet(
        FormService svc,
        ConfigProvider config,
        [Description("GUID of the form (formid)")] string formId,
        [Description("true to include the raw FormXML alongside the structure (default false)")] bool includeFormXml = false,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(formId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid formId GUID format." });

            var env = config.GetActiveEnvironment();
            var result = await svc.GetAsync(env.OrgUrl, id, includeFormXml, ct);
            return result is null
                ? JsonSerializer.Serialize(new { error = "Form not found." })
                : JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "form_add_control")]
    [Description("Add a control to a section of a form. For a code component (PCF) pass " +
                 "customControlName — the STORED customcontrol.name including the publisher prefix " +
                 "(e.g. 'xv_Crossvertise.SharePointDocumentViewer'); the manifest name without the " +
                 "prefix fails with 0x80160007. The tool writes the generic custom-control classid, " +
                 "a uniqueid, and a controlDescription declaring all three form factors (a missing " +
                 "one makes the designer reject the form). For a plain field control pass " +
                 "dataFieldName and classId instead. A bound column must already be PUBLISHED, or " +
                 "the write fails with 0x80160051. Publishes by default: without it the change is " +
                 "invisible even to a read-back.")]
    public static async Task<string> FormAddControl(
        FormService svc,
        ConfigProvider config,
        [Description("GUID of the form")] string formId,
        [Description("Tab id, name or label")] string tab,
        [Description("Section id, name or label")] string section,
        [Description("Control spec as JSON: {\"customControlName\":\"xv_Ns.Ctrl\", \"dataFieldName\":\"xv_col\", \"label\":\"Documents\", \"classId\":\"{...}\", \"rowSpan\":8, \"colSpan\":2, \"parameters\":{\"authMode\":{\"value\":\"auto\",\"static\":true,\"type\":\"Enum\"}}}. A parameter with static=false is bound to a column instead of being a fixed value.")] string controlJson,
        [Description("true (default) to publish the table afterwards")] bool publish = true,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(formId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid formId GUID format." });

            var spec = FormControlSpec.Parse(controlJson);
            var env = config.GetActiveEnvironment();
            var result = await svc.AddControlAsync(env.OrgUrl, id, tab, section, spec, publish, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "form_replace_control")]
    [Description("Replace a control in place, keeping its cell — the way an old preview or a " +
                 "foreign control gets swapped for a code component. Identify the old control by " +
                 "its id or its uniqueid (both come from form_get). Any controlDescription " +
                 "belonging to the old control is removed, so it cannot keep rendering. The new " +
                 "control spec is the same shape as for form_add_control. Publishes by default.")]
    public static async Task<string> FormReplaceControl(
        FormService svc,
        ConfigProvider config,
        [Description("GUID of the form")] string formId,
        [Description("id or uniqueid of the control to replace")] string controlId,
        [Description("Control spec as JSON — see form_add_control")] string controlJson,
        [Description("true (default) to publish the table afterwards")] bool publish = true,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(formId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid formId GUID format." });

            var spec = FormControlSpec.Parse(controlJson);
            var env = config.GetActiveEnvironment();
            var result = await svc.ReplaceControlAsync(env.OrgUrl, id, controlId, spec, publish, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "form_remove_tab")]
    [Description("Remove a whole tab from a form, identified by its id, name or label. Control " +
                 "descriptions left behind by the removed controls are cleaned up, since a dangling " +
                 "description points at a control the form no longer has. A form cannot have zero " +
                 "tabs, so removing the last one is refused up front. Prefer the tab's id or name " +
                 "over its label: a label read straight after another write can still be the old " +
                 "one. The error message lists the tabs it did find. Publishes by default.")]
    public static async Task<string> FormRemoveTab(
        FormService svc,
        ConfigProvider config,
        [Description("GUID of the form")] string formId,
        [Description("Tab id, name or label")] string tab,
        [Description("true (default) to publish the table afterwards")] bool publish = true,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(formId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid formId GUID format." });

            var env = config.GetActiveEnvironment();
            var result = await svc.RemoveTabAsync(env.OrgUrl, id, tab, publish, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
