namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Models;
using Dataverse.Core.Services;
using ModelContextProtocol.Server;

/// <summary>
/// Tools for modern commands (<c>appaction</c>). The long descriptions are deliberate: the semantics
/// of this table are barely documented and were reverse-engineered against a live org.
/// </summary>
[McpServerToolType]
public sealed class CommandTools
{
    private const string SemanticsNote =
        "Modern commands are the Unified Interface replacement for classic RibbonDiffXml buttons; " +
        "several tables no longer render RibbonDiffXml at all. Key field semantics (verified against " +
        "the appaction option sets and the migrated system commands of a live org): " +
        "location 0=Form, 1=MainGrid, 2=SubGrid, 3=AssociatedGrid, 4=QuickForm, 5=GlobalHeader, 6=Dashboard. " +
        "context 1=Entity together with contextvalue=<table logical name>. " +
        "origin 0=Default, 1=Migrated, 2=EnhancedMigrated — use Default; Migrated is reserved for the " +
        "modern mirrors of buttons that still live in classic ribbon XML, and a hand-made row with " +
        "origin=Migrated has no ribbon CommandDefinition to bind to and does not render. " +
        "origin is create-only: a PATCH changing it returns 200 and silently keeps the old value, so a " +
        "command created with the wrong origin has to be deleted and recreated. " +
        "visibilitytype 0=None, 1=Formula, 2=ClassicRules — ClassicRules needs appactionrule rows linked " +
        "through the appaction_appactionrule_classicrules N:N, so use None for always-visible buttons. " +
        "onclickeventtype 2=JavaScript. type 0=StandardButton, 1=Dropdown, 2=Split, 3=Group.";

    private const string ParameterNote =
        "parameters is the argument list handed to the JavaScript function, stored as the JSON array " +
        "onclickeventjavascriptparameters. Accepts a comma-separated list of names, a JSON array of " +
        "names/numbers, or a JSON array of {\"type\":...,\"value\":...} objects. Verified type codes " +
        "(derived by aligning 470 migrated commands with the CrmParameter children of their ribbon " +
        "CommandDefinition): 1=PrimaryEntityTypeCode, 2=PrimaryEntityTypeName, 3=PrimaryItemIds, " +
        "4=FirstPrimaryItemId, 5=PrimaryControl, 7=SelectedEntityTypeCode, 8=SelectedEntityTypeName, " +
        "10=FirstSelectedItemId, 12=SelectedControl, 18=BoolParameter (literal), 20=IntParameter " +
        "(literal), 21=StringParameter (literal, set value), 23=SelectedControlSelectedItemIds, " +
        "24=SelectedControlSelectedItemReferences, 25=SelectedControlAllItemCount. " +
        "Typical form command: 'PrimaryControl'. Typical grid command: " +
        "'SelectedControlSelectedItemIds,SelectedControl'.";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "command_list")]
    [Description("List the modern commands (appaction rows) bound to a table, i.e. all rows whose " +
                 "contextvalue matches the logical name. Includes the managed system commands that " +
                 "were migrated from the classic ribbon (origin=Migrated) as well as hand-authored " +
                 "ones (origin=Default). " + SemanticsNote)]
    public static async Task<string> CommandList(
        CommandService svc,
        ConfigProvider config,
        [Description("Logical name of the table, e.g. 'sample_purchaseorder'")] string tableLogicalName,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ListAsync(env.OrgUrl, tableLogicalName, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "command_get")]
    [Description("Get the full definition of a modern command, including its decoded JavaScript " +
                 "parameter list with resolved type names. " + SemanticsNote)]
    public static async Task<string> CommandGet(
        CommandService svc,
        ConfigProvider config,
        [Description("The appaction GUID")] string appActionId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(appActionId, out var id))
            {
                return JsonSerializer.Serialize(new { error = "Invalid appActionId GUID format." });
            }

            var env = config.GetActiveEnvironment();
            var result = await svc.GetAsync(env.OrgUrl, id, ct);
            return result is null
                ? JsonSerializer.Serialize(new { error = "Command not found." })
                : JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "command_create")]
    [Description("Create a JavaScript-backed modern command on a table's command bar. The lookups are " +
                 "bound through their navigation property names ContextEntity (target collection " +
                 "'entities', value = the table's MetadataId) and OnClickEventJavaScriptWebResourceId " +
                 "(target collection 'webresourceset') — using the attribute names contextentity / " +
                 "onclickeventjavascriptwebresourceid in @odata.bind fails with 0x80048d19 " +
                 "'undeclared property'. Run publish_customizations for the table afterwards. " +
                 SemanticsNote + " " + ParameterNote)]
    public static async Task<string> CommandCreate(
        CommandService svc,
        WebResourceService webResourceSvc,
        SolutionService solutionSvc,
        ConfigProvider config,
        [Description("Logical name of the table the command belongs to, e.g. 'sample_purchaseorder'")] string tableLogicalName,
        [Description("Internal name of the command, e.g. 'sample.sample_purchaseorder.CorrectPrice.Form'")] string name,
        [Description("The label shown on the button")] string buttonLabelText,
        [Description("Command bar location: 0=Form, 1=MainGrid, 2=SubGrid, 3=AssociatedGrid, 4=QuickForm, 5=GlobalHeader, 6=Dashboard")] int location,
        [Description("Name or GUID of the JScript web resource holding the handler")] string javaScriptWebResourceName,
        [Description("Fully qualified JavaScript function, e.g. 'Sample.PurchaseOrder.CorrectPrice.onFormButton'")] string functionName,
        [Description("Parameter list for the handler — see the type table in this tool's description")] string parameters,
        [Description("Tooltip title")] string? tooltipTitle = null,
        [Description("Tooltip description")] string? tooltipDescription = null,
        [Description("Fluent icon, in the form used by system commands: '$clientsvg:Add', '$clientsvg:Edit', …")] string? fontIcon = null,
        [Description("Display order; system commands sit around 1000100200")] double? sequence = null,
        [Description("Optional unique name; defaults to '<prefix>_<name>!<table>!<location>'. It MUST " +
                     "start with a publisher customization prefix plus underscore, otherwise Dataverse " +
                     "rejects the create with 0x800608ad.")] string? uniqueName = null,
        [Description("Customization prefix for the generated unique name; defaults to the prefix of the " +
                     "table's logical name (e.g. 'xv' for 'sample_purchaseorder')")] string? customizationPrefix = null,
        [Description("Optional solution unique name — the command is added as component type 10343")] string? solutionUniqueName = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();

            var webResourceId = await ResolveWebResourceIdAsync(
                webResourceSvc, env.OrgUrl, javaScriptWebResourceName, ct);
            if (webResourceId is null)
            {
                return JsonSerializer.Serialize(
                    new { error = $"JavaScript web resource '{javaScriptWebResourceName}' not found." });
            }

            var parsed = CommandService.ParseParameterSpec(parameters);

            var id = await svc.CreateAsync(
                env.OrgUrl,
                tableLogicalName,
                name,
                buttonLabelText,
                location,
                webResourceId.Value,
                functionName,
                parsed,
                uniqueName,
                tooltipTitle,
                tooltipDescription,
                fontIcon,
                sequence is null ? null : (decimal)sequence.Value,
                customizationPrefix: customizationPrefix,
                ct: ct);

            var solutionComponentAdded = false;
            if (!string.IsNullOrWhiteSpace(solutionUniqueName))
            {
                await solutionSvc.AddComponentAsync(
                    env.OrgUrl, solutionUniqueName, id, CommandService.AppActionComponentType, ct);
                solutionComponentAdded = true;
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                appActionId = id,
                tableLogicalName,
                name,
                location,
                parameters = parsed,
                solutionUniqueName,
                solutionComponentAdded,
                note = "Run publish_customizations with entities='" + tableLogicalName +
                       "' before the button appears."
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "command_update")]
    [Description("Update properties of a modern command. Convenience arguments cover the common " +
                 "fields; propertiesJson is merged on top for anything else. Note that origin cannot " +
                 "be changed after create — Dataverse accepts the PATCH and keeps the old value; " +
                 "delete and recreate instead. Publish the table afterwards. " + ParameterNote)]
    public static async Task<string> CommandUpdate(
        CommandService svc,
        ConfigProvider config,
        [Description("The appaction GUID")] string appActionId,
        [Description("New button label")] string? buttonLabelText = null,
        [Description("New tooltip title")] string? tooltipTitle = null,
        [Description("New tooltip description")] string? tooltipDescription = null,
        [Description("New fully qualified JavaScript function name")] string? functionName = null,
        [Description("New parameter list — see the type table in this tool's description")] string? parameters = null,
        [Description("New Fluent icon, e.g. '$clientsvg:Edit'")] string? fontIcon = null,
        [Description("New display order")] double? sequence = null,
        [Description("Hide or show the button")] bool? hidden = null,
        [Description("Raw JSON object of additional appaction properties to PATCH")] string? propertiesJson = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(appActionId, out var id))
            {
                return JsonSerializer.Serialize(new { error = "Invalid appActionId GUID format." });
            }

            var props = new Dictionary<string, object?>();
            if (buttonLabelText is not null) { props["buttonlabeltext"] = buttonLabelText; }
            if (tooltipTitle is not null) { props["buttontooltiptitle"] = tooltipTitle; }
            if (tooltipDescription is not null) { props["buttontooltipdescription"] = tooltipDescription; }
            if (functionName is not null) { props["onclickeventjavascriptfunctionname"] = functionName; }
            if (fontIcon is not null) { props["fonticon"] = fontIcon; }
            if (sequence is not null) { props["sequence"] = (decimal)sequence.Value; }
            if (hidden is not null) { props["hidden"] = hidden.Value; }
            if (parameters is not null)
            {
                props["onclickeventjavascriptparameters"] =
                    CommandService.SerializeParameters(CommandService.ParseParameterSpec(parameters));
            }

            if (!string.IsNullOrWhiteSpace(propertiesJson))
            {
                var extra = JsonSerializer.Deserialize<Dictionary<string, object?>>(propertiesJson)
                            ?? throw new ArgumentException("propertiesJson could not be parsed.");
                foreach (var (k, v) in extra)
                {
                    props[k] = v;
                }
            }

            if (props.Count == 0)
            {
                return JsonSerializer.Serialize(new { error = "Nothing to update." });
            }

            var env = config.GetActiveEnvironment();
            await svc.UpdateAsync(env.OrgUrl, id, props, ct);

            var originIgnored = props.ContainsKey("origin");
            return JsonSerializer.Serialize(new
            {
                success = true,
                appActionId = id,
                updated = props.Keys,
                warning = originIgnored
                    ? "'origin' was included but Dataverse ignores it on update — delete and recreate " +
                      "the command if the origin must change."
                    : null
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "command_delete")]
    [Description("Delete a modern command (appaction row). Publish the table afterwards so the " +
                 "button disappears from the client.")]
    public static async Task<string> CommandDelete(
        CommandService svc,
        ConfigProvider config,
        [Description("The appaction GUID")] string appActionId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(appActionId, out var id))
            {
                return JsonSerializer.Serialize(new { error = "Invalid appActionId GUID format." });
            }

            var env = config.GetActiveEnvironment();
            await svc.DeleteAsync(env.OrgUrl, id, ct);
            return JsonSerializer.Serialize(new { success = true, appActionId = id });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    private static async Task<Guid?> ResolveWebResourceIdAsync(
        WebResourceService webResources,
        string orgUrl,
        string nameOrId,
        CancellationToken ct)
    {
        if (Guid.TryParse(nameOrId, out var id))
        {
            return id;
        }

        return await webResources.FindIdByNameAsync(orgUrl, nameOrId, ct);
    }
}
