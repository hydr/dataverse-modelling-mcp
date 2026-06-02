namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Services;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class SolutionTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "solution_list")]
    [Description("List all visible solutions in the Dataverse environment.")]
    public static async Task<string> SolutionList(
        SolutionService svc,
        ConfigProvider config,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ListAsync(env.OrgUrl, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "solution_get")]
    [Description("Get a solution by unique name, including its components.")]
    public static async Task<string> SolutionGet(
        SolutionService svc,
        ConfigProvider config,
        [Description("Unique name of the solution")] string uniqueName,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.GetAsync(env.OrgUrl, uniqueName, ct);
            return result is null
                ? JsonSerializer.Serialize(new { error = "Solution not found." })
                : JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "solution_create")]
    [Description("Create a new unmanaged solution.")]
    public static async Task<string> SolutionCreate(
        SolutionService svc,
        ConfigProvider config,
        [Description("Unique name (no spaces)")] string uniqueName,
        [Description("Friendly display name")] string displayName,
        [Description("Publisher unique name")] string publisherUniqueName,
        [Description("Version string (e.g. '1.0.0.0')")] string version,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            await svc.CreateAsync(env.OrgUrl, uniqueName, displayName, publisherUniqueName, version, ct);
            return JsonSerializer.Serialize(new { success = true, uniqueName });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "solution_export")]
    [Description("Export a solution as a zip. If filePath is given, writes the zip to disk and returns the path + size (recommended for large solutions). Otherwise returns the base64-encoded zip content inline.")]
    public static async Task<string> SolutionExport(
        SolutionService svc,
        ConfigProvider config,
        [Description("Unique name of the solution")] string uniqueName,
        [Description("true to export as managed, false for unmanaged")] bool managed = false,
        [Description("Optional local path to write the zip to (e.g. 'C:\\\\temp\\\\MySolution.zip'). When set, base64 is NOT returned — only the path and byte size.")] string? filePath = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ExportAsync(env.OrgUrl, uniqueName, managed, filePath, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "solution_import")]
    [Description("Import a solution asynchronously (ImportSolutionAsync + ImportJob polling). Provide EITHER filePath (read zip from disk, recommended) OR zipBase64. Blocks until the import finishes, then returns success plus any per-component errors parsed from the import job.")]
    public static async Task<string> SolutionImport(
        SolutionService svc,
        ConfigProvider config,
        [Description("Local path to a solution zip on disk. Takes precedence over zipBase64.")] string? filePath = null,
        [Description("Base64-encoded solution zip content (alternative to filePath).")] string? zipBase64 = null,
        [Description("true to overwrite unmanaged customizations")] bool overwriteUnmanaged = false,
        [Description("Max seconds to wait for the import to finish (default 600 = 10 min). Raise for large solutions.")] int timeoutSeconds = 600,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) && string.IsNullOrWhiteSpace(zipBase64))
                return JsonSerializer.Serialize(new { error = "Provide either filePath or zipBase64." });

            var env = config.GetActiveEnvironment();
            var result = await svc.ImportAsync(env.OrgUrl, zipBase64, overwriteUnmanaged, filePath, timeoutSeconds, pollIntervalSeconds: 5, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "solution_add_component")]
    [Description("Add a component to a solution.")]
    public static async Task<string> SolutionAddComponent(
        SolutionService svc,
        ConfigProvider config,
        [Description("Unique name of the solution")] string solutionUniqueName,
        [Description("GUID of the component")] string componentId,
        [Description("Component type code (e.g. 1=Entity, 24=Workflow, 92=Role)")] int componentType,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(componentId, out var compId))
                return JsonSerializer.Serialize(new { error = "Invalid componentId GUID format." });

            var env = config.GetActiveEnvironment();
            await svc.AddComponentAsync(env.OrgUrl, solutionUniqueName, compId, componentType, ct);
            return JsonSerializer.Serialize(new { success = true, solutionUniqueName, componentId });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "solution_remove_component")]
    [Description("Remove a component from a solution.")]
    public static async Task<string> SolutionRemoveComponent(
        SolutionService svc,
        ConfigProvider config,
        [Description("Unique name of the solution")] string solutionUniqueName,
        [Description("GUID of the component")] string componentId,
        [Description("Component type code")] int componentType,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(componentId, out var compId))
                return JsonSerializer.Serialize(new { error = "Invalid componentId GUID format." });

            var env = config.GetActiveEnvironment();
            await svc.RemoveComponentAsync(env.OrgUrl, solutionUniqueName, compId, componentType, ct);
            return JsonSerializer.Serialize(new { success = true, solutionUniqueName, componentId });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "solution_check_layers")]
    [Description("Check the solution layers for a given component.")]
    public static async Task<string> SolutionCheckLayers(
        SolutionService svc,
        ConfigProvider config,
        [Description("GUID of the component")] string componentId,
        [Description("Component type code")] int componentType,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(componentId, out var compId))
                return JsonSerializer.Serialize(new { error = "Invalid componentId GUID format." });

            var env = config.GetActiveEnvironment();
            var result = await svc.CheckLayersAsync(env.OrgUrl, compId, componentType, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "solution_remove_active_layer")]
    [Description("Remove the active customization layer for a component (calls RemoveActiveCustomizations).")]
    public static async Task<string> SolutionRemoveActiveLayer(
        SolutionService svc,
        ConfigProvider config,
        [Description("GUID of the component")] string componentId,
        [Description("Component type code")] int componentType,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(componentId, out var compId))
                return JsonSerializer.Serialize(new { error = "Invalid componentId GUID format." });

            var env = config.GetActiveEnvironment();
            await svc.RemoveActiveLayerAsync(env.OrgUrl, compId, componentType, ct);
            return JsonSerializer.Serialize(new { success = true, componentId });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "pipeline_list")]
    [Description("List all Power Platform Pipelines visible on a Pipeline-Host environment. The Pipeline-Host is typically a dedicated env (NOT the source/target) where the deploymentpipeline rows live.")]
    public static async Task<string> PipelineList(
        SolutionService svc,
        ConfigProvider config,
        [Description("Pipeline-Host org URL, e.g. https://orgexample.crm4.dynamics.com")] string pipelineHostOrgUrl,
        CancellationToken ct = default)
    {
        try
        {
            _ = config.Config;
            var result = await svc.ListPipelinesAsync(pipelineHostOrgUrl, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "pipeline_stages")]
    [Description("List the stages of a Power Platform Pipeline, including each stage's target deployment environment.")]
    public static async Task<string> PipelineStages(
        SolutionService svc,
        ConfigProvider config,
        [Description("Pipeline-Host org URL")] string pipelineHostOrgUrl,
        [Description("Pipeline GUID (deploymentpipelineid)")] Guid pipelineId,
        CancellationToken ct = default)
    {
        try
        {
            _ = config.Config;
            var result = await svc.ListPipelineStagesAsync(pipelineHostOrgUrl, pipelineId, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "pipeline_environments")]
    [Description("List the deployment-environment mappings on a Pipeline-Host. Maps Power-Platform env GUIDs to their pipeline-internal mapping rows (needed as devDeploymentEnvironmentId for deploys).")]
    public static async Task<string> PipelineEnvironments(
        SolutionService svc,
        ConfigProvider config,
        [Description("Pipeline-Host org URL")] string pipelineHostOrgUrl,
        CancellationToken ct = default)
    {
        try
        {
            _ = config.Config;
            var result = await svc.ListDeploymentEnvironmentsAsync(pipelineHostOrgUrl, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    // NOTE: solution_deploy_pipeline, pipeline_auth_init and pipeline_auth_complete were removed
    // from the MCP surface on 2026-06-02. They cannot work from a headless MCP: the Pipeline-Backend
    // only triggers validation for runs created with the Power Apps Maker AppId (a8f7a65c…), and that
    // token is not obtainable headlessly (device-code needs a secret; FOCI exchange is cross-family;
    // no capturable redirect URI for auth-code+PKCE). The create payload/sequence were otherwise
    // HAR-verified correct. The tool wrappers are parked verbatim in Tools/_parked/PipelineDeployTools.cs.txt
    // and the underlying SolutionService.DeployPipelineAsync + DataverseTokenProvider Maker methods are
    // kept intact, so the feature can be revived if a viable auth path appears. See the solution-pipelines
    // skill ("Token-AppId blocker") and memory project_pipeline_headless_appid_blocked for details.

    [McpServerTool(Name = "pipeline_run_status")]
    [Description("Get the status of a deployment stage run by id. Includes stagerunstatus, operation, operationstatus, validation results, error message. Returns formatted values where available.")]
    public static async Task<string> PipelineRunStatus(
        SolutionService svc,
        ConfigProvider config,
        [Description("Pipeline-Host org URL")] string pipelineHostOrgUrl,
        [Description("Deployment stage run GUID")] Guid stageRunId,
        CancellationToken ct = default)
    {
        try
        {
            _ = config.Config;
            var result = await svc.GetDeploymentStageRunAsync(pipelineHostOrgUrl, stageRunId, ct);
            return result.GetRawText();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
