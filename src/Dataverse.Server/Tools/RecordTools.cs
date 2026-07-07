namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Clients;
using Dataverse.Core.Config;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class RecordTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "record_upsert")]
    [Description(
        "Create-or-update (upsert) a single Dataverse row via PATCH {entitySet}({recordId}) WITHOUT an " +
        "If-Match header, so a row with a CLIENT-SPECIFIED GUID is created if it does not exist yet " +
        "(and updated if it does). Use this to seed reference data with a fixed primary key across " +
        "environments — something update_record (If-Match:* = update-only) cannot do. " +
        "values is a JSON object of column -> value; lookups use the @odata.bind form, e.g. " +
        "{\"sample_name\":\"X\",\"sample_verknpftequelle@odata.bind\":\"/sample_sources(<guid>)\"}. Do NOT put the " +
        "primary key in values (it belongs in recordId). Targets orgUrl when given, otherwise the active " +
        "environment. WARNING: this writes data — when targeting staging or production, confirm first.")]
    public static async Task<string> RecordUpsert(
        DataverseHttpClient http,
        ConfigProvider config,
        [Description("Entity set name (plural), e.g. 'sample_sourcedetails'")] string entitySet,
        [Description("GUID to create/update as the primary key of the row")] string recordId,
        [Description("JSON object of column -> value (incl. @odata.bind lookups). Exclude the primary key.")] string valuesJson,
        [Description("Target org URL, e.g. https://contoso-staging.crm4.dynamics.com. Defaults to the active environment's org URL.")]
            string? orgUrl = null,
        CancellationToken ct = default)
    {
        try
        {
            var targetOrg = string.IsNullOrWhiteSpace(orgUrl)
                ? config.GetActiveEnvironment().OrgUrl
                : orgUrl!;

            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(valuesJson)
                ?? throw new InvalidOperationException("values must be a JSON object.");

            if (values.Count == 0)
                throw new InvalidOperationException("values must contain at least one column.");

            var relativeUrl = $"api/data/v9.2/{entitySet}({recordId})";
            await http.PatchAsync(targetOrg, relativeUrl, values, ct);

            return JsonSerializer.Serialize(
                new { success = true, org = targetOrg, entitySet, recordId },
                JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
