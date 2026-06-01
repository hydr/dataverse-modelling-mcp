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
    [Description("Export a solution as a zip file. Returns the base64-encoded zip content.")]
    public static async Task<string> SolutionExport(
        SolutionService svc,
        ConfigProvider config,
        [Description("Unique name of the solution")] string uniqueName,
        [Description("true to export as managed, false for unmanaged")] bool managed = false,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ExportAsync(env.OrgUrl, uniqueName, managed, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "solution_import")]
    [Description("Import a solution from a base64-encoded zip string.")]
    public static async Task<string> SolutionImport(
        SolutionService svc,
        ConfigProvider config,
        [Description("Base64-encoded solution zip content")] string zipBase64,
        [Description("true to overwrite unmanaged customizations")] bool overwriteUnmanaged = false,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            await svc.ImportAsync(env.OrgUrl, zipBase64, overwriteUnmanaged, ct);
            return JsonSerializer.Serialize(new { success = true });
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

    [McpServerTool(Name = "solution_deploy_pipeline")]
    [Description("End-to-end Power Platform Pipeline deployment. Mirrors the 3 calls the Maker UI makes when clicking 'Bereitstellung' → 'Weiter' → 'Bereitstellen': (1) POST /deploymentstageruns (validation), (2) PATCH (version + notes), (3) POST /DeployPackageAsync (start). With autoConfirm=false only step 1 runs. Returns the stage-run id; use pipeline_run_status to poll until terminal.")]
    public static async Task<string> SolutionDeployPipeline(
        SolutionService svc,
        ConfigProvider config,
        [Description("Pipeline-Host org URL (where the deploymentpipeline rows live), e.g. https://orgexample.crm4.dynamics.com")] string pipelineHostOrgUrl,
        [Description("Solution GUID from the source (Dev) environment")] Guid solutionId,
        [Description("Solution unique name (also used as artifactname in the pipeline)")] string artifactName,
        [Description("Internal deploymentenvironmentid of the source/Dev environment on the Pipeline-Host (NOT the Power Platform env GUID). Resolve via pipeline_environments.")] Guid devDeploymentEnvironmentId,
        [Description("Target stage GUID (deploymentstageid). Resolve via pipeline_stages.")] Guid targetStageId,
        [Description("Current solution version in the source env (e.g. '1.10.0'). Sent as artifactdevcurrentversion. Required when autoConfirm=true.")] string? currentVersion = null,
        [Description("Target solution version after deploy (e.g. '1.11.0'). Sent as artifactversion. Required when autoConfirm=true.")] string? newVersion = null,
        [Description("Deployment notes shown in the stage-run history. Required when autoConfirm=true to match Maker-UI behaviour.")] string deploymentNotes = "",
        [Description("Optional deployment-settings JSON string. Required if the solution has pflicht-zu-setzende Environment Variables or Connection References on the target. Shape: '{\"EnvironmentVariables\":[{\"SchemaName\":\"sample_my_var\",\"Value\":\"42\"}],\"ConnectionReferences\":[]}'.")] string? deploymentSettingsJson = null,
        [Description("Language code for auto-generated deployment notes (default 'en-US')")] string languageCode = "en-US",
        [Description("If true (default), runs the full deploy (validate + commit + start). If false, only triggers validation — call pipeline_run_status to inspect.")] bool autoConfirm = true,
        [Description("Max seconds to wait for validation to complete (default 600 = 10 min). Validation can take several minutes on solutions with many components.")] int validationTimeoutSeconds = 600,
        CancellationToken ct = default)
    {
        try
        {
            _ = config.Config;
            var runId = await svc.DeployPipelineAsync(
                pipelineHostOrgUrl, solutionId, artifactName, devDeploymentEnvironmentId, targetStageId,
                languageCode, currentVersion, newVersion, deploymentNotes, deploymentSettingsJson,
                autoConfirm, validationTimeoutSeconds, pollIntervalSeconds: 10, ct);
            return JsonSerializer.Serialize(new { success = true, stageRunId = runId, artifactName, targetStageId, autoConfirm, newVersion });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "pipeline_auth_init")]
    [Description("Step 1 of Power-Apps-Maker login (two-step Device Code Flow). Returns IMMEDIATELY with the user_code and verification URL — also tries to auto-open the browser with the code pre-filled. The user must finish signing in, then call pipeline_auth_complete to persist the refresh token. Required once per device; token then cached for weeks.")]
    public static async Task<string> PipelineAuthInit(
        Dataverse.Core.Auth.DataverseTokenProvider tokenProvider,
        ConfigProvider config,
        [Description("Pipeline-Host org URL, e.g. https://orgexample.crm4.dynamics.com — used as the token resource.")] string pipelineHostOrgUrl,
        CancellationToken ct = default)
    {
        try
        {
            _ = config.Config;
            var scope = $"{pipelineHostOrgUrl.TrimEnd('/')}/.default";
            var (url, code, message, browserOpened, expiresIn) = await tokenProvider.RequestMakerDeviceCodeAsync(scope);
            return JsonSerializer.Serialize(new
            {
                step = 1,
                verificationUrl = url,
                userCode = code,
                expiresInSeconds = expiresIn,
                browserOpened,
                message,
                nextStep = "After you complete the sign-in in the browser, call pipeline_auth_complete to persist the refresh token."
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "pipeline_auth_complete")]
    [Description("Step 2 of Power-Apps-Maker login. Call AFTER you finished the browser sign-in started by pipeline_auth_init. Polls the token endpoint for up to ~60 seconds (or until the device code expires) and persists the refresh token. Then solution_deploy_pipeline runs silently for weeks.")]
    public static async Task<string> PipelineAuthComplete(
        Dataverse.Core.Auth.DataverseTokenProvider tokenProvider,
        ConfigProvider config,
        [Description("Max seconds to wait for the user-completed sign-in (default 60).")] int pollSeconds = 60,
        CancellationToken ct = default)
    {
        try
        {
            _ = config.Config;
            await tokenProvider.CompleteMakerDeviceCodeAsync(pollSeconds);
            return JsonSerializer.Serialize(new
            {
                success = true,
                message = "Refresh token cached. solution_deploy_pipeline now runs silently."
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

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
