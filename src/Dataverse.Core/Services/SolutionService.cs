namespace Dataverse.Core.Services;

using System.Text.Json;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

public sealed class SolutionService
{
    private readonly DataverseHttpClient _client;
    private readonly ILogger<SolutionService> _logger;

    public SolutionService(DataverseHttpClient client, ILogger<SolutionService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SolutionSummary>> ListAsync(
        string orgUrl,
        CancellationToken ct = default)
    {
        var url = "api/data/v9.2/solutions?$filter=isvisible eq true" +
                  "&$select=solutionid,uniquename,friendlyname,version,ismanaged,_publisherid_value" +
                  "&$expand=publisherid($select=friendlyname,uniquename)";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<SolutionSummary>();

        if (doc.RootElement.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                string? pubName = null;
                if (item.TryGetProperty("publisherid", out var pubEl) && pubEl.ValueKind == JsonValueKind.Object)
                    pubName = pubEl.GetStringOrNull("friendlyname");

                results.Add(new SolutionSummary(
                    SolutionId: item.TryGetGuid("solutionid"),
                    UniqueName: item.GetStringOrEmpty("uniquename"),
                    FriendlyName: item.GetStringOrEmpty("friendlyname"),
                    Version: item.GetStringOrEmpty("version"),
                    IsManaged: item.TryGetProperty("ismanaged", out var m) && m.ValueKind == JsonValueKind.True,
                    PublisherName: pubName));
            }
        }

        return results;
    }

    public async Task<SolutionDetail?> GetAsync(
        string orgUrl,
        string uniqueName,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/solutions?$filter=uniquename eq '{uniqueName}'" +
                  "&$select=solutionid,uniquename,friendlyname,version,ismanaged,description,installedon" +
                  "&$expand=publisherid($select=friendlyname,uniquename),solution_solutioncomponent($select=objectid,componenttype,rootcomponentbehavior)";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);

        if (!doc.RootElement.TryGetProperty("value", out var items) || items.GetArrayLength() == 0)
            return null;

        var item = items[0];
        var components = new List<SolutionComponent>();

        if (item.TryGetProperty("solution_solutioncomponent", out var comps))
        {
            foreach (var comp in comps.EnumerateArray())
            {
                components.Add(new SolutionComponent(
                    ComponentId: comp.TryGetGuid("objectid"),
                    ComponentType: comp.GetInt32OrZero("componenttype"),
                    ComponentTypeName: MapComponentType(comp.GetInt32OrZero("componenttype")),
                    RootComponentId: comp.TryGetProperty("rootcomponentbehavior", out _)
                        ? comp.TryGetGuid("rootsolutioncomponentid")
                        : null));
            }
        }

        string? detailPubName = null;
        if (item.TryGetProperty("publisherid", out var detailPubEl) && detailPubEl.ValueKind == JsonValueKind.Object)
            detailPubName = detailPubEl.GetStringOrNull("friendlyname");

        return new SolutionDetail(
            SolutionId: item.TryGetGuid("solutionid"),
            UniqueName: item.GetStringOrEmpty("uniquename"),
            FriendlyName: item.GetStringOrEmpty("friendlyname"),
            Version: item.GetStringOrEmpty("version"),
            IsManaged: item.TryGetProperty("ismanaged", out var managed) && managed.ValueKind == JsonValueKind.True,
            PublisherName: detailPubName,
            Description: item.GetStringOrNull("description"),
            InstalledOn: item.GetDateTimeOrNull("installedon"),
            Components: components);
    }

    public async Task CreateAsync(
        string orgUrl,
        string uniqueName,
        string displayName,
        string publisherUniqueName,
        string version,
        CancellationToken ct = default)
    {
        // Resolve publisher ID first
        var pubUrl = $"api/data/v9.2/publishers?$filter=uniquename eq '{publisherUniqueName}'&$select=publisherid";
        var pubRaw = await _client.GetRawAsync(orgUrl, pubUrl, ct: ct);
        var pubDoc = JsonDocument.Parse(pubRaw);
        Guid publisherId = Guid.Empty;
        if (pubDoc.RootElement.TryGetProperty("value", out var pubs) && pubs.GetArrayLength() > 0)
            publisherId = pubs[0].TryGetGuid("publisherid");

        if (publisherId == Guid.Empty)
            throw new InvalidOperationException($"Publisher '{publisherUniqueName}' not found.");

        var body = new
        {
            uniquename = uniqueName,
            friendlyname = displayName,
            version,
            publisherid = $"/publishers({publisherId})"
        };

        await _client.PostAsync(orgUrl, "api/data/v9.2/solutions", body, ct);
    }

    public async Task<SolutionExportResult> ExportAsync(
        string orgUrl,
        string uniqueName,
        bool managed,
        string? filePath = null,
        CancellationToken ct = default)
    {
        var parameters = new { SolutionName = uniqueName, Managed = managed };
        var result = await _client.ExecuteActionAsync<JsonElement?>(orgUrl, "ExportSolution", parameters, ct);

        string? base64 = null;
        if (result is JsonElement el && el.TryGetProperty("ExportSolutionFile", out var fileProp))
            base64 = fileProp.GetString();

        // When a target path is given, write the zip to disk and return the path (+ size) instead of
        // pumping the whole base64 blob back through the MCP channel — keeps large solutions practical.
        if (!string.IsNullOrWhiteSpace(filePath) && base64 is not null)
        {
            var bytes = Convert.FromBase64String(base64);
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(filePath, bytes, ct);
            return new SolutionExportResult(uniqueName, managed, FilePath: filePath, Base64Content: null, FileSizeBytes: bytes.LongLength);
        }

        return new SolutionExportResult(uniqueName, managed, FilePath: null, Base64Content: base64);
    }

    /// <summary>
    /// Import a solution asynchronously via the <c>ImportSolutionAsync</c> action, then poll the
    /// resulting <c>asyncoperation</c> until terminal and parse the <c>importjob</c> result XML.
    /// Async import avoids the HTTP timeouts the synchronous <c>ImportSolution</c> action hits on
    /// larger solutions, and surfaces per-component errors that the sync call silently swallowed.
    /// </summary>
    /// <param name="zipBase64">Base64 zip — OR provide <paramref name="filePath"/> to read from disk.</param>
    /// <param name="filePath">Path to a solution zip on disk; takes precedence over <paramref name="zipBase64"/>.</param>
    public async Task<SolutionImportResult> ImportAsync(
        string orgUrl,
        string? zipBase64,
        bool overwriteUnmanaged,
        string? filePath = null,
        int timeoutSeconds = 600,
        int pollIntervalSeconds = 5,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var bytes = await File.ReadAllBytesAsync(filePath, ct);
            zipBase64 = Convert.ToBase64String(bytes);
        }
        if (string.IsNullOrWhiteSpace(zipBase64))
            throw new ArgumentException("Either zipBase64 or filePath must be provided.");

        var importJobId = Guid.NewGuid();
        var parameters = new
        {
            CustomizationFile = zipBase64,
            OverwriteUnmanagedCustomizations = overwriteUnmanaged,
            PublishWorkflows = true,
            ImportJobId = importJobId
        };

        // ImportSolutionAsync returns the async-operation id to poll plus the import-job key.
        var resp = await _client.ExecuteActionAsync<JsonElement?>(orgUrl, "ImportSolutionAsync", parameters, ct);
        Guid asyncOperationId = Guid.Empty;
        if (resp is JsonElement el)
        {
            if (el.TryGetProperty("AsyncOperationId", out var aoEl) && Guid.TryParse(aoEl.GetString(), out var ao))
                asyncOperationId = ao;
            if (el.TryGetProperty("ImportJobKey", out var ijEl) && Guid.TryParse(ijEl.GetString(), out var ij))
                importJobId = ij; // server-confirmed key
        }
        if (asyncOperationId == Guid.Empty)
            throw new InvalidOperationException(
                $"ImportSolutionAsync did not return an AsyncOperationId. Raw: {resp?.GetRawText()}");

        // Poll the async operation until terminal (statecode 3 = Completed).
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        int statusCode = 0;
        string? statusReason = null;
        string? asyncMessage = null;
        bool completed = false;
        while (DateTime.UtcNow < deadline)
        {
            var aoUrl = $"api/data/v9.2/asyncoperations({asyncOperationId})" +
                        "?$select=statecode,statuscode,message,friendlymessage";
            var aoRaw = await _client.GetRawAsync(orgUrl, aoUrl, includeFormattedValues: true, ct: ct);
            using var aoDoc = JsonDocument.Parse(aoRaw);
            var root = aoDoc.RootElement;
            var stateCode = root.TryGetProperty("statecode", out var sc) ? sc.GetInt32() : 0;
            statusCode = root.TryGetProperty("statuscode", out var st) ? st.GetInt32() : 0;
            statusReason = root.GetStringOrNull("statuscode@OData.Community.Display.V1.FormattedValue");
            asyncMessage = root.GetStringOrNull("friendlymessage") ?? root.GetStringOrNull("message");

            if (stateCode == 3) { completed = true; break; } // Completed (terminal)
            await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), ct);
        }
        if (!completed)
            throw new TimeoutException(
                $"Solution import did not complete within {timeoutSeconds}s (asyncoperation {asyncOperationId}).");

        // statuscode 30 = Succeeded. Anything else terminal = failure.
        var success = statusCode == 30;

        // Read the import-job detail for progress + per-component errors.
        double? progress = null;
        var componentErrors = new List<string>();
        try
        {
            var ijUrl = $"api/data/v9.2/importjobs({importJobId})?$select=progress,data,completedon";
            var ijRaw = await _client.GetRawAsync(orgUrl, ijUrl, ct: ct);
            using var ijDoc = JsonDocument.Parse(ijRaw);
            if (ijDoc.RootElement.TryGetProperty("progress", out var pEl) && pEl.ValueKind == JsonValueKind.Number)
                progress = pEl.GetDouble();
            if (ijDoc.RootElement.TryGetProperty("data", out var dEl) && dEl.ValueKind == JsonValueKind.String)
                componentErrors.AddRange(ParseImportJobErrors(dEl.GetString()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read importjob {ImportJobId} detail after import.", importJobId);
        }

        return new SolutionImportResult(
            Success: success,
            AsyncOperationId: asyncOperationId,
            ImportJobId: importJobId,
            StatusReason: statusReason,
            ProgressPercent: progress,
            ErrorMessage: success ? null : asyncMessage,
            ComponentErrors: componentErrors);
    }

    /// <summary>
    /// Parse the ImportJob's <c>data</c> XML and collect any node carrying <c>result="failure"</c>,
    /// returning a short "&lt;name&gt;: &lt;errortext&gt;" line per failure for diagnostics.
    /// </summary>
    private static IReadOnlyList<string> ParseImportJobErrors(string? dataXml)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dataXml))
            return errors;
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(dataXml);
            foreach (var node in doc.Descendants())
            {
                var resultAttr = node.Attribute("result")?.Value;
                if (!string.Equals(resultAttr, "failure", StringComparison.OrdinalIgnoreCase))
                    continue;
                var errorText = node.Attribute("errortext")?.Value;
                if (string.IsNullOrWhiteSpace(errorText))
                    continue;
                var name = node.Attribute("LocalizedName")?.Value
                           ?? node.Attribute("name")?.Value
                           ?? node.Name.LocalName;
                errors.Add($"{name}: {errorText}");
            }
        }
        catch (System.Xml.XmlException)
        {
            // Malformed/partial XML — skip detail parsing, the async status remains authoritative.
        }
        return errors;
    }

    public async Task AddComponentAsync(
        string orgUrl,
        string solutionUniqueName,
        Guid componentId,
        int componentType,
        CancellationToken ct = default)
    {
        var parameters = new
        {
            ComponentId = componentId,
            ComponentType = componentType,
            SolutionUniqueName = solutionUniqueName,
            AddRequiredComponents = false
        };

        await _client.ExecuteActionAsync(orgUrl, "AddSolutionComponent", parameters, ct);
    }

    public async Task RemoveComponentAsync(
        string orgUrl,
        string solutionUniqueName,
        Guid componentId,
        int componentType,
        CancellationToken ct = default)
    {
        var parameters = new
        {
            ComponentId = componentId,
            ComponentType = componentType,
            SolutionUniqueName = solutionUniqueName
        };

        await _client.ExecuteActionAsync(orgUrl, "RemoveSolutionComponent", parameters, ct);
    }

    public async Task<SolutionLayerInfo> CheckLayersAsync(
        string orgUrl,
        Guid componentId,
        int componentType,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/msdyn_solutionhistories" +
                  $"?$filter=msdyn_solutionid ne null" +
                  $"&$select=msdyn_name,msdyn_solutionversion";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);
        var layers = new List<string>();

        if (doc.RootElement.TryGetProperty("value", out var items))
            foreach (var item in items.EnumerateArray())
            {
                var name = item.GetStringOrNull("msdyn_name");
                if (name is not null) layers.Add(name);
            }

        return new SolutionLayerInfo(componentId, componentType, layers);
    }

    public async Task RemoveActiveLayerAsync(
        string orgUrl,
        Guid componentId,
        int componentType,
        CancellationToken ct = default)
    {
        var parameters = new
        {
            ComponentId = componentId,
            ComponentType = componentType
        };

        await _client.ExecuteActionAsync(orgUrl, "RemoveActiveCustomizations", parameters, ct);
    }

    public async Task DeployPipelineAsync(
        string orgUrl,
        string pipelineId,
        string stageId,
        CancellationToken ct = default)
    {
        var body = new
        {
            StageId = stageId
        };

        await _client.PostAsync(orgUrl, $"api/data/v9.2/pipelines({pipelineId})/Deploy", body, ct);
    }

    private static string MapComponentType(int type) => type switch
    {
        1 => "Entity",
        2 => "Attribute",
        3 => "Relationship",
        9 => "OptionSet",
        14 => "ConnectionRole",
        24 => "Workflow",
        26 => "SavedQuery",
        29 => "Report",
        44 => "WebResource",
        60 => "SiteMap",
        61 => "PluginType",
        62 => "PluginAssembly",
        92 => "Role",
        _ => $"Type{type}"
    };
}
