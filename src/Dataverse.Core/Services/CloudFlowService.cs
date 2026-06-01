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

    /// <summary>
    /// Triggers a flow run via the manual-trigger endpoint and returns the newly created RunId
    /// (or empty if it cannot be resolved). For Recurrence-triggered flows, pass triggerName="Recurrence".
    /// </summary>
    public async Task<string> TriggerRunAsync(
        string region,
        string environmentId,
        string flowId,
        string triggerName = "Recurrence",
        object? triggerBody = null,
        CancellationToken ct = default)
    {
        var url = $"{PowerAutomateHttpClient.FlowsUrl(region, environmentId)}/{flowId}/triggers/{triggerName}/run?api-version=2016-11-01";
        await _client.PostAsync(url, triggerBody ?? new { }, ct);

        var runsUrl = $"{PowerAutomateHttpClient.FlowsUrl(region, environmentId)}/{flowId}/runs?api-version=2016-11-01&$top=1";
        var runsRaw = await _client.GetRawAsync(runsUrl, ct);
        var runs = JsonDocument.Parse(runsRaw).RootElement;
        if (!runs.TryGetProperty("value", out var arr) || arr.GetArrayLength() == 0)
            return string.Empty;
        return arr[0].GetStringOrNull("name") ?? string.Empty;
    }

    /// <summary>
    /// Get a single run by id without using $expand=properties/actions (which intermittently
    /// returns HTML runtime errors from the PA backend on long-running or large flows).
    /// </summary>
    public async Task<FlowRun?> GetRunAsync(
        string region,
        string environmentId,
        string flowId,
        string runId,
        CancellationToken ct = default)
    {
        var url = $"{PowerAutomateHttpClient.FlowsUrl(region, environmentId)}/{flowId}/runs/{runId}?api-version=2016-11-01";
        var raw = await _client.GetRawAsync(url, ct);
        var item = JsonDocument.Parse(raw).RootElement;
        var props = item.TryGetProperty("properties", out var p) ? p : item;
        string? errorCode = null, errorMsg = null;
        if (props.TryGetProperty("error", out var errEl))
        {
            errorCode = errEl.GetStringOrNull("code");
            errorMsg = errEl.GetStringOrNull("message");
        }
        return new FlowRun(
            RunId: item.GetStringOrNull("name") ?? runId,
            Status: props.GetStringOrNull("status") ?? "Unknown",
            StartTime: props.GetDateTimeOrNull("startTime"),
            EndTime: props.GetDateTimeOrNull("endTime"),
            TriggerName: props.GetStringOrNull("triggerName"),
            ErrorCode: errorCode,
            ErrorMessage: errorMsg);
    }

    /// <summary>
    /// List all actions of a single run with status, code, error, and outputs-link. Uses the
    /// per-run actions endpoint that works even when $expand on the run resource fails.
    /// </summary>
    public async Task<IReadOnlyList<RunActionSummary>> GetRunActionsAsync(
        string region,
        string environmentId,
        string flowId,
        string runId,
        CancellationToken ct = default)
    {
        var url = $"{PowerAutomateHttpClient.FlowsUrl(region, environmentId)}/{flowId}/runs/{runId}/actions?api-version=2016-11-01";
        var raw = await _client.GetRawAsync(url, ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<RunActionSummary>();
        if (!doc.RootElement.TryGetProperty("value", out var items))
            return results;

        foreach (var item in items.EnumerateArray())
        {
            var props = item.TryGetProperty("properties", out var p) ? p : item;
            string? errorCode = null, errorMsg = null;
            if (props.TryGetProperty("error", out var errEl))
            {
                errorCode = errEl.GetStringOrNull("code");
                errorMsg = errEl.GetStringOrNull("message");
            }
            string? outputsLink = null;
            if (props.TryGetProperty("outputsLink", out var ol))
                outputsLink = ol.GetStringOrNull("uri");

            results.Add(new RunActionSummary(
                Name: item.GetStringOrNull("name") ?? string.Empty,
                Status: props.GetStringOrNull("status") ?? "Unknown",
                Code: props.GetStringOrNull("code"),
                StartTime: props.GetDateTimeOrNull("startTime"),
                EndTime: props.GetDateTimeOrNull("endTime"),
                ErrorCode: errorCode,
                ErrorMessage: errorMsg,
                OutputsLink: outputsLink));
        }
        return results;
    }

    /// <summary>
    /// Fetch the JSON outputs of a single action in a run (resolves the SAS-signed outputsLink).
    /// The link is downloaded WITHOUT the Bearer token (it's pre-signed; adding a Bearer header
    /// triggers DirectApiRequestHasMoreThanOneAuthorization).
    /// </summary>
    public async Task<string> GetActionOutputsAsync(
        string region,
        string environmentId,
        string flowId,
        string runId,
        string actionName,
        CancellationToken ct = default)
    {
        var url = $"{PowerAutomateHttpClient.FlowsUrl(region, environmentId)}/{flowId}/runs/{runId}/actions/{actionName}?api-version=2016-11-01";
        var raw = await _client.GetRawAsync(url, ct);
        var props = JsonDocument.Parse(raw).RootElement.GetProperty("properties");
        if (!props.TryGetProperty("outputsLink", out var ol))
            return string.Empty;
        var uri = ol.GetStringOrNull("uri");
        if (string.IsNullOrEmpty(uri))
            return string.Empty;

        using var fresh = new HttpClient();
        return await fresh.GetStringAsync(uri, ct);
    }

    /// <summary>
    /// Poll the run until it reaches a terminal state (anything other than "Running") or until the
    /// timeout elapses. Defaults to 5-minute deadline, 10-second poll interval. Throws on timeout.
    /// </summary>
    public async Task<FlowRun> WaitForRunAsync(
        string region,
        string environmentId,
        string flowId,
        string runId,
        int timeoutSeconds = 300,
        int pollIntervalSeconds = 10,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        FlowRun? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await GetRunAsync(region, environmentId, flowId, runId, ct);
            if (last is null)
                throw new InvalidOperationException($"Run '{runId}' not found.");
            if (!string.Equals(last.Status, "Running", StringComparison.OrdinalIgnoreCase))
                return last;
            await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), ct);
        }
        throw new TimeoutException(
            $"Run '{runId}' did not reach a terminal state within {timeoutSeconds}s. Last status: {last?.Status ?? "unknown"}.");
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
