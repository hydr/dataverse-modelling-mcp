namespace Dataverse.Core.Services;

using System.Text.Json;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Dataverse.Core.Workflows;
using Microsoft.Extensions.Logging;

public sealed class WorkflowService
{
    private readonly DataverseHttpClient _client;
    private readonly ILogger<WorkflowService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public WorkflowService(DataverseHttpClient client, ILogger<WorkflowService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WorkflowSummary>> ListAsync(
        string orgUrl,
        string? filter = null,
        int top = 50,
        CancellationToken ct = default)
    {
        var baseFilter = "category eq 0 and type eq 1"; // Classic workflows, definitions only (type 2 = internal activation copies)
        var combinedFilter = string.IsNullOrWhiteSpace(filter)
            ? baseFilter
            : $"{baseFilter} and ({filter})";

        var url = $"api/data/v9.2/workflows" +
                  $"?$filter={Uri.EscapeDataString(combinedFilter)}" +
                  $"&$select=workflowid,name,primaryentity,statecode,statuscode,_ownerid_value" +
                  $"&$top={top}";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<WorkflowSummary>();

        if (doc.RootElement.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                results.Add(new WorkflowSummary(
                    WorkflowId: item.TryGetGuid("workflowid"),
                    Name: item.GetStringOrEmpty("name"),
                    PrimaryEntity: item.GetStringOrNull("primaryentity"),
                    StateCode: item.GetInt32OrZero("statecode"),
                    StatusCode: item.GetInt32OrZero("statuscode"),
                    OwnerId: item.GetStringOrNull("_ownerid_value"),
                    OwnerName: null));
            }
        }

        return results;
    }

    public async Task<WorkflowDetail?> GetAsync(
        string orgUrl,
        Guid workflowId,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/workflows({workflowId})" +
                  "?$select=workflowid,name,primaryentity,statecode,statuscode," +
                  "_ownerid_value,description,xaml,createdon,modifiedon," +
                  "ondemand,triggeroncreate,triggerondelete,triggeronupdateattributelist," +
                  "createstage,updatestage,deletestage," +
                  "scope,mode,runas,istransacted,rank,syncworkflowlogonfailure,asyncautodelete";

        var raw = await _client.GetRawAsync(orgUrl, url, includeFormattedValues: true, ct);
        var item = JsonDocument.Parse(raw).RootElement;

        string? ownerName = null;
        if (item.TryGetProperty("_ownerid_value@OData.Community.Display.V1.FormattedValue", out var ownerFv))
            ownerName = ownerFv.GetString();

        return new WorkflowDetail(
            WorkflowId: item.TryGetGuid("workflowid"),
            Name: item.GetStringOrEmpty("name"),
            PrimaryEntity: item.GetStringOrNull("primaryentity"),
            StateCode: item.GetInt32OrZero("statecode"),
            StatusCode: item.GetInt32OrZero("statuscode"),
            OwnerId: item.GetStringOrNull("_ownerid_value"),
            OwnerName: ownerName,
            Description: item.GetStringOrNull("description"),
            Xaml: item.GetStringOrNull("xaml"),
            CreatedOn: item.GetDateTimeOrNull("createdon"),
            ModifiedOn: item.GetDateTimeOrNull("modifiedon"),
            OnDemand: item.TryGetProperty("ondemand", out var od) && od.ValueKind == JsonValueKind.True,
            IsOnCreate: item.TryGetProperty("triggeroncreate", out var oc) && oc.ValueKind == JsonValueKind.True,
            IsOnUpdate: item.GetInt32OrZero("updatestage") != 0,
            IsOnDelete: item.TryGetProperty("triggerondelete", out var odl) && odl.ValueKind == JsonValueKind.True,
            TriggerOnUpdateAttributes: item.GetStringOrNull("triggeronupdateattributelist"),
            CreateStage: MapStage(item.GetInt32OrZero("createstage")),
            UpdateStage: MapStage(item.GetInt32OrZero("updatestage")),
            DeleteStage: MapStage(item.GetInt32OrZero("deletestage")),
            Scope: MapScope(item.GetInt32OrZero("scope")),
            Mode: item.GetInt32OrZero("mode") == 1 ? "Realtime" : "Background",
            RunAs: item.GetInt32OrZero("runas") == 1 ? "CallingUser" : "Owner",
            IsTransacted: item.TryGetProperty("istransacted", out var tr) && tr.ValueKind == JsonValueKind.True,
            Rank: item.GetInt32OrZero("rank"),
            SyncLogOnFailure: item.TryGetProperty("syncworkflowlogonfailure", out var slf) && slf.ValueKind == JsonValueKind.True,
            AsyncAutoDelete: item.TryGetProperty("asyncautodelete", out var aad) && aad.ValueKind == JsonValueKind.True);
    }

    private static string? MapStage(int value) => value switch
    {
        20 => "PreOperation",
        40 => "PostOperation",
        _ => null
    };

    private static string MapScope(int value) => value switch
    {
        1 => "User",
        2 => "BusinessUnit",
        3 => "ParentChildBusinessUnit",
        4 => "Organization",
        _ => value.ToString()
    };


    public async Task<string?> GetXamlAsync(
        string orgUrl,
        Guid workflowId,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/workflows({workflowId})?$select=xaml";
        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var item = JsonDocument.Parse(raw).RootElement;
        return item.GetStringOrNull("xaml");
    }

    /// <summary>
    /// Creates a Classic Workflow (draft) with a valid, empty XAML skeleton.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Web API rejects a create that has no <c>xaml</c> with <c>0x80045040</c> ("created outside
    /// of the Microsoft Dynamics 365 web application"). That error is about the missing XAML, not
    /// about OData: supplying a valid skeleton makes the create succeed. Verified against Dataverse
    /// 9.2 — see docs/classic-workflows-reference.md.
    /// </para>
    /// <para>
    /// The designer's internal <c>Workflow.asmx</c> is deliberately not used: it requires the legacy
    /// web client's WRPC anti-forgery token and answers <c>INVALID_WRPC_TOKEN</c> for API callers.
    /// </para>
    /// <para>
    /// The class name inside the skeleton carries a null GUID; the platform substitutes the real
    /// workflow id when the workflow is activated.
    /// </para>
    /// </remarks>
    public async Task<Guid> CreateAsync(
        string orgUrl,
        string name,
        string primaryEntity,
        string? description = null,
        bool isRealtime = false,
        CancellationToken ct = default)
    {
        var skeleton = WorkflowXamlBuilder
            .Build(new WorkflowDefinition { PrimaryEntity = primaryEntity })
            .Xaml;

        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["category"] = 0,   // 0 = Workflow
            ["type"] = 1,       // 1 = definition (2 would be an activation copy)
            ["primaryentity"] = primaryEntity,
            ["statecode"] = 0,
            ["statuscode"] = 1,
            ["mode"] = isRealtime ? 1 : 0,
            ["description"] = description,
            ["xaml"] = skeleton
        };

        var id = await _client.PostForIdAsync(orgUrl, "api/data/v9.2/workflows", body, "workflowid", ct);
        if (id == Guid.Empty)
            throw new InvalidOperationException(
                "The workflow was created but Dataverse did not report its id.");

        _logger.LogInformation("Created classic workflow {Name} ({Id}) on {Entity}", name, id, primaryEntity);
        return id;
    }

    public async Task UpdateAsync(
        string orgUrl,
        Guid workflowId,
        Dictionary<string, object?> properties,
        CancellationToken ct = default)
    {
        await _client.PatchAsync(orgUrl, $"api/data/v9.2/workflows({workflowId})", properties, ct);
    }

    /// <summary>
    /// Deletes a Classic Workflow. The workflow must be a draft — Dataverse refuses to delete an
    /// activated one.
    /// </summary>
    /// <summary>One workflow as found by name, enough to decide whether to delete it.</summary>
    public sealed record WorkflowMatch(Guid WorkflowId, string Name, string Type, bool IsActivated);

    /// <summary>
    /// Finds every workflow whose name starts with a prefix — definitions and activation copies alike,
    /// because both have to go when clearing out test leftovers.
    /// </summary>
    public async Task<List<WorkflowMatch>> FindByNamePrefixAsync(
        string orgUrl, string namePrefix, CancellationToken ct = default)
    {
        var filter = $"startswith(name,'{namePrefix.Replace("'", "''")}')";
        var url = "api/data/v9.2/workflows?$select=workflowid,name,type,statecode" +
                  $"&$filter={Uri.EscapeDataString(filter)}&$orderby=name";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var result = new List<WorkflowMatch>();

        if (JsonDocument.Parse(raw).RootElement.TryGetProperty("value", out var items))
            foreach (var item in items.EnumerateArray())
                result.Add(new WorkflowMatch(
                    item.TryGetGuid("workflowid"),
                    item.GetStringOrEmpty("name"),
                    item.GetInt32OrZero("type") == 2 ? "activation copy" : "definition",
                    item.GetInt32OrZero("statecode") == 1));

        return result;
    }

    /// <summary>
    /// Deletes a workflow definition together with the activation copies it left behind.
    /// </summary>
    /// <remarks>
    /// Activating a classic workflow makes Dataverse store a second row (<c>type=2</c>, pointing back
    /// via <c>parentworkflowid</c>). Deactivating does not remove it and deleting the definition does
    /// not cascade, so every activate/delete cycle leaves an orphan draft behind — which is how an
    /// environment fills up with copies of test workflows.
    /// </remarks>
    public async Task DeleteAsync(string orgUrl, Guid workflowId, CancellationToken ct = default)
    {
        foreach (var copy in await ActivationCopiesAsync(orgUrl, workflowId, ct))
        {
            try
            {
                await _client.DeleteAsync(orgUrl, $"api/data/v9.2/workflows({copy})", ct);
                _logger.LogInformation("Deleted activation copy {Id} of workflow {Parent}", copy, workflowId);
            }
            catch (Exception ex)
            {
                // The definition is the important one; a stuck copy must not block it.
                _logger.LogWarning(ex, "Could not delete activation copy {Id}", copy);
            }
        }

        await _client.DeleteAsync(orgUrl, $"api/data/v9.2/workflows({workflowId})", ct);
        _logger.LogInformation("Deleted classic workflow {Id}", workflowId);
    }

    private async Task<List<Guid>> ActivationCopiesAsync(string orgUrl, Guid workflowId, CancellationToken ct)
    {
        var copies = new List<Guid>();

        try
        {
            var filter = $"_parentworkflowid_value eq {workflowId} and type eq 2";
            var url = "api/data/v9.2/workflows?$select=workflowid" +
                      $"&$filter={Uri.EscapeDataString(filter)}";

            var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
            if (JsonDocument.Parse(raw).RootElement.TryGetProperty("value", out var items))
                copies.AddRange(items.EnumerateArray()
                    .Select(i => i.TryGetGuid("workflowid"))
                    .Where(id => id != Guid.Empty));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not look up activation copies of workflow {Id}", workflowId);
        }

        return copies;
    }

    public async Task SetStateAsync(
        string orgUrl,
        Guid workflowId,
        bool activate,
        CancellationToken ct = default)
    {
        // An automatic workflow without any trigger cannot be activated. Dataverse answers with
        // 0x80045018 ("no activation parameters have been specified"), which does not say what to
        // do — so check first and explain.
        if (activate)
        {
            var detail = await GetAsync(orgUrl, workflowId, ct);
            if (detail is not null
                && !detail.OnDemand
                && !detail.IsOnCreate
                && !detail.IsOnUpdate
                && !detail.IsOnDelete)
            {
                throw new InvalidOperationException(
                    "This workflow has no trigger and is not available on demand, so Dataverse " +
                    "refuses to activate it (0x80045018). Set at least one of these with " +
                    "workflow_update before activating: " +
                    "{\"triggeroncreate\": true, \"createstage\": 40} (on create), " +
                    "{\"updatestage\": 40, \"triggeronupdateattributelist\": \"field1,field2\"} (on update), " +
                    "{\"triggerondelete\": true, \"deletestage\": 20} (on delete), " +
                    "or {\"ondemand\": true} (started manually). " +
                    "A workflow meant to be called by another one needs {\"subprocess\": true}.");
            }
        }

        // statecode 1 = Activated (statuscode 2), statecode 0 = Draft (statuscode 1)
        var body = new
        {
            statecode = activate ? 1 : 0,
            statuscode = activate ? 2 : 1
        };
        await _client.PatchAsync(orgUrl, $"api/data/v9.2/workflows({workflowId})", body, ct);
    }

    public async Task AssignAsync(
        string orgUrl,
        Guid workflowId,
        string ownerType,
        Guid ownerId,
        CancellationToken ct = default)
    {
        var entityName = ownerType.Equals("team", StringComparison.OrdinalIgnoreCase) ? "teams" : "systemusers";
        var body = new
        {
            ownerid = $"/api/data/v9.2/{entityName}({ownerId})"
        };
        await _client.PatchAsync(orgUrl, $"api/data/v9.2/workflows({workflowId})", body, ct);
    }

    public async Task<IReadOnlyList<WorkflowActivitySummary>> ListActivitiesAsync(
        string orgUrl,
        string? nameFilter = null,
        CancellationToken ct = default)
    {
        var filter = "workflowactivitygroupname ne null";
        if (!string.IsNullOrWhiteSpace(nameFilter))
            filter += $" and contains(name, '{nameFilter}')";

        var url = "api/data/v9.2/plugintypes" +
                  $"?$filter={Uri.EscapeDataString(filter)}" +
                  "&$select=plugintypeid,name,assemblyname,version,description," +
                  "workflowactivitygroupname,typename,customworkflowactivityinfo" +
                  "&$expand=pluginassemblyid($select=name,version,publickeytoken,culture)" +
                  "&$orderby=assemblyname,name";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<WorkflowActivitySummary>();

        if (!doc.RootElement.TryGetProperty("value", out var items))
            return results;

        foreach (var item in items.EnumerateArray())
        {
            var typeName = item.GetStringOrNull("typename") ?? item.GetStringOrEmpty("name");
            var asmName = item.GetStringOrEmpty("assemblyname");
            var version = item.GetStringOrEmpty("version");

            results.Add(new WorkflowActivitySummary(
                PluginTypeId: item.TryGetGuid("plugintypeid"),
                Name: item.GetStringOrEmpty("name"),
                AssemblyQualifiedName: BuildAssemblyQualifiedName(item, typeName, asmName, version),
                AssemblyName: asmName,
                Version: version,
                Description: item.GetStringOrNull("description"),
                // This column is a string ("Sample.CrmPlugins (1.0.0.0)"), not a number.
                WorkflowActivityGroupName: item.GetStringOrNull("workflowactivitygroupname")));
        }

        return results;
    }

    /// <summary>
    /// Determines the AssemblyQualifiedName for a custom workflow activity.
    /// </summary>
    /// <remarks>
    /// Priority: (1) the ready-made value from <c>customworkflowactivityinfo</c>, (2) assembled
    /// from the <c>pluginassemblyid</c> expand including the real <c>publickeytoken</c>,
    /// (3) a bare "type, assembly" fallback. Never hardcode <c>PublicKeyToken=null</c> — signed
    /// assemblies carry a real token and XAML referencing the wrong one will not load.
    /// </remarks>
    private static string BuildAssemblyQualifiedName(
        JsonElement item, string typeName, string asmName, string version)
    {
        var info = CustomActivityInfoParser.Parse(item.GetStringOrNull("customworkflowactivityinfo"));
        if (!string.IsNullOrWhiteSpace(info?.AssemblyQualifiedName))
            return info!.AssemblyQualifiedName!;

        if (item.TryGetProperty("pluginassemblyid", out var asmEl) && asmEl.ValueKind == JsonValueKind.Object)
        {
            var fullAsmName = asmEl.GetStringOrNull("name") ?? asmName;
            var asmVersion = asmEl.GetStringOrNull("version") ?? version;
            var culture = asmEl.GetStringOrNull("culture") ?? "neutral";
            var token = asmEl.GetStringOrNull("publickeytoken") ?? "null";
            return $"{typeName}, {fullAsmName}, Version={asmVersion}, Culture={culture}, PublicKeyToken={token}";
        }

        return $"{typeName}, {asmName}";
    }

    public async Task<WorkflowActivityDetail?> GetActivityParametersAsync(
        string orgUrl,
        Guid pluginTypeId,
        CancellationToken ct = default)
    {
        // Parameters come from customworkflowactivityinfo. The plugintypeattributes table this
        // used to query does not exist in current Dataverse (0x80060888).
        var url = $"api/data/v9.2/plugintypes({pluginTypeId})" +
                  "?$select=plugintypeid,name,assemblyname,version,description,typename," +
                  "workflowactivitygroupname,customworkflowactivityinfo" +
                  "&$expand=pluginassemblyid($select=name,version,publickeytoken,culture)";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var item = JsonDocument.Parse(raw).RootElement;

        var typeName = item.GetStringOrNull("typename") ?? item.GetStringOrEmpty("name");
        var asmName = item.GetStringOrEmpty("assemblyname");
        var version = item.GetStringOrEmpty("version");

        var info = CustomActivityInfoParser.Parse(item.GetStringOrNull("customworkflowactivityinfo"));

        if (info?.ValidationError is { } validationError)
            _logger.LogWarning("Custom activity {TypeName} reports a validation error: {Error}",
                typeName, validationError);

        return new WorkflowActivityDetail(
            PluginTypeId: item.TryGetGuid("plugintypeid"),
            Name: item.GetStringOrEmpty("name"),
            AssemblyQualifiedName: BuildAssemblyQualifiedName(item, typeName, asmName, version),
            AssemblyName: info?.AssemblyName ?? asmName,
            Version: info?.AssemblyVersion ?? version,
            PublicKeyToken: info?.PublicKeyToken,
            Culture: info?.Culture,
            GroupName: info?.GroupName ?? item.GetStringOrNull("workflowactivitygroupname"),
            Description: item.GetStringOrNull("description"),
            Parameters: info?.Parameters ?? []);
    }

    /// <summary>
    /// Reads the parameter metadata of several code activities at once, addressed by
    /// AssemblyQualifiedName as it appears in a definition.
    /// </summary>
    /// <remarks>
    /// Only the type part is used for matching, because a definition may name a different assembly
    /// version than the one installed. Activities that are not found are simply absent from the
    /// catalog; the validator then reports what it can and skips the parameter checks.
    /// </remarks>
    public async Task<WorkflowActivityCatalog> GetActivityCatalogAsync(
        string orgUrl,
        IEnumerable<string> assemblyQualifiedNames,
        CancellationToken ct = default)
    {
        var typeNames = assemblyQualifiedNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(WorkflowActivityCatalog.TypePart)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (typeNames.Count == 0)
            return WorkflowActivityCatalog.Empty;

        var entries = new List<KeyValuePair<string, IReadOnlyList<WorkflowActivityParameter>>>();

        // typename is the reliable column here; plugintype.name equals it for workflow activities,
        // but only typename is guaranteed to be the CLR type.
        var filter = string.Join(" or ", typeNames.Select(n =>
            $"typename eq '{n.Replace("'", "''")}' or name eq '{n.Replace("'", "''")}'"));

        var url = "api/data/v9.2/plugintypes" +
                  $"?$filter={Uri.EscapeDataString(filter)}" +
                  "&$select=plugintypeid,name,typename,customworkflowactivityinfo";

        try
        {
            var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
            var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("value", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var info = CustomActivityInfoParser.Parse(item.GetStringOrNull("customworkflowactivityinfo"));
                    if (info is null)
                        continue;

                    var key = item.GetStringOrNull("typename") ?? item.GetStringOrEmpty("name");
                    if (!string.IsNullOrWhiteSpace(key))
                        entries.Add(new(key, info.Parameters));
                }
            }
        }
        catch (Exception ex)
        {
            // Not being able to read the metadata must not block authoring — it only means the
            // parameter checks are skipped and a wrong argument surfaces on activation instead.
            _logger.LogWarning(ex, "Could not read parameter metadata for {Count} code activit(y|ies)",
                typeNames.Count);
            return WorkflowActivityCatalog.Empty;
        }

        return new WorkflowActivityCatalog(entries);
    }

    public async Task<WorkflowValidationReport> ValidateAsync(
        string orgUrl,
        Guid workflowId,
        CancellationToken ct = default)
    {
        var detail = await GetAsync(orgUrl, workflowId, ct);
        if (detail is null)
            return new WorkflowValidationReport(workflowId, "Unknown", false, ["Workflow not found."]);

        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(detail.Xaml))
            issues.Add("Workflow has no XAML definition.");

        if (string.IsNullOrWhiteSpace(detail.PrimaryEntity))
            issues.Add("Workflow has no primary entity set.");

        if (detail.StateCode != 1)
            issues.Add("Workflow is not activated (statecode != 1).");

        return new WorkflowValidationReport(workflowId, detail.Name, issues.Count == 0, issues);
    }
}

