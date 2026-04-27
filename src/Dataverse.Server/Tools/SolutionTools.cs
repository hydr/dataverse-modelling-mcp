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

    [McpServerTool(Name = "solution_deploy_pipeline")]
    [Description("Trigger a Power Platform Pipeline deployment to a stage.")]
    public static async Task<string> SolutionDeployPipeline(
        SolutionService svc,
        ConfigProvider config,
        [Description("Pipeline GUID")] string pipelineId,
        [Description("Target stage GUID")] string stageId,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            await svc.DeployPipelineAsync(env.OrgUrl, pipelineId, stageId, ct);
            return JsonSerializer.Serialize(new { success = true, pipelineId, stageId });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
