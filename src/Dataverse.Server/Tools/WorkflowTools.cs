namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Services;
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
    [Description("Update properties of a Classic Workflow via OData PATCH. " +
                 "Updatable fields include: name, description, xaml, " +
                 "primaryentity, scope (1=User,2=BU,3=ParentChildBU,4=Org), " +
                 "mode (0=Background,1=Realtime), runas (0=Owner,1=CallingUser), " +
                 "ondemand, isoncreate, isonupdate, isondelete, triggerattribute, " +
                 "createstage/updatestage/deletestage (20=Pre,40=Post), " +
                 "logcontent (0=None,1=Details,2=All), asyncautodelete, rank, istransacted.")]
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
}
