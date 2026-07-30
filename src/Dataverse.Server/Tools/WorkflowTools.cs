namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Services;
using Dataverse.Core.Workflows;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class WorkflowTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "workflow_list")]
    [Description("List Classic Workflows in the configured Dataverse environment.")]
    public static async Task<string> WorkflowList(
        WorkflowService svc,
        ConfigProvider config,
        [Description("Optional OData filter (e.g. \"name eq 'MyWorkflow'\")")] string? filter = null,
        [Description("Maximum number of results to return (default 50)")] int top = 50,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ListAsync(env.OrgUrl, filter, top, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_get")]
    [Description("Get the full definition of a Classic Workflow, including its XAML.")]
    public static async Task<string> WorkflowGet(
        WorkflowService svc,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var env = config.GetActiveEnvironment();
            var result = await svc.GetAsync(env.OrgUrl, id, ct);
            return result is null
                ? JsonSerializer.Serialize(new { error = "Workflow not found." })
                : JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_export_xaml")]
    [Description("Export the raw XAML definition of a Classic Workflow, as-is. " +
                 "Use for inspection, backup or diffing. Always call this before changing a workflow " +
                 "so you have a restore point (write it back with workflow_restore_xaml). " +
                 "To understand a workflow prefer workflow_explain; to change one prefer " +
                 "workflow_set_definition, which generates conventional XAML for you.")]
    public static async Task<string> WorkflowExportXaml(
        WorkflowService svc,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var env = config.GetActiveEnvironment();
            var xaml = await svc.GetXamlAsync(env.OrgUrl, id, ct);
            return xaml is null
                ? JsonSerializer.Serialize(new { error = "Workflow not found or has no XAML." })
                : xaml;
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_create")]
    [Description("Create a new Classic Workflow (draft) for a given primary entity and return its id. " +
                 "The workflow starts with an empty but valid XAML skeleton; add logic with " +
                 "workflow_set_definition, then activate with workflow_set_state. " +
                 "Triggers (isoncreate, updatestage, ...) are set with workflow_update.")]
    public static async Task<string> WorkflowCreate(
        WorkflowService svc,
        ConfigProvider config,
        [Description("Display name of the workflow")] string name,
        [Description("Logical name of the primary entity (e.g. 'account')")] string primaryEntity,
        [Description("Optional description")] string? description = null,
        [Description("true = real-time (synchronous) workflow, false = background (default)")] bool isRealtime = false,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var id = await svc.CreateAsync(env.OrgUrl, name, primaryEntity, description, isRealtime, ct);
            return JsonSerializer.Serialize(new { workflowId = id, name, primaryEntity, isRealtime });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_update")]
    [Description("Update metadata properties of a Classic Workflow via OData PATCH. " +
                 "Updatable fields: name, description, " +
                 "primaryentity, scope (1=User,2=BU,3=ParentChildBU,4=Org), " +
                 "mode (0=Background,1=Realtime), runas (0=Owner,1=CallingUser), " +
                 "ondemand, isoncreate, isonupdate, isondelete, triggerattribute, " +
                 "createstage/updatestage/deletestage (20=Pre,40=Post), " +
                 "logcontent (0=None,1=Details,2=All), asyncautodelete, rank, istransacted, subprocess. " +
                 "For workflow LOGIC use workflow_set_definition instead of writing 'xaml' here — it " +
                 "generates XAML that follows the designer's naming conventions and validates it first. " +
                 "Writing 'xaml' directly is possible but only sensible to restore a backup " +
                 "(workflow_restore_xaml). Note: the workflow must be a draft for any change.")]
    public static async Task<string> WorkflowUpdate(
        WorkflowService svc,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        [Description("JSON object with properties to update (e.g. {\"logcontent\": 2, \"ondemand\": true})")] string propertiesJson,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var props = JsonSerializer.Deserialize<Dictionary<string, object?>>(propertiesJson)
                        ?? throw new ArgumentException("propertiesJson could not be parsed.");

            var env = config.GetActiveEnvironment();
            await svc.UpdateAsync(env.OrgUrl, id, props, ct);
            return JsonSerializer.Serialize(new { success = true, workflowId });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_set_state")]
    [Description("Activate or deactivate a Classic Workflow.")]
    public static async Task<string> WorkflowSetState(
        WorkflowService svc,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        [Description("true to activate, false to deactivate")] bool activate,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var env = config.GetActiveEnvironment();
            await svc.SetStateAsync(env.OrgUrl, id, activate, ct);
            return JsonSerializer.Serialize(new { success = true, workflowId, activate });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_assign")]
    [Description("Assign a Classic Workflow to a different owner (user or team).")]
    public static async Task<string> WorkflowAssign(
        WorkflowService svc,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        [Description("Owner type: 'user' or 'team'")] string ownerType,
        [Description("Owner GUID")] string ownerId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var wfId))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });
            if (!Guid.TryParse(ownerId, out var owId))
                return JsonSerializer.Serialize(new { error = "Invalid ownerId GUID format." });

            var env = config.GetActiveEnvironment();
            await svc.AssignAsync(env.OrgUrl, wfId, ownerType, owId, ct);
            return JsonSerializer.Serialize(new { success = true, workflowId, ownerType, ownerId });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_list_activities")]
    [Description("List all Custom Workflow Activities (code activities) registered in Dataverse, including their AssemblyQualifiedName for use in XAML.")]
    public static async Task<string> WorkflowListActivities(
        WorkflowService svc,
        ConfigProvider config,
        [Description("Optional name filter (substring match)")] string? nameFilter = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ListActivitiesAsync(env.OrgUrl, nameFilter, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_get_activity_parameters")]
    [Description("Get the Input and Output parameters of a Custom Workflow Activity, needed to wire up arguments in XAML.")]
    public static async Task<string> WorkflowGetActivityParameters(
        WorkflowService svc,
        ConfigProvider config,
        [Description("The PluginType GUID (from workflow_list_activities)")] string pluginTypeId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(pluginTypeId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid pluginTypeId GUID format." });

            var env = config.GetActiveEnvironment();
            var result = await svc.GetActivityParametersAsync(env.OrgUrl, id, ct);
            return result is null
                ? JsonSerializer.Serialize(new { error = "Plugin type not found." })
                : JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_explain")]
    [Description("Explain a Classic Workflow in readable form: triggers, execution settings and the " +
                 "full step tree with conditions and field assignments. Start here when asked what a " +
                 "workflow does. If the report ends with a 'Not understood' section, the workflow uses " +
                 "constructs this server cannot model — do not rewrite it with workflow_set_definition.")]
    public static async Task<string> WorkflowExplain(
        WorkflowAuthoringService authoring,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var env = config.GetActiveEnvironment();
            var (detail, parsed) = await authoring.GetDefinitionAsync(env.OrgUrl, id, ct);
            return WorkflowExplainer.Explain(detail, parsed);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_get_definition")]
    [Description("Read a Classic Workflow's logic as an editable JSON definition (the same shape " +
                 "workflow_set_definition accepts). Use this to modify an existing workflow: read, " +
                 "change the JSON, write it back. IMPORTANT: only write it back when " +
                 "'fullyUnderstood' is true — otherwise parts of the original would be lost.")]
    public static async Task<string> WorkflowGetDefinition(
        WorkflowAuthoringService authoring,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var env = config.GetActiveEnvironment();
            var (detail, parsed) = await authoring.GetDefinitionAsync(env.OrgUrl, id, ct);

            return JsonSerializer.Serialize(new
            {
                workflowId = id,
                name = detail.Name,
                stateCode = detail.StateCode,
                isDraft = detail.StateCode != 1,
                fullyUnderstood = parsed.FullyUnderstood,
                unrecognised = parsed.Unrecognised,
                notes = parsed.Notes,
                definition = parsed.Definition
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_validate_definition")]
    [Description("Check a workflow definition without writing anything. Validates structure, " +
                 "completeness, value expressions and — against live metadata — that every table and " +
                 "attribute exists. Returns issues with a code, a JSON path, the problem and the fix. " +
                 "Use this while composing a definition; workflow_set_definition runs the same checks " +
                 "and refuses to write when any error remains.")]
    public static async Task<string> WorkflowValidateDefinition(
        WorkflowAuthoringService authoring,
        ConfigProvider config,
        [Description("The workflow definition as JSON (see the classic-workflows skill for the shape)")] string definitionJson,
        CancellationToken ct = default)
    {
        try
        {
            var definition = ParseDefinition(definitionJson, out var parseError);
            if (definition is null)
                return JsonSerializer.Serialize(new { canSave = false, issues = new[] { parseError } }, JsonOptions);

            var env = config.GetActiveEnvironment();
            var result = await authoring.ValidateAsync(env.OrgUrl, definition, ct);

            return JsonSerializer.Serialize(new
            {
                canSave = result.CanSave,
                errorCount = result.ErrorCount,
                warningCount = result.WarningCount,
                issues = result.Issues
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_set_definition")]
    [Description("Write a workflow's logic from a JSON definition. This is the supported way to author " +
                 "Classic Workflow logic: step ids, DisplayNames and variables are generated so the " +
                 "Dataverse designer can still render the result. " +
                 "Nothing is written unless validation passes (structure, value expressions, and table/" +
                 "attribute existence) and the generated XAML passes a self-check. " +
                 "The workflow must be a draft; pass reactivate=true to deactivate, write and " +
                 "re-activate in one call. The response contains the previous XAML as 'backup'.")]
    public static async Task<string> WorkflowSetDefinition(
        WorkflowAuthoringService authoring,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        [Description("The workflow definition as JSON (see the classic-workflows skill for the shape)")] string definitionJson,
        [Description("If the workflow is active: deactivate, write, re-activate (default false = refuse)")] bool reactivate = false,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var definition = ParseDefinition(definitionJson, out var parseError);
            if (definition is null)
                return JsonSerializer.Serialize(new { applied = false, issues = new[] { parseError } }, JsonOptions);

            var env = config.GetActiveEnvironment();
            var result = await authoring.SetDefinitionAsync(env.OrgUrl, id, definition, reactivate, ct);

            return JsonSerializer.Serialize(new
            {
                applied = result.Applied,
                workflowId = result.WorkflowId,
                message = result.Message,
                stepIds = result.StepIds,
                canSave = result.Validation.CanSave,
                errorCount = result.Validation.ErrorCount,
                warningCount = result.Validation.WarningCount,
                issues = result.Validation.Issues,
                // Keep this to undo the change with workflow_restore_xaml.
                backup = result.Backup
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_restore_xaml")]
    [Description("Write a previously exported XAML back verbatim, to undo a change. " +
                 "Takes the string from workflow_export_xaml or the 'backup' field of " +
                 "workflow_set_definition. The workflow must be a draft.")]
    public static async Task<string> WorkflowRestoreXaml(
        WorkflowAuthoringService authoring,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        [Description("The XAML to restore")] string xaml,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });
            if (string.IsNullOrWhiteSpace(xaml))
                return JsonSerializer.Serialize(new { error = "xaml must not be empty." });

            var env = config.GetActiveEnvironment();
            await authoring.RestoreXamlAsync(env.OrgUrl, id, xaml, ct);
            return JsonSerializer.Serialize(new { success = true, workflowId });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    /// <summary>Deserialises a definition and turns failures into an actionable issue.</summary>
    private static WorkflowDefinition? ParseDefinition(string json, out object error)
    {
        error = new
        {
            severity = "error",
            code = "WF000",
            path = "$",
            problem = "definitionJson could not be parsed as JSON.",
            fix = "Pass a JSON object like " +
                  "{\"primaryEntity\":\"lead\",\"steps\":[{\"kind\":\"updateRecord\"," +
                  "\"attributes\":[{\"attribute\":\"subject\",\"value\":{\"kind\":\"literal\",\"literal\":\"x\"}}]}]}"
        };

        try
        {
            var definition = JsonSerializer.Deserialize<WorkflowDefinition>(json, DefinitionJsonOptions);
            if (definition is null)
                return null;
            error = null!;
            return definition;
        }
        catch (JsonException ex)
        {
            error = new
            {
                severity = "error",
                code = "WF000",
                path = "$",
                problem = "definitionJson is not valid JSON: " + ex.Message,
                fix = "Fix the JSON syntax. Field names are camelCase: kind, conditions, then, else, " +
                      "attributes, value, literal, fields, fallback, stepOutput."
            };
            return null;
        }
    }

    private static readonly JsonSerializerOptions DefinitionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [McpServerTool(Name = "workflow_validate")]
    [Description("Quick health check of a stored Classic Workflow (has XAML, has a primary entity, " +
                 "is activated). For checking a definition you are about to write, use " +
                 "workflow_validate_definition instead.")]
    public static async Task<string> WorkflowValidate(
        WorkflowService svc,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var env = config.GetActiveEnvironment();
            var report = await svc.ValidateAsync(env.OrgUrl, id, ct);
            return JsonSerializer.Serialize(report, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
