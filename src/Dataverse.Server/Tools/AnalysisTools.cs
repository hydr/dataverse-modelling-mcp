namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Services;
using ModelContextProtocol.Server;

/// <summary>
/// Read-only questions you should be able to answer before changing or deleting something.
/// </summary>
[McpServerToolType]
public sealed class AnalysisTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "component_dependencies")]
    [Description("List everything that depends on a component — ask this before deleting anything. " +
                 "Wraps RetrieveDependenciesForDelete and resolves the GUIDs and type codes it " +
                 "returns into names, so the answer is readable. canDelete=true means nothing in " +
                 "the environment requires the component. objectId is the component's own id: for a " +
                 "table or column its MetadataId, the same id solution_get reports as componentId.")]
    public static async Task<string> ComponentDependencies(
        ComponentDependencyService svc,
        ConfigProvider config,
        [Description("objectId of the component — for a table or column its MetadataId")] string objectId,
        [Description("Component type code (1=Entity, 2=Attribute, 26=SavedQuery, 29=Workflow, 60=SystemForm, 61=WebResource, 66=CustomControl, 91=PluginAssembly)")] int componentType,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(objectId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid objectId GUID format." });

            var env = config.GetActiveEnvironment();
            var result = await svc.GetDependenciesForDeleteAsync(env.OrgUrl, id, componentType, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "entity_solution_map")]
    [Description("Show which solutions contain a table, with each one's rootcomponentbehavior and " +
                 "the subcomponents it holds explicitly. Answers \"which solution carries this " +
                 "change?\" — the question hangs entirely on rootcomponentbehavior (0 = include " +
                 "subcomponents, so forms and columns travel with the table automatically; 1 = do " +
                 "not include; 2 = shell only), and the same table routinely sits in several " +
                 "solutions with different values. Subcomponents are found through their " +
                 "rootsolutioncomponentid back-reference, so the assignment is the platform's own, " +
                 "not a guess.")]
    public static async Task<string> EntitySolutionMap(
        EntitySolutionMapService svc,
        ConfigProvider config,
        [Description("Logical name of the table (e.g. 'salesorder')")] string logicalName,
        [Description("Resolve subcomponent GUIDs to names (default true)")] bool resolveComponentNames = true,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.GetAsync(env.OrgUrl, logicalName, resolveComponentNames, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
