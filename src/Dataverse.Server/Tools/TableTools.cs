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

    /// <summary>
    /// Publish the table when asked, and describe the outcome for the result payload.
    /// </summary>
    /// <remarks>
    /// A metadata write is not visible to clients until the table is published. Reading it back is a
    /// different matter — <c>EntityDefinitions</c> is eventually consistent and catches up on its own
    /// within seconds — so publishing is genuinely optional here and stays opt-in rather than turning
    /// every column change into a minutes-long operation on a large org. What is not optional is
    /// saying so, hence the note on every write that did not publish.
    /// </remarks>
    private static async Task<(bool Published, string? Note)> PublishIfRequestedAsync(
        PublishService publishSvc,
        string orgUrl,
        string tableLogicalName,
        bool publish,
        CancellationToken ct)
    {
        if (!publish)
        {
            return (false, $"Not published. Clients keep seeing the old definition until the table is "
                           + $"published: publish_customizations(entities='{tableLogicalName}'), or pass "
                           + "publish=true here. A read-back may briefly show the old value for a few "
                           + "seconds regardless — that is eventual consistency, not a failed write.");
        }

        await publishSvc.PublishAsync(orgUrl, entities: [tableLogicalName], ct: ct);
        return (true, null);
    }

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
    [Description("Create a new custom Dataverse table. The table is not visible to clients until it " +
                 "is published — pass publish=true, or run publish_customizations afterwards.")]
    public static async Task<string> TableCreate(
        TableService svc,
        PublishService publishSvc,
        ConfigProvider config,
        [Description("Schema name / logical name (e.g. 'new_myentity')")] string logicalName,
        [Description("Singular display name")] string displayName,
        [Description("Plural display name")] string pluralDisplayName,
        [Description("Optional description")] string? description = null,
        [Description("true to publish the table right away (default false)")] bool publish = false,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            await svc.CreateAsync(env.OrgUrl, logicalName, displayName, pluralDisplayName, description, ct);
            var (published, note) = await PublishIfRequestedAsync(
                publishSvc, env.OrgUrl, logicalName, publish, ct);

            return JsonSerializer.Serialize(new
            {
                success = true,
                logicalName,
                displayName,
                published,
                publishNote = note
            }, JsonOptions);
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
        PublishService publishSvc,
        ConfigProvider config,
        [Description("Logical name of the table")] string logicalName,
        [Description("JSON object with properties to update")] string propertiesJson,
        [Description("true to publish the table right away (default false)")] bool publish = false,
        CancellationToken ct = default)
    {
        try
        {
            var props = JsonSerializer.Deserialize<Dictionary<string, object?>>(propertiesJson)
                        ?? throw new ArgumentException("propertiesJson could not be parsed.");
            var env = config.GetActiveEnvironment();
            var normalized = await svc.UpdateAsync(env.OrgUrl, logicalName, props, ct);
            var (published, note) = await PublishIfRequestedAsync(
                publishSvc, env.OrgUrl, logicalName, publish, ct);

            return JsonSerializer.Serialize(new
            {
                success = true,
                logicalName,
                normalizedManagedProperties = normalized.Count > 0 ? normalized : null,
                published,
                publishNote = note
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
        PublishService publishSvc,
        ConfigProvider config,
        [Description("Logical name of the table")] string tableLogicalName,
        [Description("Full attribute definition as JSON (must include @odata.type, LogicalName, DisplayName, etc.)")] string attributeJson,
        [Description("true to publish the table right away (default false). A PCF control bound to a brand-new column needs the column published first.")] bool publish = false,
        CancellationToken ct = default)
    {
        try
        {
            var attribute = JsonSerializer.Deserialize<Dictionary<string, object?>>(attributeJson)
                            ?? throw new ArgumentException("attributeJson could not be parsed.");
            var env = config.GetActiveEnvironment();
            var normalized = await svc.AddColumnAsync(env.OrgUrl, tableLogicalName, attribute, ct);
            var (published, note) = await PublishIfRequestedAsync(
                publishSvc, env.OrgUrl, tableLogicalName, publish, ct);

            return JsonSerializer.Serialize(new
            {
                success = true,
                tableLogicalName,
                normalizedManagedProperties = normalized.Count > 0 ? normalized : null,
                published,
                publishNote = note
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "column_delete")]
    [Description("Delete a column from a Dataverse table. IRREVERSIBLE — it takes the stored data " +
                 "with it. By default the column's dependencies are checked first and the delete is " +
                 "refused if anything (a form, a view, a workflow) still uses it; the blockers come " +
                 "back in the result. Pass force=true to delete anyway. Set publish=true to publish " +
                 "the table afterwards, otherwise clients keep seeing the old definition.")]
    public static async Task<string> ColumnDelete(
        TableService svc,
        ComponentDependencyService dependencies,
        PublishService publishSvc,
        ConfigProvider config,
        [Description("Logical name of the table")] string tableLogicalName,
        [Description("Logical name of the column to delete")] string columnLogicalName,
        [Description("true to delete even when other components depend on the column (default false)")] bool force = false,
        [Description("true to publish the table after the delete (default false)")] bool publish = false,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var metadataId = await svc.GetColumnMetadataIdAsync(
                env.OrgUrl, tableLogicalName, columnLogicalName, ct);

            var report = await dependencies.GetDependenciesForDeleteAsync(env.OrgUrl, metadataId, 2, ct);

            if (!report.CanDelete && !force)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    deleted = false,
                    tableLogicalName,
                    columnLogicalName,
                    metadataId,
                    reason = "Other components depend on this column. Review them and pass force=true "
                             + "to delete anyway.",
                    dependencies = report
                }, JsonOptions);
            }

            await svc.DeleteColumnAsync(env.OrgUrl, tableLogicalName, columnLogicalName, ct);
            var (published, note) = await PublishIfRequestedAsync(
                publishSvc, env.OrgUrl, tableLogicalName, publish, ct);

            return JsonSerializer.Serialize(new
            {
                success = true,
                deleted = true,
                tableLogicalName,
                columnLogicalName,
                metadataId,
                forcedOverDependencies = !report.CanDelete,
                dependentCount = report.DependentCount,
                published,
                publishNote = note,
                readBackNote = "A delete can still show the column for a few seconds — "
                               + "EntityDefinitions is eventually consistent (measured at ~16s)."
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
        PublishService publishSvc,
        ConfigProvider config,
        [Description("Logical name of the table")] string tableLogicalName,
        [Description("Logical name of the column")] string columnLogicalName,
        [Description("JSON object with properties to update")] string propertiesJson,
        [Description("true to publish the table right away (default false)")] bool publish = false,
        CancellationToken ct = default)
    {
        try
        {
            var props = JsonSerializer.Deserialize<Dictionary<string, object?>>(propertiesJson)
                        ?? throw new ArgumentException("propertiesJson could not be parsed.");
            var env = config.GetActiveEnvironment();
            var normalized = await svc.UpdateColumnAsync(
                env.OrgUrl, tableLogicalName, columnLogicalName, props, ct);
            var (published, note) = await PublishIfRequestedAsync(
                publishSvc, env.OrgUrl, tableLogicalName, publish, ct);

            return JsonSerializer.Serialize(new
            {
                success = true,
                tableLogicalName,
                columnLogicalName,
                normalizedManagedProperties = normalized.Count > 0 ? normalized : null,
                published,
                publishNote = note
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
