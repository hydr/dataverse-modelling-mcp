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
    [Description(
        "Create a new Cloud Flow. " +
        "If solutionUniqueName is provided, creates a solution-aware flow directly in Dataverse " +
        "(supports flow_get_clientdata / flow_save_draft / versioning). " +
        "Without solutionUniqueName, creates a personal flow via the Power Automate API " +
        "(no versioning, clientdata tools not available).")]
    public static async Task<string> FlowCreate(
        CloudFlowService svc,
        FlowVersionService versionSvc,
        ConfigProvider config,
        [Description("Display name for the flow")] string displayName,
        [Description("Flow definition as JSON object (Logic Apps schema)")] string definitionJson,
        [Description("Solution unique name — required to create a solution-aware flow that supports versioning and draft/publish tools")]
            string? solutionUniqueName = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();

            if (!string.IsNullOrWhiteSpace(solutionUniqueName))
            {
                var definition = JsonSerializer.Deserialize<JsonElement>(definitionJson);
                var clientData = JsonSerializer.Serialize(new
                {
                    properties = new
                    {
                        connectionReferences = new { },
                        definition
                    },
                    schemaVersion = "1.0.0.0"
                });

                var flowId = await versionSvc.CreateSolutionAwareAsync(
                    env.OrgUrl, displayName, clientData, solutionUniqueName, ct);
                return JsonSerializer.Serialize(new { flowId, displayName, solutionUniqueName, solutionAware = true });
            }
            else
            {
                var definition = JsonSerializer.Deserialize<JsonElement>(definitionJson);
                var flowId = await svc.CreateAsync(env.Region, env.EnvironmentId!, displayName, definition, ct);
                return JsonSerializer.Serialize(new { flowId, displayName, solutionAware = false });
            }
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

    [McpServerTool(Name = "flow_list_versions")]
    [Description("List version history of a solution-aware Cloud Flow (latest first). Only works for solution flows.")]
    public static async Task<string> FlowListVersions(
        FlowVersionService svc,
        ConfigProvider config,
        [Description("The flow / workflow GUID")] Guid flowId,
        [Description("Max versions to return (default 50)")] int top = 50,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ListAsync(env.OrgUrl, flowId, top, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_get_version")]
    [Description("Get metadata for a single Cloud Flow version (no snapshot content; that is not exposed by the componentversion table).")]
    public static async Task<string> FlowGetVersion(
        FlowVersionService svc,
        ConfigProvider config,
        [Description("The flow / workflow GUID")] Guid flowId,
        [Description("The componentversion GUID")] Guid versionId,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.GetAsync(env.OrgUrl, flowId, versionId, ct);
            return result is null
                ? JsonSerializer.Serialize(new { error = "Version not found." })
                : JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_publish")]
    [Description("Publish the current draft of a solution-aware Cloud Flow. Creates a new Published component version (snapshot). By default the flow is NOT activated — pass activateFlow=true to publish-and-activate in one call.")]
    public static async Task<string> FlowPublish(
        FlowVersionService svc,
        ConfigProvider config,
        [Description("The flow / workflow GUID")] Guid flowId,
        [Description("If true, also activate the flow at runtime (statecode=1). Default false (publish only).")] bool activateFlow = false,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            await svc.PublishAsync(env.OrgUrl, flowId, activateFlow, ct);
            return JsonSerializer.Serialize(new { success = true, flowId, activated = activateFlow });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_save_draft")]
    [Description("Save a draft of a solution-aware Cloud Flow without publishing. Creates an Update component version row but does not promote the changes to the live runtime.")]
    public static async Task<string> FlowSaveDraft(
        FlowVersionService svc,
        ConfigProvider config,
        [Description("The flow / workflow GUID")] Guid flowId,
        [Description("The new clientdata JSON string (stringified Logic Apps definition wrapper).")] string clientData,
        [Description("Optional new display name.")] string? name = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            await svc.SaveDraftAsync(env.OrgUrl, flowId, clientData, name, ct);
            return JsonSerializer.Serialize(new { success = true, flowId });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_restore_version")]
    [Description("Restore a Cloud Flow to a previous version. Creates a new draft on the flow with the chosen snapshot; the draft must still be published to take effect at runtime.")]
    public static async Task<string> FlowRestoreVersion(
        FlowVersionService svc,
        ConfigProvider config,
        [Description("The flow / workflow GUID")] Guid flowId,
        [Description("The componentversion GUID to restore from")] Guid versionId,
        [Description("Optional change summary note")] string? changeSummary = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.RestoreAsync(env.OrgUrl, flowId, versionId, changeSummary, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_get_clientdata")]
    [Description("Get the raw stringified clientdata of a solution-aware Cloud Flow (the full Logic Apps wrapper including connectionReferences, definition, schemaVersion). Use this when you need to mutate the flow's JSON locally — flow_get only returns the inner definition and loses connectionReferences.")]
    public static async Task<string> FlowGetClientData(
        FlowVersionService svc,
        ConfigProvider config,
        [Description("The flow / workflow GUID")] Guid flowId,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var data = await svc.GetClientDataAsync(env.OrgUrl, flowId, ct);
            return JsonSerializer.Serialize(new { flowId, length = data.Length, clientdata = data });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_patch_action_input")]
    [Description("Surgically update one parameter on a single action's inputs.parameters object inside a solution-aware Cloud Flow. Avoids round-tripping the full clientdata. Common use cases: replace a List action's fetchXml, change a Compose action's input, add a recipient. Optionally publishes the draft immediately.")]
    public static async Task<string> FlowPatchActionInput(
        FlowVersionService svc,
        ConfigProvider config,
        [Description("The flow / workflow GUID")] Guid flowId,
        [Description("Action name as it appears in clientdata.properties.definition.actions, e.g. List_Lost_Customers")] string actionName,
        [Description("Parameter key directly under inputs.parameters, e.g. 'fetchXml' or 'item/subject' (slashes are part of the key, not a path separator)")] string parameterName,
        [Description("New value as JSON (string, number, bool, object, or array). Strings must be JSON-quoted.")] string valueJson,
        [Description("If true, also publish the draft right after saving (default false — just save draft).")] bool publish = false,
        [Description("If publish=true, also activate the flow at runtime (default false).")] bool activateFlow = false,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            await svc.PatchActionInputAsync(env.OrgUrl, flowId, actionName, parameterName, valueJson, publish, activateFlow, ct);
            return JsonSerializer.Serialize(new { success = true, flowId, actionName, parameterName, published = publish });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_save_draft_and_publish")]
    [Description("Atomic save-draft + publish on a solution-aware Cloud Flow. Mirrors the Maker UI 'Save and Publish' button. Use when you have the full new clientdata in hand.")]
    public static async Task<string> FlowSaveDraftAndPublish(
        FlowVersionService svc,
        ConfigProvider config,
        [Description("The flow / workflow GUID")] Guid flowId,
        [Description("The new clientdata JSON string")] string clientData,
        [Description("Optional new display name")] string? name = null,
        [Description("If true, also activate the flow at runtime (default false — publish only)")] bool activateFlow = false,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            await svc.SaveDraftAndPublishAsync(env.OrgUrl, flowId, clientData, name, activateFlow, ct);
            return JsonSerializer.Serialize(new { success = true, flowId, activated = activateFlow });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "fetchxml_validate")]
    [Description("Read-only sanity check for a FetchXML: runs it against Dataverse, reports whether it parses and how many rows it returns. Optionally returns the first N raw row samples. Use as a pre-flight check before patching the FetchXML into a flow action.")]
    public static async Task<string> FetchXmlValidate(
        FlowVersionService svc,
        ConfigProvider config,
        [Description("Entity set name (plural), e.g. 'accounts', 'contacts', 'leads'")] string entitySet,
        [Description("The FetchXML to validate")] string fetchXml,
        [Description("If > 0, include this many rows as raw JSON in the result")] int sampleSize = 0,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ValidateFetchXmlAsync(env.OrgUrl, entitySet, fetchXml, sampleSize, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_trigger_run")]
    [Description("Manually trigger a Cloud Flow run via the PA Flow API (equivalent of the Maker UI 'Run flow' button). Returns the newly created RunId. For Recurrence-triggered flows the default triggerName 'Recurrence' is correct.")]
    public static async Task<string> FlowTriggerRun(
        CloudFlowService svc,
        ConfigProvider config,
        [Description("The flow GUID")] string flowId,
        [Description("Trigger name as defined in the flow definition (default 'Recurrence')")] string triggerName = "Recurrence",
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var runId = await svc.TriggerRunAsync(env.Region, env.EnvironmentId!, flowId, triggerName, null, ct);
            return JsonSerializer.Serialize(new { flowId, runId, triggered = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_wait_for_run")]
    [Description("Block (server-side polling) until a flow run reaches a terminal state (anything other than 'Running'), or the timeout elapses. Returns the final run status incl. error code if any. Default timeout 300s, poll every 10s.")]
    public static async Task<string> FlowWaitForRun(
        CloudFlowService svc,
        ConfigProvider config,
        [Description("The flow GUID")] string flowId,
        [Description("The run id returned by flow_trigger_run / flow_get_runs")] string runId,
        [Description("Maximum seconds to wait (default 300)")] int timeoutSeconds = 300,
        [Description("Polling interval in seconds (default 10)")] int pollIntervalSeconds = 10,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.WaitForRunAsync(env.Region, env.EnvironmentId!, flowId, runId, timeoutSeconds, pollIntervalSeconds, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_get_run_actions")]
    [Description("List all actions of a single run with status, error code/message, and outputs link. Uses the per-run actions endpoint that is stable even when the run-detail $expand=properties/actions intermittently fails with HTML runtime errors.")]
    public static async Task<string> FlowGetRunActions(
        CloudFlowService svc,
        ConfigProvider config,
        [Description("The flow GUID")] string flowId,
        [Description("The run id")] string runId,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.GetRunActionsAsync(env.Region, env.EnvironmentId!, flowId, runId, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "flow_get_action_outputs")]
    [Description("Fetch the JSON outputs of a single action in a run (resolves the SAS-signed outputsLink). Returns the raw body as a string — useful for inspecting Compose outputs, ListRecords result counts, or failed-action error bodies.")]
    public static async Task<string> FlowGetActionOutputs(
        CloudFlowService svc,
        ConfigProvider config,
        [Description("The flow GUID")] string flowId,
        [Description("The run id")] string runId,
        [Description("The action name (as in clientdata.actions, e.g. 'Compose_Email_Body')")] string actionName,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.GetActionOutputsAsync(env.Region, env.EnvironmentId!, flowId, runId, actionName, ct);
            return JsonSerializer.Serialize(new { flowId, runId, actionName, length = result.Length, outputs = result });
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
