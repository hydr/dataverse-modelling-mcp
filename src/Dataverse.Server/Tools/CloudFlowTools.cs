namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Services;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class CloudFlowTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "flow_list")]
    [Description("List Power Automate Cloud Flows in the configured environment.")]
    public static async Task<string> FlowList(
        CloudFlowService svc,
        ConfigProvider config,
        [Description("Optional OData filter string")] string? filter = null,
        [Description("Maximum number of results to return (default 50)")] int top = 50,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ListAsync(env.Region, env.EnvironmentId!, filter, top, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_get")]
    [Description("Get the full definition of a Cloud Flow.")]
    public static async Task<string> FlowGet(
        CloudFlowService svc,
        ConfigProvider config,
        [Description("The flow GUID or name")] string flowId,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.GetAsync(env.Region, env.EnvironmentId!, flowId, ct);
            return result is null
                ? JsonSerializer.Serialize(new { error = "Flow not found." })
                : JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_create")]
    [Description("Create a new Cloud Flow with the given display name and JSON definition.")]
    public static async Task<string> FlowCreate(
        CloudFlowService svc,
        ConfigProvider config,
        [Description("Display name for the flow")] string displayName,
        [Description("Flow definition as JSON object")] string definitionJson,
        CancellationToken ct = default)
    {
        try
        {
            var definition = JsonSerializer.Deserialize<JsonElement>(definitionJson);
            var env = config.GetActiveEnvironment();
            var flowId = await svc.CreateAsync(env.Region, env.EnvironmentId!, displayName, definition, ct);
            return JsonSerializer.Serialize(new { flowId, displayName });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_update")]
    [Description("Update the definition of a Cloud Flow.")]
    public static async Task<string> FlowUpdate(
        CloudFlowService svc,
        ConfigProvider config,
        [Description("The flow GUID")] string flowId,
        [Description("Updated flow definition as JSON object")] string definitionJson,
        CancellationToken ct = default)
    {
        try
        {
            var definition = JsonSerializer.Deserialize<JsonElement>(definitionJson);
            var env = config.GetActiveEnvironment();
            await svc.UpdateAsync(env.Region, env.EnvironmentId!, flowId, definition, ct);
            return JsonSerializer.Serialize(new { success = true, flowId });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_set_state")]
    [Description("Enable or disable a Cloud Flow.")]
    public static async Task<string> FlowSetState(
        CloudFlowService svc,
        ConfigProvider config,
        [Description("The flow GUID")] string flowId,
        [Description("true to enable, false to disable")] bool enable,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            await svc.SetStateAsync(env.Region, env.EnvironmentId!, flowId, enable, ct);
            return JsonSerializer.Serialize(new { success = true, flowId, enable });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_get_runs")]
    [Description("Get the run history of a Cloud Flow.")]
    public static async Task<string> FlowGetRuns(
        CloudFlowService svc,
        ConfigProvider config,
        [Description("The flow GUID")] string flowId,
        [Description("Maximum number of runs to return (default 25)")] int top = 25,
        [Description("Optional OData filter")] string? filter = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.GetRunsAsync(env.Region, env.EnvironmentId!, flowId, top, filter, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_describe")]
    [Description("Get a human-readable description of a Cloud Flow's trigger and actions.")]
    public static async Task<string> FlowDescribe(
        CloudFlowService svc,
        ConfigProvider config,
        [Description("The flow GUID")] string flowId,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.DescribeAsync(env.Region, env.EnvironmentId!, flowId, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
