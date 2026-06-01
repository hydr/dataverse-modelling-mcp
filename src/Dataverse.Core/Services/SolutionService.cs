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
        CancellationToken ct = default)
    {
        var parameters = new { SolutionName = uniqueName, Managed = managed };
        var result = await _client.ExecuteActionAsync<JsonElement?>(orgUrl, "ExportSolution", parameters, ct);

        string? base64 = null;
        if (result is JsonElement el && el.TryGetProperty("ExportSolutionFile", out var fileProp))
            base64 = fileProp.GetString();

        return new SolutionExportResult(uniqueName, managed, FilePath: null, Base64Content: base64);
    }

    public async Task ImportAsync(
        string orgUrl,
        string zipBase64,
        bool overwriteUnmanaged,
        CancellationToken ct = default)
    {
        var parameters = new
        {
            CustomizationFile = zipBase64,
            OverwriteUnmanagedCustomizations = overwriteUnmanaged,
            PublishWorkflows = true,
            ImportJobId = Guid.NewGuid()
        };

        await _client.ExecuteActionAsync(orgUrl, "ImportSolution", parameters, ct);
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
