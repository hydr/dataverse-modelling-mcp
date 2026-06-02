namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Services;
using Dataverse.Core.Workflows;
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
    [Description("Update metadata properties of an existing Dataverse table.")]
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
            await svc.UpdateAsync(env.OrgUrl, logicalName, props, ct);
            return JsonSerializer.Serialize(new { success = true, logicalName });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "column_add")]
    [Description("Add a new column (attribute) to a Dataverse table.")]
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
            await svc.AddColumnAsync(env.OrgUrl, tableLogicalName, attribute, ct);
            return JsonSerializer.Serialize(new { success = true, tableLogicalName });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "column_update")]
    [Description("Update properties of a column on a Dataverse table.")]
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
            await svc.UpdateColumnAsync(env.OrgUrl, tableLogicalName, columnLogicalName, props, ct);
            return JsonSerializer.Serialize(new { success = true, tableLogicalName, columnLogicalName });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "column_add_rollup")]
    [Description("Add a rollup column (SourceType=2) that aggregates a value over related child records. " +
                 "v1 covers the common unfiltered case over a single 1:N relationship: e.g. SUM of a child amount, " +
                 "or COUNT of children. The platform provisions the recalculation automatically. " +
                 "For Sum/Avg/Max/Min provide aggregateAttribute (a numeric child column); for Count it is optional " +
                 "(the child primary key is used).")]
    public static async Task<string> ColumnAddRollup(
        TableService svc,
        ConfigProvider config,
        [Description("Logical name of the parent table that owns the rollup column (e.g. 'account')")] string parentEntity,
        [Description("Schema name of the new rollup column (e.g. 'sample_totalrevenue')")] string schemaName,
        [Description("Display label of the new column")] string displayName,
        [Description("Numeric type of the rollup column: Integer | Decimal | Money")] string numericType,
        [Description("Logical name of the child entity to aggregate over (e.g. 'salesorder')")] string childEntity,
        [Description("Schema name of the 1:N relationship parent->child (e.g. 'opportunity_sales_orders')")] string relationshipSchemaName,
        [Description("Lookup attribute on the child pointing to the parent (e.g. 'opportunityid')")] string lookupAttribute,
        [Description("Aggregate operator: Sum | Count | Avg | Max | Min")] string aggregate,
        [Description("Numeric child column to aggregate (required for Sum/Avg/Max/Min; ignored for Count)")] string? aggregateAttribute = null,
        [Description("Child primary-key attribute used by Count (defaults to '{childEntity}id')")] string? childPrimaryKey = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!Enum.TryParse<RollupNumericType>(numericType, ignoreCase: true, out var numeric))
                return JsonSerializer.Serialize(new { error = $"Invalid numericType '{numericType}'. Use Integer, Decimal or Money." });
            if (!Enum.TryParse<RollupAggregate>(aggregate, ignoreCase: true, out var agg))
                return JsonSerializer.Serialize(new { error = $"Invalid aggregate '{aggregate}'. Use Sum, Count, Avg, Max or Min." });

            if (agg != RollupAggregate.Count && string.IsNullOrWhiteSpace(aggregateAttribute))
                return JsonSerializer.Serialize(new { error = $"aggregateAttribute is required for {agg}." });

            var rollup = new RollupDefinition(
                ChildEntity: childEntity,
                RelationshipSchemaName: relationshipSchemaName,
                LookupAttribute: lookupAttribute,
                Aggregate: agg,
                AggregateAttribute: aggregateAttribute,
                ChildPrimaryKey: childPrimaryKey);

            var env = config.GetActiveEnvironment();
            await svc.AddRollupColumnAsync(env.OrgUrl, parentEntity, schemaName, displayName, numeric, rollup, ct);
            return JsonSerializer.Serialize(new { success = true, parentEntity, schemaName });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
