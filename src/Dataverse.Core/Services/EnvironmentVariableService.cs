namespace Dataverse.Core.Services;

using System.Text.Json;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

public sealed class EnvironmentVariableService
{
    private readonly DataverseHttpClient _client;
    private readonly ILogger<EnvironmentVariableService> _logger;

    public EnvironmentVariableService(DataverseHttpClient client, ILogger<EnvironmentVariableService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EnvironmentVariableSummary>> ListAsync(
        string orgUrl,
        CancellationToken ct = default)
    {
        var url = "api/data/v9.2/environmentvariabledefinitions" +
                  "?$select=environmentvariabledefinitionid,schemaname,displayname,type,defaultvalue" +
                  "&$expand=environmentvariabledefinition_environmentvariablevalue($select=value)";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<EnvironmentVariableSummary>();

        if (doc.RootElement.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                string? currentValue = null;
                if (item.TryGetProperty("environmentvariabledefinition_environmentvariablevalue", out var vals) &&
                    vals.GetArrayLength() > 0)
                    currentValue = vals[0].GetStringOrNull("value");

                results.Add(new EnvironmentVariableSummary(
                    DefinitionId: item.TryGetGuid("environmentvariabledefinitionid"),
                    SchemaName: item.GetStringOrEmpty("schemaname"),
                    DisplayName: item.GetStringOrNull("displayname"),
                    VariableType: item.GetStringOrNull("type") ?? "String",
                    DefaultValue: item.GetStringOrNull("defaultvalue"),
                    CurrentValue: currentValue));
            }
        }

        return results;
    }

    public async Task<EnvironmentVariableDetail?> GetAsync(
        string orgUrl,
        string schemaName,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/environmentvariabledefinitions" +
                  $"?$filter=schemaname eq '{schemaName}'" +
                  "&$select=environmentvariabledefinitionid,schemaname,displayname,type,defaultvalue,description" +
                  "&$expand=environmentvariabledefinition_environmentvariablevalue($select=environmentvariablevalueid,value)";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);

        if (!doc.RootElement.TryGetProperty("value", out var items) || items.GetArrayLength() == 0)
            return null;

        var item = items[0];
        string? currentValue = null;
        Guid? valueId = null;

        if (item.TryGetProperty("environmentvariabledefinition_environmentvariablevalue", out var vals) &&
            vals.GetArrayLength() > 0)
        {
            var valItem = vals[0];
            currentValue = valItem.GetStringOrNull("value");
            valueId = valItem.TryGetGuid("environmentvariablevalueid");
            if (valueId == Guid.Empty) valueId = null;
        }

        return new EnvironmentVariableDetail(
            DefinitionId: item.TryGetGuid("environmentvariabledefinitionid"),
            SchemaName: item.GetStringOrEmpty("schemaname"),
            DisplayName: item.GetStringOrNull("displayname"),
            VariableType: item.GetStringOrNull("type") ?? "String",
            DefaultValue: item.GetStringOrNull("defaultvalue"),
            CurrentValue: currentValue,
            Description: item.GetStringOrNull("description"),
            ValueId: valueId);
    }

    public async Task SetAsync(
        string orgUrl,
        string schemaName,
        string value,
        CancellationToken ct = default)
    {
        var detail = await GetAsync(orgUrl, schemaName, ct)
                     ?? throw new InvalidOperationException($"Environment variable '{schemaName}' not found.");

        if (detail.ValueId.HasValue)
        {
            var body = new { value };
            await _client.PatchAsync(
                orgUrl,
                $"api/data/v9.2/environmentvariablevalues({detail.ValueId.Value})",
                body,
                ct);
        }
        else
        {
            // @odata.bind notation requires dictionary keys (camelCase policy does not apply to dict keys)
            var body = new Dictionary<string, object?>
            {
                ["value"] = value,
                ["EnvironmentVariableDefinitionId@odata.bind"] = $"/environmentvariabledefinitions({detail.DefinitionId})"
            };
            await _client.PostAsync(orgUrl, "api/data/v9.2/environmentvariablevalues", body, ct);
        }
    }
}
