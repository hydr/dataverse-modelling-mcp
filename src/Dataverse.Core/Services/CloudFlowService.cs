namespace Dataverse.Core.Services;

using System.Text;
using System.Text.Json;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

public sealed class CloudFlowService
{
    private readonly PowerAutomateHttpClient _client;
    private readonly ILogger<CloudFlowService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public CloudFlowService(PowerAutomateHttpClient client, ILogger<CloudFlowService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FlowSummary>> ListAsync(
        string region,
        string environmentId,
        string? filter = null,
        int top = 50,
        CancellationToken ct = default)
    {
        var url = $"{PowerAutomateHttpClient.FlowsUrl(region, environmentId)}?api-version=2016-11-01&$top={top}";
        if (!string.IsNullOrWhiteSpace(filter))
            url += $"&$filter={Uri.EscapeDataString(filter)}";

        var raw = await _client.GetRawAsync(url, ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<FlowSummary>();

        if (doc.RootElement.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var props = item.TryGetProperty("properties", out var p) ? p : item;
                results.Add(new FlowSummary(
                    FlowId: item.GetStringOrNull("name") ?? string.Empty,
                    DisplayName: props.GetStringOrNull("displayName") ?? string.Empty,
                    State: props.GetStringOrNull("state") ?? "Unknown",
                    CreatedTime: props.GetDateTimeOrNull("createdTime"),
                    LastModifiedTime: props.GetDateTimeOrNull("lastModifiedTime"),
                    TriggerType: ExtractTriggerType(props)));
            }
        }

        return results;
    }

    public async Task<FlowDetail?> GetAsync(
        string region,
        string environmentId,
        string flowId,
        CancellationToken ct = default)
    {
        var url = $"{PowerAutomateHttpClient.FlowsUrl(region, environmentId)}/{flowId}?api-version=2016-11-01";
        var raw = await _client.GetRawAsync(url, ct);
        var item = JsonDocument.Parse(raw).RootElement;
        var props = item.TryGetProperty("properties", out var p) ? p : item;

        object? definition = null;
        if (props.TryGetProperty("definition", out var defEl))
            definition = defEl;

        return new FlowDetail(
            FlowId: item.GetStringOrNull("name") ?? flowId,
            DisplayName: props.GetStringOrNull("displayName") ?? string.Empty,
            State: props.GetStringOrNull("state") ?? "Unknown",
            CreatedTime: props.GetDateTimeOrNull("createdTime"),
            LastModifiedTime: props.GetDateTimeOrNull("lastModifiedTime"),
            TriggerType: ExtractTriggerType(props),
            Definition: definition);
    }

    public async Task<string> CreateAsync(
        string region,
        string environmentId,
        string displayName,
        JsonElement definition,
        CancellationToken ct = default)
    {
        var url = $"{PowerAutomateHttpClient.FlowsUrl(region, environmentId)}?api-version=2016-11-01";
        var body = new
        {
            properties = new
            {
                displayName,
                definition
            }
        };

        var raw = await _client.PostAsync<JsonElement?>(url, body, ct);
        if (raw is JsonElement el && el.TryGetProperty("name", out var nameProp))
            return nameProp.GetString() ?? string.Empty;
        return string.Empty;
    }

    public async Task UpdateAsync(
        string region,
        string environmentId,
        string flowId,
        JsonElement definition,
        CancellationToken ct = default)
    {
        var url = $"{PowerAutomateHttpClient.FlowsUrl(region, environmentId)}/{flowId}?api-version=2016-11-01";
        var body = new { properties = new { definition } };
        await _client.PatchAsync<JsonElement?>(url, body, ct);
    }

    public async Task SetStateAsync(
        string region,
        string environmentId,
        string flowId,
        bool enable,
        CancellationToken ct = default)
    {
        var action = enable ? "start" : "stop";
        var url = $"{PowerAutomateHttpClient.FlowsUrl(region, environmentId)}/{flowId}/{action}?api-version=2016-11-01";
        await _client.PostAsync(url, null, ct);
    }

    public async Task<IReadOnlyList<FlowRun>> GetRunsAsync(
        string region,
        string environmentId,
        string flowId,
        int top = 25,
        string? filter = null,
        CancellationToken ct = default)
    {
        var url = $"{PowerAutomateHttpClient.FlowsUrl(region, environmentId)}/{flowId}/runs?api-version=2016-11-01&$top={top}";
        if (!string.IsNullOrWhiteSpace(filter))
            url += $"&$filter={Uri.EscapeDataString(filter)}";

        var raw = await _client.GetRawAsync(url, ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<FlowRun>();

        if (doc.RootElement.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var props = item.TryGetProperty("properties", out var p) ? p : item;
                string? errorCode = null;
                string? errorMsg = null;
                if (props.TryGetProperty("error", out var errEl))
                {
                    errorCode = errEl.GetStringOrNull("code");
                    errorMsg = errEl.GetStringOrNull("message");
                }

                results.Add(new FlowRun(
                    RunId: item.GetStringOrNull("name") ?? string.Empty,
                    Status: props.GetStringOrNull("status") ?? "Unknown",
                    StartTime: props.GetDateTimeOrNull("startTime"),
                    EndTime: props.GetDateTimeOrNull("endTime"),
                    TriggerName: props.GetStringOrNull("triggerName"),
                    ErrorCode: errorCode,
                    ErrorMessage: errorMsg));
            }
        }

        return results;
    }

    public async Task<FlowDescription> DescribeAsync(
        string region,
        string environmentId,
        string flowId,
        CancellationToken ct = default)
    {
        var detail = await GetAsync(region, environmentId, flowId, ct);
        if (detail is null)
            return new FlowDescription(flowId, "Unknown", "Unknown trigger", [], "Flow not found.");

        var actionSummaries = new List<string>();
        var triggerSummary = detail.TriggerType ?? "Unknown trigger";

        if (detail.Definition is JsonElement defEl)
        {
            if (defEl.TryGetProperty("triggers", out var triggers))
            {
                foreach (var trig in triggers.EnumerateObject())
                {
                    var kind = trig.Value.GetStringOrNull("type") ?? "unknown";
                    triggerSummary = $"{trig.Name} ({kind})";
                    break;
                }
            }

            if (defEl.TryGetProperty("actions", out var actions))
            {
                foreach (var action in actions.EnumerateObject())
                {
                    var kind = action.Value.GetStringOrNull("type") ?? "unknown";
                    actionSummaries.Add($"{action.Name} ({kind})");
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Flow: {detail.DisplayName} (ID: {flowId})");
        sb.AppendLine($"State: {detail.State}");
        sb.AppendLine($"Trigger: {triggerSummary}");
        sb.AppendLine($"Actions ({actionSummaries.Count}):");
        foreach (var a in actionSummaries)
            sb.AppendLine($"  - {a}");

        return new FlowDescription(
            FlowId: flowId,
            DisplayName: detail.DisplayName,
            TriggerSummary: triggerSummary,
            ActionSummaries: actionSummaries,
            FullDescription: sb.ToString());
    }

    private static string? ExtractTriggerType(JsonElement props)
    {
        if (props.TryGetProperty("definition", out var def) &&
            def.TryGetProperty("triggers", out var triggers))
        {
            foreach (var t in triggers.EnumerateObject())
                return t.Value.GetStringOrNull("type");
        }
        return null;
    }
}
