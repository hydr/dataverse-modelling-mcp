namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Services;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class TableTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "table_list")]
    [Description("List Dataverse tables (entities) in the configured environment.")]
    public static async Task<string> TableList(
        TableService svc,
        ConfigProvider config,
        [Description("Optional OData filter (e.g. \"IsCustomEntity eq true\")")] string? filter = null,
        [Description("Optional solution unique name to filter by")] string? solution = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ListAsync(env.OrgUrl, filter, solution, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "table_get")]
    [Description("Get the full definition of a Dataverse table, including all columns.")]
    public static async Task<string> TableGet(
        TableService svc,
        ConfigProvider config,
        [Description("Logical name of the table (e.g. 'account')")] string logicalName,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.GetAsync(env.OrgUrl, logicalName, ct);
            return result is null
                ? JsonSerializer.Serialize(new { error = "Table not found." })
                : JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "table_create")]
    [Description("Create a new custom Dataverse table.")]
    public static async Task<string> TableCreate(
        TableService svc,
        ConfigProvider config,
        [Description("Schema name / logical name (e.g. 'new_myentity')")] string logicalName,
        [Description("Singular display name")] string displayName,
        [Description("Plural display name")] string pluralDisplayName,
        [Description("Optional description")] string? description = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            await svc.CreateAsync(env.OrgUrl, logicalName, displayName, pluralDisplayName, description, ct);
            return JsonSerializer.Serialize(new { success = true, logicalName, displayName });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "table_update")]
    [Description("Update metadata properties of an existing Dataverse table. Pass only the " +
                 "properties you want to change: the metadata endpoint rejects PATCH and only " +
                 "accepts a PUT of the complete definition, so the tool reads the current " +
                 "definition and lays your properties over it. Managed properties may be given as " +
                 "a plain true/false; note that IsValidForAdvancedFind is a plain boolean on a " +
                 "TABLE (unlike on a column).")]
    public static async Task<string> TableUpdate(
        TableService svc,
        ConfigProvider config,
        [Description("Logical name of the table")] string logicalName,
        [Description("JSON object with properties to update")] string propertiesJson,
        CancellationToken ct = default)
    {
        try
        {
            var props = JsonSerializer.Deserialize<Dictionary<string, object?>>(propertiesJson)
                        ?? throw new ArgumentException("propertiesJson could not be parsed.");
            var env = config.GetActiveEnvironment();
            var normalized = await svc.UpdateAsync(env.OrgUrl, logicalName, props, ct);
            return JsonSerializer.Serialize(new
            {
                success = true,
                logicalName,
                normalizedManagedProperties = normalized.Count > 0 ? normalized : null
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "column_add")]
    [Description("Add a new column (attribute) to a Dataverse table. Managed properties " +
                 "(IsValidForAdvancedFind, IsAuditEnabled, IsCustomizable, IsRenameable, " +
                 "CanModifyAdditionalSettings, IsGlobalFilterEnabled, IsSortableEnabled, " +
                 "RequiredLevel) may be given as a plain true/false or string — they are rewritten " +
                 "into the BooleanManagedProperty object the metadata endpoint requires, and the " +
                 "result lists what was rewritten under normalizedManagedProperties.")]
    public static async Task<string> ColumnAdd(
        TableService svc,
        ConfigProvider config,
        [Description("Logical name of the table")] string tableLogicalName,
        [Description("Full attribute definition as JSON (must include @odata.type, LogicalName, DisplayName, etc.)")] string attributeJson,
        CancellationToken ct = default)
    {
        try
        {
            var attribute = JsonSerializer.Deserialize<Dictionary<string, object?>>(attributeJson)
                            ?? throw new ArgumentException("attributeJson could not be parsed.");
            var env = config.GetActiveEnvironment();
            var normalized = await svc.AddColumnAsync(env.OrgUrl, tableLogicalName, attribute, ct);
            return JsonSerializer.Serialize(new
            {
                success = true,
                tableLogicalName,
                normalizedManagedProperties = normalized.Count > 0 ? normalized : null
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "column_update")]
    [Description("Update properties of a column on a Dataverse table. Pass only the properties you " +
                 "want to change: the metadata endpoint rejects PATCH and only accepts a PUT of the " +
                 "complete definition, so the tool reads the current definition and lays your " +
                 "properties over it. The overlay is shallow — a whole object you pass (e.g. " +
                 "DisplayName) replaces the old one. Managed properties may be given as a plain " +
                 "true/false or string and are reported under normalizedManagedProperties.")]
    public static async Task<string> ColumnUpdate(
        TableService svc,
        ConfigProvider config,
        [Description("Logical name of the table")] string tableLogicalName,
        [Description("Logical name of the column")] string columnLogicalName,
        [Description("JSON object with properties to update")] string propertiesJson,
        CancellationToken ct = default)
    {
        try
        {
            var props = JsonSerializer.Deserialize<Dictionary<string, object?>>(propertiesJson)
                        ?? throw new ArgumentException("propertiesJson could not be parsed.");
            var env = config.GetActiveEnvironment();
            var normalized = await svc.UpdateColumnAsync(
                env.OrgUrl, tableLogicalName, columnLogicalName, props, ct);
            return JsonSerializer.Serialize(new
            {
                success = true,
                tableLogicalName,
                columnLogicalName,
                normalizedManagedProperties = normalized.Count > 0 ? normalized : null
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
