namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dataverse.Core.Config;
using Dataverse.Core.Services;
using Dataverse.Core.Workflows;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class WorkflowTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Options for parsing a <see cref="WorkflowDefinition"/> from tool input: enums as
    /// strings, case-insensitive, polymorphic "kind" discriminators handled by the records.</summary>
    private static readonly JsonSerializerOptions DefOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

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
    [Description("Export the raw XAML definition of a Classic Workflow. " +
                 "Returns the XAML string as-is for inspection, backup, or documentation purposes. " +
                 "Call this BEFORE any workflow_update call to have a restore point. " +
                 "Do not attempt to write modified XAML back via workflow_update — " +
                 "structural changes corrupt the Dataverse designer (error 0x80045037).")]
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
    [Description("Create a new Classic Workflow for a given primary entity.")]
    public static async Task<string> WorkflowCreate(
        WorkflowService svc,
        ConfigProvider config,
        [Description("Display name of the workflow")] string name,
        [Description("Logical name of the primary entity (e.g. 'account')")] string primaryEntity,
        [Description("Optional description")] string? description = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var id = await svc.CreateAsync(env.OrgUrl, name, primaryEntity, description, ct);
            return JsonSerializer.Serialize(new { workflowId = id, name });
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
                 "logcontent (0=None,1=Details,2=All), asyncautodelete, rank, istransacted. " +
                 "WARNING: Do NOT include 'xaml' in the properties. Dataverse Classic Workflow XAML " +
                 "uses a WF4-based format with internal UiData linked to every visual step. " +
                 "Structural changes via XAML PATCH will corrupt the designer (error 0x80045037). " +
                 "To inspect XAML use workflow_export_xaml. To change workflow logic, use the Dataverse designer.")]
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

    [McpServerTool(Name = "workflow_validate")]
    [Description("Validate a Classic Workflow and return a report of common issues.")]
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

    [McpServerTool(Name = "workflow_interpret")]
    [Description("Read and explain a Classic Workflow's XAML as a structured, human-readable summary. " +
                 "Works on ANY workflow (best-effort): recognised steps (Update/Create entity, Custom Activity, " +
                 "Condition) are described; anything else is reported as 'Unknown'. " +
                 "'isTemplateRecognized' tells you whether the workflow is simple enough to be edited via workflow_update_simple.")]
    public static async Task<string> WorkflowInterpret(
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
            var result = await svc.InterpretAsync(env.OrgUrl, id, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_validate_definition")]
    [Description("Dry-run validate a workflow definition JSON without writing anything. " +
                 "Returns errors (blocking) and warnings (non-blocking). " +
                 "Call workflow_list_templates to learn the definition JSON shape.")]
    public static string WorkflowValidateDefinition(
        [Description("Workflow definition as JSON (see workflow_list_templates)")] string definitionJson)
    {
        try
        {
            if (!TryParseDefinition(definitionJson, out var def, out var parseError))
                return JsonSerializer.Serialize(new { error = parseError });

            // Structural validation is pure (no network). Semantic (activity registration) checks
            // run only inside create/update where the live environment is available.
            var structural = WorkflowXamlValidator.Validate(def!);
            return JsonSerializer.Serialize(structural, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_create_simple")]
    [Description("Create a Classic Workflow from a template definition (JSON) and write its XAML. " +
                 "Supports only simple, safe cases: updating the primary entity, creating a record, and " +
                 "calling a Custom Workflow Activity with literal and/or field-sourced arguments. " +
                 "Validates before writing; if validation fails nothing is created. " +
                 "Call workflow_list_templates first to learn the JSON shape and supported value types.")]
    public static async Task<string> WorkflowCreateSimple(
        WorkflowService svc,
        ConfigProvider config,
        [Description("Display name of the workflow")] string name,
        [Description("Workflow definition as JSON (primaryEntity, mode, scope, trigger, steps)")] string definitionJson,
        [Description("Optional description")] string? description = null,
        [Description("Activate the workflow after creation (default false)")] bool activate = false,
        CancellationToken ct = default)
    {
        try
        {
            if (!TryParseDefinition(definitionJson, out var def, out var parseError))
                return JsonSerializer.Serialize(new { error = parseError });

            var env = config.GetActiveEnvironment();
            var result = await svc.CreateFromDefinitionAsync(env.OrgUrl, name, def!, description, activate, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_update_simple")]
    [Description("Re-generate and write the XAML of an existing Classic Workflow from a template definition (JSON). " +
                 "Only succeeds if the workflow's current XAML is a recognised MCP template (use workflow_interpret " +
                 "to check 'isTemplateRecognized'); otherwise it refuses to avoid corrupting hand-built logic. " +
                 "The workflow is deactivated during the write and its prior activation state restored.")]
    public static async Task<string> WorkflowUpdateSimple(
        WorkflowService svc,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        [Description("Workflow definition as JSON (see workflow_list_templates)")] string definitionJson,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            if (!TryParseDefinition(definitionJson, out var def, out var parseError))
                return JsonSerializer.Serialize(new { error = parseError });

            var env = config.GetActiveEnvironment();
            var result = await svc.UpdateFromDefinitionAsync(env.OrgUrl, id, def!, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_list_templates")]
    [Description("Describe the supported workflow template cases and the exact JSON shape accepted by " +
                 "workflow_create_simple / workflow_update_simple / workflow_validate_definition, including " +
                 "enums, step kinds, argument value kinds and supported value types.")]
    public static string WorkflowListTemplates() => TemplateDoc;

    private static bool TryParseDefinition(string json, out WorkflowDefinition? def, out string? error)
    {
        def = null;
        error = null;
        try
        {
            def = JsonSerializer.Deserialize<WorkflowDefinition>(json, DefOptions);
            if (def is null)
            {
                error = "definitionJson deserialized to null.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"definitionJson could not be parsed: {ex.Message}";
            return false;
        }
    }

    private const string TemplateDoc = """
    {
      "description": "JSON shape for workflow_create_simple / workflow_update_simple / workflow_validate_definition.",
      "definition": {
        "primaryEntity": "logical name of the primary entity, e.g. 'account'",
        "mode": "Background (async) | Realtime (sync)",
        "scope": "User | BusinessUnit | ParentChildBusinessUnit | Organization",
        "trigger": {
          "onDemand": "bool",
          "onCreate": "bool",
          "onUpdate": "bool",
          "onDelete": "bool",
          "updateAttributes": ["attribute logical names that trigger an onUpdate workflow"],
          "stage": "PreOperation | PostOperation (PostOperation for background)"
        },
        "steps": ["array of step objects, each with a 'kind' discriminator"]
      },
      "stepKinds": {
        "updateEntity": {
          "kind": "updateEntity",
          "entityName": "must equal primaryEntity in this version",
          "assignments": [{ "attribute": "logical name", "type": "<valueType>", "value": "<argumentValue>" }]
        },
        "createEntity": {
          "kind": "createEntity",
          "entityName": "logical name of the entity to create",
          "assignments": [{ "attribute": "logical name", "type": "<valueType>", "value": "<argumentValue>" }]
        },
        "customActivity": {
          "kind": "customActivity",
          "assemblyQualifiedName": "from workflow_list_activities (AssemblyQualifiedName)",
          "label": "display label",
          "inputArguments": [{ "name": "ParamName", "type": "<valueType>", "value": "<argumentValue>" }],
          "outputArguments": [{ "name": "ParamName", "type": "<valueType>", "outputVariable": "optional variable name" }]
        }
      },
      "argumentValue": {
        "literal": { "kind": "literal", "type": "<valueType>", "value": "string form", "entityReferenceLogicalName": "only for EntityReference literals (custom-activity inputs only)" },
        "field": { "kind": "field", "attribute": "primary-entity attribute to read", "type": "<valueType>" }
      },
      "valueTypes": ["String", "Integer", "Decimal", "Double", "Money", "Boolean", "OptionSet", "DateTime", "EntityReference", "Guid"],
      "notes": [
        "EntityReference literals are NOT allowed for entity field assignments; use a field reference instead.",
        "updateEntity can only target the primary entity in this version.",
        "OptionSet literal value is the integer option value as a string.",
        "Workflows that contain conditions or other unsupported activities can be read via workflow_interpret but not edited via workflow_update_simple."
      ],
      "example": {
        "primaryEntity": "opportunity",
        "mode": "Realtime",
        "scope": "Organization",
        "trigger": { "onUpdate": true, "updateAttributes": ["statuscode"], "stage": "PostOperation" },
        "steps": [
          { "kind": "updateEntity", "entityName": "opportunity",
            "assignments": [
              { "attribute": "description", "type": "String", "value": { "kind": "literal", "type": "String", "value": "Closed via workflow" } }
            ] }
        ]
      }
    }
    """;
}
