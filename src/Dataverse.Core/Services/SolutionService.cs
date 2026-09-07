namespace Dataverse.Core.Services;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dataverse.Core.Auth;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

public sealed class SolutionService
{
    private readonly DataverseHttpClient _client;
    private readonly DataverseTokenProvider? _tokenProvider;
    private readonly ILogger<SolutionService> _logger;

    public SolutionService(
        DataverseHttpClient client,
        ILogger<SolutionService> logger,
        DataverseTokenProvider? tokenProvider = null)
    {
        _client = client;
        _logger = logger;
        _tokenProvider = tokenProvider;
    }

    private static readonly HttpClient s_pipelineHttp = new();

    /// <summary>
    /// Send a Pipeline-Backend mutating call (POST/PATCH) with a Power-Apps-Maker AppId token.
    /// The Az-CLI / custom-app tokens get filtered out by the Pipeline-Backend validation workflow.
    /// </summary>
    private async Task<string> SendPipelineAsync(
        string method,
        string pipelineHostOrgUrl,
        string relativeUrl,
        object? body,
        bool requestRepresentation,
        CancellationToken ct)
    {
        if (_tokenProvider is null)
            throw new InvalidOperationException(
                "Pipeline mutating calls require DataverseTokenProvider — DI registration missing.");

        var scope = $"{pipelineHostOrgUrl.TrimEnd('/')}/.default";
        var token = await _tokenProvider.GetMakerUiTokenAsync(scope, ct);

        var uri = new Uri($"{pipelineHostOrgUrl.TrimEnd('/')}/{relativeUrl.TrimStart('/')}");
        var req = new HttpRequestMessage(new HttpMethod(method), uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("OData-MaxVersion", "4.0");
        req.Headers.Add("OData-Version", "4.0");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (requestRepresentation)
            req.Headers.TryAddWithoutValidation("Prefer", "return=representation");
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await s_pipelineHttp.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Pipeline {method} {relativeUrl} → {(int)resp.StatusCode}: {err}", null, resp.StatusCode);
        }
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            return string.Empty;
        return await resp.Content.ReadAsStringAsync(ct);
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

    /// <summary>
    /// Read a solution with its complete component list.
    /// </summary>
    /// <param name="resolveComponentNames">
    /// Resolve each component's GUID to a name (one bulk lookup per component type). Worth the extra
    /// requests for a readable answer; pass false when only the ids matter.
    /// </param>
    public async Task<SolutionDetail?> GetAsync(
        string orgUrl,
        string uniqueName,
        bool resolveComponentNames = true,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/solutions?$filter=uniquename eq '{uniqueName}'" +
                  "&$select=solutionid,uniquename,friendlyname,version,ismanaged,description,installedon" +
                  "&$expand=publisherid($select=friendlyname,uniquename)";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);

        if (!doc.RootElement.TryGetProperty("value", out var items) || items.GetArrayLength() == 0)
            return null;

        var item = items[0];
        var solutionId = item.TryGetGuid("solutionid");
        var components = await ReadComponentsAsync(orgUrl, solutionId, ct);

        // Solution-Component-Framework types (>= 1000) carry environment-specific codes and are not part
        // of the documented componenttype choice — resolve those names once from the metadata instead of
        // guessing. Everything else keeps the static (documented) mapping.
        if (components.Any(c => c.ComponentType >= 1000 && c.ComponentTypeName == $"Type{c.ComponentType}"))
        {
            var frameworkTypes = await TryResolveFrameworkComponentTypesAsync(orgUrl, ct);
            if (frameworkTypes.Count > 0)
            {
                for (var i = 0; i < components.Count; i++)
                {
                    var c = components[i];
                    if (c.ComponentTypeName == $"Type{c.ComponentType}"
                        && frameworkTypes.TryGetValue(c.ComponentType, out var resolvedName))
                    {
                        components[i] = c with { ComponentTypeName = resolvedName };
                    }
                }
            }
        }

        IReadOnlyList<SolutionComponent> resolved = components;
        if (resolveComponentNames && components.Count > 0)
        {
            resolved = await new SolutionComponentNameResolver(_client, _logger)
                .ResolveAsync(orgUrl, components, ct);
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
            ComponentCount: resolved.Count,
            Components: resolved);
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
            throw new InvalidOperationException(
                $"Publisher '{publisherUniqueName}' not found in {orgUrl}. " +
                "Pass the publisher's *unique name* (column 'uniquename' of the publisher table, " +
                "e.g. 'contoso'), not its display name.");

        // publisherid is a lookup — it MUST be sent as an OData navigation-property binding.
        // Sending it as a primitive value ("publisherid": "/publishers(...)") makes Dataverse reject
        // the payload with 0x80048d19 ("a 'StartArray'/'StartObject'/null node was expected").
        var body = new Dictionary<string, object?>
        {
            ["uniquename"] = uniqueName,
            ["friendlyname"] = displayName,
            ["version"] = version,
            ["publisherid@odata.bind"] = $"/publishers({publisherId})"
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
        byte[]? zipBytes = null;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            zipBytes = await File.ReadAllBytesAsync(filePath, ct);
            zipBase64 = Convert.ToBase64String(zipBytes);
        }
        if (string.IsNullOrWhiteSpace(zipBase64))
            throw new ArgumentException("Either zipBase64 or filePath must be provided.");

        if (zipBytes is null)
        {
            try
            {
                zipBytes = Convert.FromBase64String(zipBase64);
            }
            catch (FormatException)
            {
                // Let the platform reject the payload — the post-import control check is the only
                // thing that needs the bytes, and it is optional.
            }
        }

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

        // A reported success is not proof that a PCF control was applied, so every control in the
        // zip is compared against what the environment stores now.
        IReadOnlyList<CustomControlVersionCheck> controlVersions = [];
        var controlWarnings = new List<string>();
        if (success && zipBytes is not null)
        {
            var manifests = CustomControlManifestReader.Read(zipBytes);
            if (manifests.Count > 0)
            {
                controlVersions = await CheckCustomControlVersionsAsync(orgUrl, manifests, ct);
                controlWarnings.AddRange(CustomControlManifestReader.BuildWarnings(controlVersions));
                foreach (var warning in controlWarnings)
                    _logger.LogWarning("Solution import: {Warning}", warning);
            }
        }

        return new SolutionImportResult(
            Success: success,
            AsyncOperationId: asyncOperationId,
            ImportJobId: importJobId,
            StatusReason: statusReason,
            ProgressPercent: progress,
            ErrorMessage: success ? null : asyncMessage,
            ComponentErrors: componentErrors,
            CustomControlWarnings: controlWarnings,
            CustomControlVersions: controlVersions);
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

    /// <summary>
    /// Read a solution's component rows from <c>solutioncomponent</c> as a paged top-level query.
    /// </summary>
    /// <remarks>
    /// This used to ride along on the solution read as
    /// <c>$expand=solution_solutioncomponent(...)</c>. An expanded collection cannot be paged — there
    /// is no <c>@odata.nextLink</c> inside an expand — so that read had no way to prove it was
    /// complete, and it could not return <c>solutioncomponentid</c> or a reliable
    /// <c>rootcomponentbehavior</c> either. A top-level query pages properly and carries both.
    /// The solution lookup is a <c>LookupType</c>, hence <c>_solutionid_value</c> in the filter
    /// (filtering on <c>solutionid</c> fails).
    /// </remarks>
    private async Task<List<SolutionComponent>> ReadComponentsAsync(
        string orgUrl,
        Guid solutionId,
        CancellationToken ct)
    {
        if (solutionId == Guid.Empty)
            return [];

        var rows = await _client.GetAllPagesAsync(
            orgUrl,
            $"api/data/v9.2/solutioncomponents?$filter=_solutionid_value eq {solutionId:D}"
            + "&$select=solutioncomponentid,objectid,componenttype,rootcomponentbehavior,rootsolutioncomponentid",
            ct: ct);

        var components = new List<SolutionComponent>(rows.Count);
        foreach (var row in rows)
        {
            var type = row.GetInt32OrZero("componenttype");

            // rootcomponentbehavior is genuinely absent on many subcomponent rows — 0 ("include
            // subcomponents") is a meaningful value there, so a missing one must stay null rather
            // than defaulting to it.
            int? behavior = row.TryGetProperty("rootcomponentbehavior", out var rb)
                            && rb.ValueKind == JsonValueKind.Number
                ? rb.GetInt32()
                : null;

            var rootId = row.TryGetGuid("rootsolutioncomponentid");
            var rowId = row.TryGetGuid("solutioncomponentid");

            components.Add(new SolutionComponent(
                ComponentId: row.TryGetGuid("objectid"),
                ComponentType: type,
                ComponentTypeName: MapComponentType(type),
                RootComponentBehavior: behavior,
                RootComponentBehaviorName: MapRootComponentBehavior(behavior),
                RootComponentId: rootId == Guid.Empty ? null : rootId,
                SolutionComponentId: rowId == Guid.Empty ? null : rowId));
        }

        return components;
    }

    /// <summary>
    /// Label for <c>solutioncomponent.rootcomponentbehavior</c> (global choice
    /// <c>solutioncomponent_rootcomponentbehavior</c>). Which solution carries a form or column
    /// hangs entirely on this value, so it is worth spelling out rather than returning a bare code.
    /// </summary>
    internal static string? MapRootComponentBehavior(int? behavior) => behavior switch
    {
        null => null,
        0 => "IncludeSubcomponents",
        1 => "DoNotIncludeSubcomponents",
        2 => "IncludeAsShellOnly",
        _ => $"Behavior{behavior}"
    };

    private async Task<Guid> ResolveSolutionIdAsync(
        string orgUrl,
        string solutionUniqueName,
        CancellationToken ct)
    {
        var raw = await _client.GetRawAsync(
            orgUrl,
            $"api/data/v9.2/solutions?$filter=uniquename eq '{solutionUniqueName}'&$select=solutionid",
            ct: ct);
        using var doc = JsonDocument.Parse(raw);

        if (doc.RootElement.TryGetProperty("value", out var items) && items.GetArrayLength() > 0)
        {
            var id = items[0].TryGetGuid("solutionid");
            if (id != Guid.Empty)
                return id;
        }

        throw new InvalidOperationException($"Solution '{solutionUniqueName}' not found.");
    }

    /// <summary>
    /// Find the membership row of a component in a solution, or null when there is none.
    /// </summary>
    private async Task<Guid?> FindMembershipAsync(
        string orgUrl,
        Guid solutionId,
        Guid componentId,
        int componentType,
        CancellationToken ct)
    {
        var rows = await _client.GetAllPagesAsync(
            orgUrl,
            $"api/data/v9.2/solutioncomponents?$filter=_solutionid_value eq {solutionId:D}"
            + $" and objectid eq {componentId:D} and componenttype eq {componentType}"
            + "&$select=solutioncomponentid",
            ct: ct);

        if (rows.Count == 0)
            return null;

        var id = rows[0].TryGetGuid("solutioncomponentid");
        return id == Guid.Empty ? null : id;
    }

    /// <summary>
    /// Work out which root component already covers a component that has no membership row of its own.
    /// </summary>
    /// <remarks>
    /// A table in a solution with <c>rootcomponentbehavior = 0</c> (include subcomponents) carries its
    /// forms and columns along, and those subcomponents get no <c>solutioncomponent</c> row. So the
    /// candidates are exactly the solution's behavior-0 tables. For a column (component type 2) the
    /// owning table can be pinned down exactly by asking each candidate whether it owns that
    /// MetadataId — worth the extra requests, because "covered by table x" is an answer and "one of
    /// these five tables" is not.
    /// </remarks>
    private async Task<(string? Owner, IReadOnlyList<string> Candidates)> FindCoveringRootsAsync(
        string orgUrl,
        Guid solutionId,
        Guid componentId,
        int componentType,
        CancellationToken ct)
    {
        try
        {
            var rows = await _client.GetAllPagesAsync(
                orgUrl,
                $"api/data/v9.2/solutioncomponents?$filter=_solutionid_value eq {solutionId:D}"
                + " and componenttype eq 1 and rootcomponentbehavior eq 0"
                + "&$select=objectid",
                ct: ct);

            if (rows.Count == 0)
                return (null, []);

            var tableNames = await new SolutionComponentNameResolver(_client, _logger)
                .GetTableNamesAsync(orgUrl, ct);

            var candidates = rows
                .Select(r => r.TryGetGuid("objectid"))
                .Where(id => id != Guid.Empty)
                .Select(id => tableNames.TryGetValue(id, out var n) ? n : id.ToString("D"))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (componentType != 2)
                return (null, candidates);

            foreach (var table in candidates)
            {
                try
                {
                    await _client.GetRawAsync(
                        orgUrl,
                        $"api/data/v9.2/EntityDefinitions(LogicalName='{table}')/Attributes({componentId:D})"
                        + "?$select=LogicalName",
                        ct: ct);
                    return (table, candidates);
                }
                catch (HttpRequestException)
                {
                    // Not this table's column — try the next candidate.
                }
            }

            return (null, candidates);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not determine covering root components in solution {SolutionId}.", solutionId);
            return (null, []);
        }
    }

    /// <summary>
    /// Add a component to a solution and verify afterwards that a membership row really exists.
    /// </summary>
    /// <remarks>
    /// <c>AddSolutionComponent</c> reports success even when it changes nothing. Adding a column to a
    /// solution that already contains the owning table with <c>rootcomponentbehavior = 0</c> creates
    /// no row: the column is covered by the table and travels with it. A bare "success" there reads as
    /// "the column is now explicitly in this solution", which is wrong and sends the caller looking
    /// for a row that will never appear — so the membership is read back and reported for what it is.
    /// The interplay of the action with <c>rootcomponentbehavior</c> is not documented; this behaviour
    /// was observed on a live environment.
    /// </remarks>
    public async Task<AddComponentResult> AddComponentAsync(
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

        var typeName = MapComponentType(componentType);
        var solutionId = await ResolveSolutionIdAsync(orgUrl, solutionUniqueName, ct);
        var membership = await FindMembershipAsync(orgUrl, solutionId, componentId, componentType, ct);

        if (membership is not null)
        {
            return new AddComponentResult(
                Success: true,
                SolutionUniqueName: solutionUniqueName,
                ComponentId: componentId,
                ComponentType: componentType,
                ComponentTypeName: typeName,
                ExplicitMembership: true,
                SolutionComponentId: membership);
        }

        var (owner, candidates) = await FindCoveringRootsAsync(
            orgUrl, solutionId, componentId, componentType, ct);

        var note = owner is not null
            ? $"AddSolutionComponent reported success but created no solutioncomponent row: this "
              + $"{typeName} is already covered by table '{owner}', which is in '{solutionUniqueName}' "
              + "with rootcomponentbehavior 0 (include subcomponents). Subcomponents of such a table "
              + "travel with it and never get a membership row of their own. Nothing further to do."
            : candidates.Count > 0
                ? $"AddSolutionComponent reported success but created no solutioncomponent row. That is "
                  + "expected when the component is covered by a table held with rootcomponentbehavior 0 "
                  + $"(include subcomponents) — '{solutionUniqueName}' holds {candidates.Count} such "
                  + "table(s), listed in coveringRootComponents. If the component does not belong to any "
                  + "of them, verify componentId and componentType: the add then really did nothing."
                : "AddSolutionComponent reported success but created no solutioncomponent row, and "
                  + $"'{solutionUniqueName}' holds no table with rootcomponentbehavior 0 that could cover "
                  + "it. The add most likely did nothing — verify componentId and componentType.";

        return new AddComponentResult(
            Success: owner is not null,
            SolutionUniqueName: solutionUniqueName,
            ComponentId: componentId,
            ComponentType: componentType,
            ComponentTypeName: typeName,
            ExplicitMembership: false,
            SolutionComponentId: null,
            Note: note,
            CoveringRootComponents: candidates.Count > 0 ? candidates : null);
    }

    /// <summary>
    /// Remove a component from a solution.
    /// </summary>
    /// <remarks>
    /// The Web API action does <b>not</b> take a <c>ComponentId</c> the way the SDK message does — its
    /// documented parameters are <c>SolutionComponent</c> (an entity reference, hence the
    /// <c>@odata.type</c> and the nested id), <c>ComponentType</c> and <c>SolutionUniqueName</c>.
    /// Sending <c>ComponentId</c> fails with
    /// <c>0x80048d19 … The parameter 'ComponentId' … is not a valid parameter for the operation</c>.
    /// <para>
    /// Which GUID belongs in <c>solutioncomponentid</c> is documented contradictorily: the SDK
    /// property reference calls it "the primary key for the SolutionComponent entity", while the
    /// official ALM sample passes an <c>EntityMetadata.MetadataId</c>. Empirically the component's own
    /// <c>objectid</c> (a table's or column's MetadataId) is what works; the membership row's
    /// <c>solutioncomponentid</c> fails with
    /// <c>0x8004f021 Cannot find solution component</c>. That is why this method takes the objectid,
    /// and why it checks the membership up front — an unhelpful platform error for "not a member"
    /// becomes a sentence that says so.
    /// </para>
    /// </remarks>
    public async Task<RemoveComponentResult> RemoveComponentAsync(
        string orgUrl,
        string solutionUniqueName,
        Guid componentId,
        int componentType,
        CancellationToken ct = default)
    {
        var typeName = MapComponentType(componentType);
        var solutionId = await ResolveSolutionIdAsync(orgUrl, solutionUniqueName, ct);
        var before = await FindMembershipAsync(orgUrl, solutionId, componentId, componentType, ct);

        if (before is null)
        {
            var (owner, candidates) = await FindCoveringRootsAsync(
                orgUrl, solutionId, componentId, componentType, ct);

            var missingNote = owner is not null
                ? $"No solutioncomponent row for this {typeName} in '{solutionUniqueName}' — it is "
                  + $"covered by table '{owner}', held with rootcomponentbehavior 0 (include "
                  + "subcomponents). A covered subcomponent cannot be removed on its own; change the "
                  + "table's behaviour or remove the table."
                : $"No solutioncomponent row for {typeName} {componentId:D} in "
                  + $"'{solutionUniqueName}' — nothing to remove. Note that componentId must be the "
                  + "component's own objectid (for a table or column its MetadataId), not the "
                  + "solutioncomponentid of the membership row.";

            return new RemoveComponentResult(
                Success: false,
                SolutionUniqueName: solutionUniqueName,
                ComponentId: componentId,
                ComponentType: componentType,
                ComponentTypeName: typeName,
                Removed: false,
                Note: missingNote,
                CoveringRootComponents: candidates.Count > 0 ? candidates : null);
        }

        // An anonymous type cannot carry the "@odata.type" annotation, so the payload is built as
        // dictionaries. Dictionary keys bypass the camelCase naming policy and stay verbatim, which
        // the case-sensitive action parameters require.
        var parameters = new Dictionary<string, object?>
        {
            ["SolutionComponent"] = new Dictionary<string, object?>
            {
                ["@odata.type"] = "Microsoft.Dynamics.CRM.solutioncomponent",
                ["solutioncomponentid"] = componentId.ToString("D")
            },
            ["ComponentType"] = componentType,
            ["SolutionUniqueName"] = solutionUniqueName
        };

        await _client.ExecuteActionAsync(orgUrl, "RemoveSolutionComponent", parameters, ct);

        var after = await FindMembershipAsync(orgUrl, solutionId, componentId, componentType, ct);
        var removed = after is null;

        return new RemoveComponentResult(
            Success: removed,
            SolutionUniqueName: solutionUniqueName,
            ComponentId: componentId,
            ComponentType: componentType,
            ComponentTypeName: typeName,
            Removed: removed,
            Note: removed
                ? null
                : "RemoveSolutionComponent returned success but the membership row is still there.");
    }

    /// <summary>
    /// Retrieve the solution layers of a single component — the same data the Maker's
    /// "Solution Layers" view shows.
    /// </summary>
    /// <remarks>
    /// Source is the <c>msdyn_componentlayer</c> virtual table. It requires BOTH
    /// <c>msdyn_componentid</c> and <c>msdyn_solutioncomponentname</c> in the filter; the latter is the
    /// component type's *name* (e.g. "Entity", "WebResource"), not its numeric code, so
    /// <paramref name="componentType"/> is translated via <see cref="MapComponentType"/> first.
    /// Filtering on the id alone silently returns zero rows, which is why an unresolvable component type
    /// throws instead of reporting "no layers".
    /// Note that <c>msdyn_componentlayer</c> ignores <c>$select</c> and always ships the large
    /// <c>msdyn_changes</c>/<c>msdyn_componentjson</c> blobs; they are deliberately not read here so they
    /// never reach the MCP channel.
    /// </remarks>
    public async Task<SolutionLayerInfo> CheckLayersAsync(
        string orgUrl,
        Guid componentId,
        int componentType,
        CancellationToken ct = default)
    {
        var componentTypeName = await ResolveComponentTypeNameAsync(orgUrl, componentType, ct);

        var filter = $"msdyn_componentid eq '{componentId}' and " +
                     $"msdyn_solutioncomponentname eq '{componentTypeName}'";
        var url = "api/data/v9.2/msdyn_componentlayers" +
                  $"?$filter={Uri.EscapeDataString(filter)}" +
                  "&$orderby=msdyn_order";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        using var doc = JsonDocument.Parse(raw);

        var layers = new List<SolutionLayer>();
        if (doc.RootElement.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var solutionName = item.GetStringOrNull("msdyn_solutionname");
                if (string.IsNullOrWhiteSpace(solutionName))
                    continue;

                layers.Add(new SolutionLayer(
                    Order: item.GetInt32OrZero("msdyn_order"),
                    SolutionName: solutionName!,
                    PublisherName: item.GetStringOrNull("msdyn_publishername"),
                    OverwriteTime: item.GetDateTimeOrNull("msdyn_overwritetime"),
                    IsTopLayer: false));
            }
        }

        // $orderby on a virtual table is not guaranteed — sort locally so "last entry wins" always holds.
        layers.Sort((a, b) => a.Order.CompareTo(b.Order));
        if (layers.Count > 0)
        {
            layers[^1] = layers[^1] with { IsTopLayer = true };
        }

        // msdyn_name repeats the component's name on every layer row — take it from the first one.
        string? componentName = null;
        if (items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
        {
            componentName = items[0].GetStringOrNull("msdyn_name");
        }

        _logger.LogInformation(
            "Component {ComponentId} ({TypeName}) has {LayerCount} solution layer(s).",
            componentId, componentTypeName, layers.Count);

        return new SolutionLayerInfo(
            ComponentId: componentId,
            ComponentType: componentType,
            ComponentTypeName: componentTypeName,
            ComponentName: componentName,
            LayerCount: layers.Count,
            TopLayerSolutionName: layers.Count > 0 ? layers[^1].SolutionName : null,
            Layers: layers);
    }

    /// <summary>
    /// Translate a numeric component type into the name <c>msdyn_componentlayer</c> expects. Falls back
    /// to the environment's <c>solutioncomponentdefinitions</c> for Solution-Component-Framework codes.
    /// Throws when the type cannot be named — querying with an unknown name would silently yield an
    /// empty layer list, which reads like "this component has no layers".
    /// </summary>
    private async Task<string> ResolveComponentTypeNameAsync(string orgUrl, int componentType, CancellationToken ct)
    {
        var name = MapComponentType(componentType);
        if (name != $"Type{componentType}")
            return name;

        var frameworkTypes = await TryResolveFrameworkComponentTypesAsync(orgUrl, ct);
        if (frameworkTypes.TryGetValue(componentType, out var resolved))
            return resolved;

        throw new InvalidOperationException(
            $"Unknown component type {componentType} — cannot query solution layers. " +
            "msdyn_componentlayer must be filtered by the component type's name (e.g. 'Entity', " +
            "'WebResource'), and this code maps to no known name in this environment.");
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

    /// <summary>
    /// Triggers a Power Platform Pipeline deployment end-to-end. Mirrors the 3 calls the Maker UI
    /// makes when the user clicks "Bereitstellung", "Weiter", and finally "Bereitstellen":
    /// <list type="number">
    /// <item>POST <c>/deploymentstageruns</c> — creates the run, starts validation.</item>
    /// <item>PATCH <c>/deploymentstageruns({id})</c> — sets version + notes once validation passes.</item>
    /// <item>POST <c>/DeployPackageAsync</c> — starts the actual deploy.</item>
    /// </list>
    /// When <paramref name="autoConfirm"/> is false, only step 1 is executed (validation only).
    /// </summary>
    /// <param name="pipelineHostOrgUrl">
    /// URL of the Pipeline-Host environment (where the <c>deploymentpipeline</c> rows live —
    /// often a dedicated env, not the source/target). Example: <c>https://orgexample.crm4.dynamics.com</c>.
    /// </param>
    /// <param name="solutionId">Solution GUID from the source (Dev) environment.</param>
    /// <param name="artifactName">Solution unique name (= artifact name in the pipeline).</param>
    /// <param name="devDeploymentEnvironmentId">
    /// Internal <c>deploymentenvironmentid</c> of the source/Dev environment in the Pipeline-Host.
    /// This is NOT the Power Platform environment GUID — it is the mapping row id.
    /// Discoverable via <c>GET /api/data/v9.2/deploymentenvironments?$filter=environmentid eq '...'</c> on the host.
    /// </param>
    /// <param name="targetStageId">The <c>deploymentstageid</c> of the target stage.</param>
    /// <param name="languageCode">Language code for the auto-generated deployment notes (e.g. "en-US", "de-DE").</param>
    /// <returns>The new <c>deploymentstagerunid</c>, useful for polling status.</returns>
    public async Task<Guid> DeployPipelineAsync(
        string pipelineHostOrgUrl,
        Guid solutionId,
        string artifactName,
        Guid devDeploymentEnvironmentId,
        Guid targetStageId,
        string languageCode = "en-US",
        string? currentVersion = null,
        string? newVersion = null,
        string deploymentNotes = "",
        string? deploymentSettingsJson = null,
        bool autoConfirm = true,
        int validationTimeoutSeconds = 600,
        int pollIntervalSeconds = 10,
        CancellationToken ct = default)
    {
        // Step 1: POST /deploymentstageruns — triggers validation.
        var createBody = new Dictionary<string, object?>
        {
            ["artifactname"] = artifactName,
            ["devdeploymentenvironment@odata.bind"] = $"/deploymentenvironments({devDeploymentEnvironmentId})",
            ["deploymentstageid@odata.bind"] = $"/deploymentstages({targetStageId})",
            ["makerainoteslanguagecode"] = languageCode,
            ["solutionid"] = solutionId.ToString()
        };

        // Use Power-Apps-Maker AppId token — Pipeline-Backend's validation workflow filters
        // by appid claim and ignores tokens from custom apps / Azure CLI.
        var rawResp = await SendPipelineAsync(
            "POST", pipelineHostOrgUrl,
            "api/data/v9.0/deploymentstageruns?$select=deploymentstagerunid",
            createBody, requestRepresentation: true, ct);

        Guid runId = Guid.Empty;
        if (!string.IsNullOrWhiteSpace(rawResp))
        {
            try
            {
                using var doc = JsonDocument.Parse(rawResp);
                if (doc.RootElement.TryGetProperty("deploymentstagerunid", out var idEl)
                    && idEl.ValueKind == JsonValueKind.String
                    && Guid.TryParse(idEl.GetString(), out var parsed))
                {
                    runId = parsed;
                }
            }
            catch (JsonException) { /* fall through to error below */ }
        }
        if (runId == Guid.Empty)
            throw new InvalidOperationException(
                $"deploymentstageruns POST did not return deploymentstagerunid. Raw response: {rawResp}");

        if (!autoConfirm)
            return runId;

        // Step 1.5: poll until validation passes (stagerunstatus = 200000007 Überprüfung erfolgreich).
        // 200000003 = Fehlgeschlagen, 200000004 = Cancelled — both terminal failures.
        var deadline = DateTime.UtcNow.AddSeconds(validationTimeoutSeconds);
        string? validationError = null;
        while (DateTime.UtcNow < deadline)
        {
            var run = await GetDeploymentStageRunAsync(pipelineHostOrgUrl, runId, ct);
            var status = run.GetProperty("stagerunstatus").GetInt32();
            if (status == 200000007) break; // Validation OK
            if (status == 200000003 || status == 200000004)
            {
                validationError = run.TryGetProperty("validationresults", out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : run.TryGetProperty("errormessage", out var e) ? e.GetString() : "unknown";
                throw new InvalidOperationException(
                    $"Pipeline validation failed for run {runId}: {validationError}");
            }
            await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), ct);
        }
        if (validationError == null && DateTime.UtcNow >= deadline)
            throw new TimeoutException($"Pipeline validation did not complete within {validationTimeoutSeconds}s for run {runId}.");

        // Step 2a (optional): PATCH deploymentsettingsjson — fills environment variables and
        // connection-reference overrides. The Maker UI sends this whenever the solution has any
        // pflicht-zu-setzenden EnvVars or ConnRefs on the target. Skip if not provided.
        if (!string.IsNullOrWhiteSpace(deploymentSettingsJson))
        {
            var envBody = new Dictionary<string, object?>
            {
                ["deploymentsettingsjson"] = deploymentSettingsJson
            };
            await SendPipelineAsync(
                "PATCH", pipelineHostOrgUrl,
                $"api/data/v9.0/deploymentstageruns({runId})",
                envBody, requestRepresentation: false, ct);
        }

        // Step 2b: PATCH — set artifact version + deployment notes.
        // Maker UI always sends artifactdevcurrentversion (= source solution version),
        // artifactversion (= target version after deploy), and deploymentnotes.
        var patchBody = new Dictionary<string, object?>
        {
            ["artifactdevcurrentversion"] = currentVersion ?? string.Empty,
            ["artifactversion"] = newVersion ?? throw new ArgumentNullException(nameof(newVersion),
                "newVersion is required when autoConfirm=true (target solution version after deploy, e.g. '1.11.0')."),
            ["deploymentnotes"] = deploymentNotes ?? string.Empty
        };
        await SendPipelineAsync(
            "PATCH", pipelineHostOrgUrl,
            $"api/data/v9.0/deploymentstageruns({runId})",
            patchBody, requestRepresentation: false, ct);

        // Step 3: POST /DeployPackageAsync — kicks off the actual deploy.
        var deployBody = new Dictionary<string, object?> { ["StageRunId"] = runId.ToString() };
        await SendPipelineAsync(
            "POST", pipelineHostOrgUrl,
            "api/data/v9.0/DeployPackageAsync",
            deployBody, requestRepresentation: false, ct);

        return runId;
    }

    /// <summary>
    /// Poll the status of a deployment stage run until it reaches a terminal state.
    /// stagerunstatus values: 200000000=NotStarted, 200000001=Running, 200000002=Succeeded,
    /// 200000003=Failed, 200000004=Cancelled, 200000005=Approved, 200000006=Pending Approval.
    /// </summary>
    public async Task<JsonElement> GetDeploymentStageRunAsync(
        string pipelineHostOrgUrl,
        Guid stageRunId,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.0/deploymentstageruns({stageRunId})" +
                  "?$select=deploymentstagerunid,stagerunstatus,errormessage,operation,operationdetails," +
                  "operationstatus,scheduledtime,starttime,endtime,validationresults,artifactname,deploymentsettingsjson";
        var raw = await _client.GetRawAsync(pipelineHostOrgUrl, url, includeFormattedValues: true, ct: ct);
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    /// <summary>
    /// List all pipelines visible on a Pipeline-Host environment.
    /// </summary>
    public async Task<IReadOnlyList<PipelineSummary>> ListPipelinesAsync(
        string pipelineHostOrgUrl,
        CancellationToken ct = default)
    {
        var raw = await _client.GetRawAsync(
            pipelineHostOrgUrl,
            "api/data/v9.2/deploymentpipelines?$select=deploymentpipelineid,name,description,statecode",
            includeFormattedValues: true,
            ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<PipelineSummary>();
        if (!doc.RootElement.TryGetProperty("value", out var items))
            return results;
        foreach (var item in items.EnumerateArray())
        {
            results.Add(new PipelineSummary(
                PipelineId: item.TryGetGuid("deploymentpipelineid"),
                Name: item.GetStringOrEmpty("name"),
                Description: item.GetStringOrNull("description"),
                State: item.GetStringOrNull("statecode@OData.Community.Display.V1.FormattedValue") ?? "?"));
        }
        return results;
    }

    /// <summary>
    /// List stages of a pipeline plus the linked target deployment environment.
    /// </summary>
    public async Task<IReadOnlyList<PipelineStageSummary>> ListPipelineStagesAsync(
        string pipelineHostOrgUrl,
        Guid pipelineId,
        CancellationToken ct = default)
    {
        var raw = await _client.GetRawAsync(
            pipelineHostOrgUrl,
            $"api/data/v9.2/deploymentstages?$filter=_deploymentpipelineid_value eq {pipelineId}" +
            "&$select=deploymentstageid,name,_previousdeploymentstageid_value,statecode" +
            "&$expand=targetdeploymentenvironmentid($select=name,environmentid,deploymentenvironmentid)",
            includeFormattedValues: true,
            ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<PipelineStageSummary>();
        if (!doc.RootElement.TryGetProperty("value", out var items))
            return results;
        foreach (var item in items.EnumerateArray())
        {
            string? targetName = null;
            string? targetEnvId = null;
            string? targetDepEnvId = null;
            if (item.TryGetProperty("targetdeploymentenvironmentid", out var te) && te.ValueKind == JsonValueKind.Object)
            {
                targetName = te.GetStringOrNull("name");
                targetEnvId = te.GetStringOrNull("environmentid");
                targetDepEnvId = te.GetStringOrNull("deploymentenvironmentid");
            }
            results.Add(new PipelineStageSummary(
                StageId: item.TryGetGuid("deploymentstageid"),
                Name: item.GetStringOrEmpty("name"),
                PreviousStageId: item.TryGetProperty("_previousdeploymentstageid_value", out var p) && p.ValueKind == JsonValueKind.String
                    ? Guid.Parse(p.GetString()!) : (Guid?)null,
                State: item.GetStringOrNull("statecode@OData.Community.Display.V1.FormattedValue") ?? "?",
                TargetEnvironmentName: targetName,
                TargetEnvironmentId: targetEnvId,
                TargetDeploymentEnvironmentId: targetDepEnvId));
        }
        return results;
    }

    /// <summary>
    /// List the deployment-environment mappings on a Pipeline-Host. Maps Power-Platform env GUIDs
    /// (<c>environmentid</c>) to their Pipeline-internal mapping rows (<c>deploymentenvironmentid</c>).
    /// </summary>
    public async Task<IReadOnlyList<DeploymentEnvironmentSummary>> ListDeploymentEnvironmentsAsync(
        string pipelineHostOrgUrl,
        CancellationToken ct = default)
    {
        var raw = await _client.GetRawAsync(
            pipelineHostOrgUrl,
            "api/data/v9.2/deploymentenvironments?$select=deploymentenvironmentid,name,environmentid",
            ct: ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<DeploymentEnvironmentSummary>();
        if (!doc.RootElement.TryGetProperty("value", out var items))
            return results;
        foreach (var item in items.EnumerateArray())
        {
            results.Add(new DeploymentEnvironmentSummary(
                DeploymentEnvironmentId: item.TryGetGuid("deploymentenvironmentid"),
                Name: item.GetStringOrEmpty("name"),
                EnvironmentId: item.GetStringOrEmpty("environmentid")));
        }
        return results;
    }

    /// <summary>
    /// Map a <c>componenttype</c> code to its label. Values mirror the official <c>componenttype</c>
    /// global choice of the <c>solutioncomponent</c> table
    /// (learn.microsoft.com/power-apps/developer/data-platform/reference/entities/solutioncomponent).
    /// Codes not listed there — in particular the Solution-Component-Framework range (&gt;= 1000,
    /// e.g. Custom API or Managed Identity) — are deliberately NOT hard-coded: they are assigned per
    /// environment and are resolved at runtime via <c>solutioncomponentdefinitions</c>
    /// (see <see cref="TryResolveFrameworkComponentTypesAsync"/>). Unknown codes fall back to
    /// <c>Type&lt;code&gt;</c> rather than guessing.
    /// </summary>
    private static string MapComponentType(int type) => type switch
    {
        1 => "Entity",
        2 => "Attribute",
        3 => "Relationship",
        4 => "AttributePicklistValue",
        5 => "AttributeLookupValue",
        6 => "ViewAttribute",
        7 => "LocalizedLabel",
        8 => "RelationshipExtraCondition",
        9 => "OptionSet",
        10 => "EntityRelationship",
        11 => "EntityRelationshipRole",
        12 => "EntityRelationshipRelationships",
        13 => "ManagedProperty",
        14 => "EntityKey",
        16 => "Privilege",
        17 => "PrivilegeObjectTypeCode",
        18 => "Index",
        20 => "Role",
        21 => "RolePrivilege",
        22 => "DisplayString",
        23 => "DisplayStringMap",
        24 => "Form",
        25 => "Organization",
        26 => "SavedQuery",
        29 => "Workflow",
        31 => "Report",
        32 => "ReportEntity",
        33 => "ReportCategory",
        34 => "ReportVisibility",
        35 => "Attachment",
        36 => "EmailTemplate",
        37 => "ContractTemplate",
        38 => "KBArticleTemplate",
        39 => "MailMergeTemplate",
        44 => "DuplicateRule",
        45 => "DuplicateRuleCondition",
        46 => "EntityMap",
        47 => "AttributeMap",
        48 => "RibbonCommand",
        49 => "RibbonContextGroup",
        50 => "RibbonCustomization",
        52 => "RibbonRule",
        53 => "RibbonTabToCommandMap",
        55 => "RibbonDiff",
        59 => "SavedQueryVisualization",
        60 => "SystemForm",
        61 => "WebResource",
        62 => "SiteMap",
        63 => "ConnectionRole",
        64 => "ComplexControl",
        65 => "HierarchyRule",
        66 => "CustomControl",
        68 => "CustomControlDefaultConfig",
        70 => "FieldSecurityProfile",
        71 => "FieldPermission",
        90 => "PluginType",
        91 => "PluginAssembly",
        92 => "SdkMessageProcessingStep",
        93 => "SdkMessageProcessingStepImage",
        95 => "ServiceEndpoint",
        150 => "RoutingRule",
        151 => "RoutingRuleItem",
        152 => "SLA",
        153 => "SLAItem",
        154 => "ConvertRule",
        155 => "ConvertRuleItem",
        161 => "MobileOfflineProfile",
        162 => "MobileOfflineProfileItem",
        165 => "SimilarityRule",
        166 => "DataSourceMapping",
        201 => "SdkMessage",
        202 => "SdkMessageFilter",
        203 => "SdkMessagePair",
        204 => "SdkMessageRequest",
        205 => "SdkMessageRequestField",
        206 => "SdkMessageResponse",
        207 => "SdkMessageResponseField",
        208 => "ImportMap",
        210 => "WebWizard",
        300 => "CanvasApp",
        371 or 372 => "Connector",
        380 => "EnvironmentVariableDefinition",
        381 => "EnvironmentVariableValue",
        400 => "AIProjectType",
        401 => "AIProject",
        402 => "AIConfiguration",
        430 => "EntityAnalyticsConfiguration",
        431 => "AttributeImageConfiguration",
        432 => "EntityImageConfiguration",
        _ => $"Type{type}"
    };

    /// <summary>
    /// Resolve labels for Solution-Component-Framework component types (codes &gt;= 1000, e.g. Custom API
    /// or Managed Identity). Those codes are not part of the documented <c>componenttype</c> choice and
    /// are assigned per environment, so they are looked up from <c>solutioncomponentdefinitions</c>
    /// instead of being hard-coded. Failures are swallowed — the caller keeps the <c>Type&lt;code&gt;</c>
    /// fallback rather than reporting a wrong name.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, string>> TryResolveFrameworkComponentTypesAsync(
        string orgUrl,
        CancellationToken ct)
    {
        var map = new Dictionary<int, string>();
        try
        {
            var raw = await _client.GetRawAsync(
                orgUrl,
                "api/data/v9.2/solutioncomponentdefinitions?$select=name,objecttypecode",
                ct: ct);
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array)
                return map;

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("objecttypecode", out var otc) || otc.ValueKind != JsonValueKind.Number)
                    continue;
                var name = item.GetStringOrNull("name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                map[otc.GetInt32()] = name!;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve solutioncomponentdefinitions for component-type labels.");
        }
        return map;
    }

    /// <summary>
    /// Compare each control in the imported zip against the version the environment stores now.
    /// </summary>
    private async Task<IReadOnlyList<CustomControlVersionCheck>> CheckCustomControlVersionsAsync(
        string orgUrl,
        IReadOnlyList<PcfControlManifest> manifests,
        CancellationToken ct)
    {
        var results = new List<CustomControlVersionCheck>(manifests.Count);

        foreach (var manifest in manifests)
        {
            string? storedName = null;
            string? storedVersion = null;
            try
            {
                var suffix = manifest.QualifiedName.Replace("'", "''");
                var rows = await _client.GetAllPagesAsync(
                    orgUrl,
                    $"api/data/v9.2/customcontrols?$select=name,version&$filter=endswith(name,'{suffix}')",
                    ct: ct);

                // Prefer the "<prefix>_<namespace>.<constructor>" hit; endswith alone could also
                // match a longer namespace that happens to end the same way.
                var match = rows.FirstOrDefault(r =>
                    (r.GetStringOrNull("name") ?? string.Empty)
                        .EndsWith("_" + manifest.QualifiedName, StringComparison.OrdinalIgnoreCase));
                if (match.ValueKind != JsonValueKind.Object && rows.Count == 1)
                    match = rows[0];

                if (match.ValueKind == JsonValueKind.Object)
                {
                    storedName = match.GetStringOrNull("name");
                    storedVersion = match.GetStringOrNull("version");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex, "Could not read the stored version of custom control {Control}.", manifest.QualifiedName);
            }

            bool? matches = storedVersion is null || manifest.Version is null
                ? null
                : string.Equals(storedVersion, manifest.Version, StringComparison.OrdinalIgnoreCase);

            results.Add(new CustomControlVersionCheck(
                ManifestName: manifest.QualifiedName,
                StoredName: storedName,
                ManifestVersion: manifest.Version,
                StoredVersion: storedVersion,
                Matches: matches));
        }

        return results;
    }
}
