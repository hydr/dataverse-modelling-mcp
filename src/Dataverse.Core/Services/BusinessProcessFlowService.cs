namespace Dataverse.Core.Services;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dataverse.Core.BusinessProcessFlows;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Dataverse.Core.Workflows;
using Microsoft.Extensions.Logging;

/// <param name="Applied">True when the XAML was written.</param>
/// <param name="Stages">The stages as written (or as they would be written), with their ids.</param>
/// <param name="Backup">The previous XAML, to undo the change with <c>bpf_restore_xaml</c>.</param>
/// <param name="Diff">On a dry run: what the write would change.</param>
public sealed record BpfSaveResult(
    bool Applied,
    Guid ProcessId,
    IReadOnlyList<BpfBuiltStage> Stages,
    WorkflowValidationResult Validation,
    string? Backup,
    string? Message,
    string? Diff = null);

/// <summary>
/// Business process flows: read, validate and write their definition, manage their lifecycle and
/// their running instances.
/// </summary>
/// <remarks>
/// <para>
/// Everything here goes through the plain Web API. The facts it relies on were established against
/// Dataverse 9.2 (see <c>docs/business-process-flows-reference.md</c>):
/// </para>
/// <list type="bullet">
/// <item>A create needs <c>scope = 4</c> (organisation). Without it the platform's
/// <c>BpfEntityAttributeValidationStep</c> fails with a NullReferenceException, surfaced as
/// <c>0x80040216 An unexpected error occurred</c>.</item>
/// <item><c>clientdata</c>, <c>uidata</c> and the <c>processstage</c> rows are derived from the XAML
/// on every write. A control without its <c>Parameters</c> makes that derivation fail with
/// <c>0x80045037 Error generating UiData</c>.</item>
/// <item>An activated process can be rewritten in place — unlike a classic workflow. A new table in
/// the definition gets its <c>bpf_&lt;table&gt;id</c> lookup on the instance table during the write.</item>
/// <item>The first activation creates the instance table and takes about two minutes.</item>
/// </list>
/// </remarks>
public sealed class BusinessProcessFlowService(DataverseHttpClient client, ILogger<BusinessProcessFlowService> logger)
{
    private const string Select =
        "workflowid,name,uniquename,primaryentity,statecode,processorder,businessprocesstype,ismanaged,modifiedon";

    private int? _languageCode;

    // ---------------------------------------------------------------- read

    public async Task<IReadOnlyList<BpfSummary>> ListAsync(
        string orgUrl, string? primaryEntity = null, bool includeTaskFlows = false, CancellationToken ct = default)
    {
        var filter = "category eq 4 and type eq 1";
        if (!includeTaskFlows)
            filter += " and businessprocesstype eq 0";
        if (!string.IsNullOrWhiteSpace(primaryEntity))
            filter += $" and primaryentity eq '{Escape(primaryEntity!)}'";

        var rows = await client.GetAllPagesAsync(orgUrl,
            $"api/data/v9.2/workflows?$select={Select}&$filter={Uri.EscapeDataString(filter)}&$orderby=primaryentity,processorder",
            ct: ct);

        return rows.Select(r => new BpfSummary(
            r.TryGetGuid("workflowid"),
            r.GetStringOrEmpty("name"),
            r.GetStringOrNull("uniquename"),
            r.GetStringOrNull("primaryentity"),
            r.GetInt32OrZero("statecode") == 1,
            NullableInt(r, "processorder"),
            r.GetInt32OrZero("businessprocesstype") == 1 ? "task flow" : "business process flow",
            r.TryGetProperty("ismanaged", out var m) && m.ValueKind == JsonValueKind.True,
            r.GetDateTimeOrNull("modifiedon"))).ToList();
    }

    public async Task<BpfDetail?> GetAsync(string orgUrl, Guid processId, CancellationToken ct = default)
    {
        string raw;
        try
        {
            raw = await client.GetRawAsync(orgUrl,
                $"api/data/v9.2/workflows({processId})?$select={Select},description,xaml,category", ct: ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        var r = JsonDocument.Parse(raw).RootElement;
        if (r.GetInt32OrZero("category") != 4)
            throw new InvalidOperationException(
                $"{processId} is not a business process flow (category {r.GetInt32OrZero("category")}). "
                + "Classic workflows are handled by the workflow_* tools.");

        return new BpfDetail(
            r.TryGetGuid("workflowid"),
            r.GetStringOrEmpty("name"),
            r.GetStringOrNull("uniquename"),
            r.GetStringOrNull("primaryentity"),
            r.GetStringOrNull("description"),
            r.GetInt32OrZero("statecode"),
            NullableInt(r, "processorder"),
            r.GetInt32OrZero("businessprocesstype"),
            r.TryGetProperty("ismanaged", out var m) && m.ValueKind == JsonValueKind.True,
            r.GetStringOrNull("xaml"),
            r.GetDateTimeOrNull("modifiedon"));
    }

    public async Task<(BpfDetail Detail, BpfParseResult Parsed)> GetDefinitionAsync(
        string orgUrl, Guid processId, CancellationToken ct = default)
    {
        var detail = await GetAsync(orgUrl, processId, ct)
                     ?? throw new InvalidOperationException($"Business process flow {processId} not found.");
        return (detail, BpfXamlParser.Parse(detail.Xaml, detail.PrimaryEntity ?? string.Empty));
    }

    /// <summary>The stages as the platform stored them — the ids instances point at.</summary>
    public async Task<IReadOnlyList<BpfStageRow>> GetStagesAsync(string orgUrl, Guid processId, CancellationToken ct = default)
    {
        var rows = await client.GetAllPagesAsync(orgUrl,
            "api/data/v9.2/processstages?$select=processstageid,stagename,primaryentitytypecode,stagecategory"
            + $"&$filter=_processid_value eq {processId}", ct: ct);

        return rows.Select(r => new BpfStageRow(
            r.TryGetGuid("processstageid"),
            r.GetStringOrEmpty("stagename"),
            r.GetStringOrNull("primaryentitytypecode"),
            NullableInt(r, "stagecategory"))).ToList();
    }

    // ---------------------------------------------------------------- validate

    /// <summary>
    /// Model rules, then tables, attributes and relationships against live metadata. Returns the
    /// definition with relationship attributes filled in where the caller left them out.
    /// </summary>
    public async Task<(WorkflowValidationResult Result, BpfDefinition Definition, BpfCatalog Fields)> ValidateAsync(
        string orgUrl, BpfDefinition definition, CancellationToken ct = default)
    {
        var model = BpfDefinitionValidator.Validate(definition);
        if (!model.CanSave)
            return (model, definition, BpfCatalog.Empty);

        var (issues, completed, fields) = await ValidateAgainstMetadataAsync(orgUrl, definition, ct);
        var all = model.Issues.Concat(issues).ToList();
        return (new WorkflowValidationResult(all.All(i => i.Severity != "error"), all), completed, fields);
    }

    private async Task<(List<WorkflowValidationIssue> Issues, BpfDefinition Definition, BpfCatalog Fields)>
        ValidateAgainstMetadataAsync(string orgUrl, BpfDefinition definition, CancellationToken ct)
    {
        var issues = new List<WorkflowValidationIssue>();
        var catalog = new BpfCatalog();
        var resolved = BpfStageResolver.Resolve(definition, assignMissingIds: false);

        foreach (var entity in resolved.Stages.Select(s => s.Entity).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (exists, enabled, attributes) = await ReadTableAsync(orgUrl, entity, ct);
            catalog.Add(entity, exists ? attributes : null);

            if (!exists)
                issues.Add(new("error", "BPF300", "$.stages",
                    $"Table '{entity}' does not exist or its metadata is not readable.",
                    "Check the logical name with table_list. Logical names are lowercase."));
            else if (!enabled)
                issues.Add(new("error", "BPF305", "$.stages",
                    $"Table '{entity}' is not enabled for business process flows.",
                    "Enable it with table_update (IsBusinessProcessEnabled = true). Note that this cannot be undone."));
        }

        await CheckProcessesAsync(orgUrl, definition, resolved, catalog, issues, ct);

        var stages = definition.Stages.ToList();

        for (var i = 0; i < resolved.Stages.Count; i++)
        {
            var stage = resolved.Stages[i];
            var path = $"$.stages[{i}]";
            if (catalog.IsMissing(stage.Entity))
                continue;

            for (var j = 0; j < stage.Source.Steps.Count; j++)
            {
                var step = stage.Source.Steps[j];
                if (step.Kind != BpfStepKind.Field || string.IsNullOrWhiteSpace(step.Attribute))
                    continue;

                CheckAttribute(stage.Entity, step.Attribute!, $"{path}.steps[{j}].attribute", displayable: true);
            }

            if (stage.Source.Branch is { } branching)
                for (var b = 0; b < branching.Branches.Count; b++)
                    CheckConditions(stage.Entity, branching.Branches[b].Conditions, $"{path}.branch.branches[{b}].conditions");

            if (stage.Source.Relationship is { } rel && !string.IsNullOrWhiteSpace(rel.Name))
            {
                var from = stage.Relationship is { } r ? resolved.Stages[r.FromIndex].Entity : null;
                var checkedRel = await CheckRelationshipAsync(orgUrl, rel, from, stage.Entity, $"{path}.relationship", issues, ct);
                if (checkedRel is not null)
                    stages[i] = stages[i] with { Relationship = checkedRel };
            }
        }

        return (issues, definition with { Stages = stages }, catalog);

        void CheckAttribute(string entity, string attribute, string path, bool displayable)
        {
            if (!catalog.Knows(entity) || catalog.IsMissing(entity))
                return;

            var info = catalog.Find(entity, attribute);
            if (info is null)
            {
                var hint = catalog.AttributesOf(entity)
                    .Where(a => a.Contains(attribute, StringComparison.OrdinalIgnoreCase)
                                || attribute.Contains(a, StringComparison.OrdinalIgnoreCase))
                    .Take(5).ToList();
                issues.Add(new("error", "BPF301", path, $"Table '{entity}' has no attribute '{attribute}'.",
                    hint.Count > 0
                        ? $"Did you mean: {string.Join(", ", hint)}? describe_table lists all of them."
                        : "Look up the logical name with describe_table."));
                return;
            }

            if (displayable && BpfControlClass.Unsupported.Contains(info.AttributeType))
                issues.Add(new("error", "BPF302", path,
                    $"'{entity}.{attribute}' is of type {info.AttributeType}, which a data step cannot show.",
                    "Pick a column of a type the form can edit (text, number, date, choice, lookup, …)."));
        }

        void CheckConditions(string entity, List<WorkflowCondition> conditions, string path)
        {
            for (var c = 0; c < conditions.Count; c++)
            {
                var condition = conditions[c];
                if (condition.IsGroup)
                {
                    CheckConditions(entity, condition.Conditions!, $"{path}[{c}].conditions");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(condition.Attribute))
                    CheckAttribute(entity, condition.Attribute, $"{path}[{c}].attribute", displayable: false);

                foreach (var (field, f) in (condition.Value?.Fields ?? []).Select((x, n) => (x, n)))
                    CheckAttribute(entity, field.Contains('.') ? field.Split('.', 2)[1] : field,
                        $"{path}[{c}].value.fields[{f}]", displayable: false);
            }
        }
    }

    /// <summary>
    /// Loads every workflow, action and flow the definition refers to into the catalog — the builder
    /// needs their names — and checks that each is usable where it is used.
    /// </summary>
    private async Task CheckProcessesAsync(
        string orgUrl, BpfDefinition definition, BpfResolvedDefinition resolved, BpfCatalog catalog,
        List<WorkflowValidationIssue> issues, CancellationToken ct)
    {
        var uses = new List<(string? Id, string Path, string Kind, string Entity)>();

        for (var i = 0; i < resolved.Stages.Count; i++)
        {
            var stage = resolved.Stages[i];
            for (var j = 0; j < stage.Source.Steps.Count; j++)
            {
                var step = stage.Source.Steps[j];
                if (step.Kind != BpfStepKind.Field)
                    uses.Add((step.ProcessId, $"$.stages[{i}].steps[{j}].processId", step.Kind, stage.Entity));
            }

            for (var t = 0; t < stage.Source.Workflows.Count; t++)
                uses.Add((stage.Source.Workflows[t].WorkflowId, $"$.stages[{i}].workflows[{t}].workflowId", "trigger", stage.Entity));
        }

        for (var t = 0; t < definition.Workflows.Count; t++)
            uses.Add((definition.Workflows[t].WorkflowId, $"$.workflows[{t}].workflowId", "trigger", definition.PrimaryEntity));

        foreach (var (rawId, path, kind, entity) in uses)
        {
            if (!Guid.TryParse(rawId, out var id))
                continue;

            if (!catalog.KnowsProcess(id))
                catalog.AddProcess(id, await ReadProcessAsync(orgUrl, id, ct));

            var info = catalog.FindProcess(id);
            if (info is null)
            {
                issues.Add(new("error", "BPF307", path, $"There is no workflow, action or flow {id}.",
                    kind == BpfStepKind.Flow ? "flow_list lists the flows." : "workflow_list lists the classic workflows."));
                continue;
            }

            var fits = kind switch
            {
                BpfStepKind.Flow => info.Category == 5,
                BpfStepKind.Action => (info.Category == 0 && info.OnDemand) || info.Category == 3,
                _ => info.Category == 0 && info.OnDemand
            };

            if (!fits)
            {
                issues.Add(new("error", "BPF308", path,
                    $"'{info.Name}' ({CategoryName(info.Category)}{(info.Category == 0 && !info.OnDemand ? ", not on-demand" : "")}) "
                    + $"cannot be used as {(kind == "trigger" ? "a triggered workflow" : $"a {kind} step")}.",
                    kind switch
                    {
                        BpfStepKind.Flow => "A flow step needs an instant cloud flow.",
                        BpfStepKind.Action => "An action step needs an on-demand classic workflow or a custom process action.",
                        _ => "A triggered workflow must be a classic workflow that can run on demand (ondemand = true)."
                    }));
                continue;
            }

            if (!info.IsActivated)
                issues.Add(new("error", "BPF309", path, $"'{info.Name}' is not activated.",
                    "Activate it first; the designer only offers activated processes, and a draft never runs."));

            if (kind != BpfStepKind.Flow
                && !string.Equals(info.PrimaryEntity, entity, StringComparison.OrdinalIgnoreCase))
                issues.Add(new("error", "BPF310", path,
                    $"'{info.Name}' runs on '{info.PrimaryEntity}', but is used on '{entity}'.",
                    $"Use a {(kind == "trigger" ? "workflow" : "process")} whose primary table is '{entity}'."));
        }
    }

    private async Task<BpfProcessInfo?> ReadProcessAsync(string orgUrl, Guid id, CancellationToken ct)
    {
        try
        {
            var raw = await client.GetRawAsync(orgUrl,
                $"api/data/v9.2/workflows({id})?$select=workflowid,name,uniquename,category,primaryentity,statecode,ondemand,type,_sdkmessageid_value",
                ct: ct);
            var r = JsonDocument.Parse(raw).RootElement;

            // An action is called by its message name, which is what the process stores — not the
            // workflow's uniquename, which lacks the publisher prefix. workflow has no navigation
            // property to sdkmessage, hence the second read.
            var uniqueName = r.GetStringOrNull("uniquename");
            if (r.GetInt32OrZero("category") == 3 && Guid.TryParse(r.GetStringOrNull("_sdkmessageid_value"), out var messageId))
            {
                var messageRaw = await client.GetRawAsync(orgUrl, $"api/data/v9.2/sdkmessages({messageId})?$select=name", ct: ct);
                uniqueName = JsonDocument.Parse(messageRaw).RootElement.GetStringOrNull("name") ?? uniqueName;
            }

            return new BpfProcessInfo(
                id,
                r.GetStringOrEmpty("name"),
                uniqueName,
                r.GetInt32OrZero("category"),
                r.GetStringOrNull("primaryentity"),
                r.GetInt32OrZero("statecode") == 1,
                r.TryGetProperty("ondemand", out var od) && od.ValueKind == JsonValueKind.True);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static string CategoryName(int category) => category switch
    {
        0 => "classic workflow",
        1 => "dialog",
        2 => "business rule",
        3 => "custom action",
        4 => "business process flow",
        5 => "cloud flow",
        _ => $"category {category}"
    };

    private async Task<(bool Exists, bool Enabled, Dictionary<string, BpfFieldInfo> Attributes)> ReadTableAsync(
        string orgUrl, string entity, CancellationToken ct)
    {
        try
        {
            var tableRaw = await client.GetRawAsync(orgUrl,
                $"api/data/v9.2/EntityDefinitions(LogicalName='{Uri.EscapeDataString(entity)}')?$select=LogicalName,IsBusinessProcessEnabled",
                ct: ct);
            var enabled = JsonDocument.Parse(tableRaw).RootElement.TryGetProperty("IsBusinessProcessEnabled", out var e)
                          && e.ValueKind == JsonValueKind.True;

            var attributesRaw = await client.GetRawAsync(orgUrl,
                $"api/data/v9.2/EntityDefinitions(LogicalName='{Uri.EscapeDataString(entity)}')/Attributes"
                + "?$select=LogicalName,AttributeType,DisplayName,AttributeOf", ct: ct);

            var attributes = new Dictionary<string, BpfFieldInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in JsonDocument.Parse(attributesRaw).RootElement.GetProperty("value").EnumerateArray())
            {
                // Shadow columns (…name, …yominame) belong to another attribute and cannot be placed.
                if (a.GetStringOrNull("AttributeOf") is not null)
                    continue;

                var name = a.GetStringOrNull("LogicalName");
                if (name is null)
                    continue;

                string? display = null;
                if (a.TryGetProperty("DisplayName", out var dn) && dn.ValueKind == JsonValueKind.Object
                    && dn.TryGetProperty("UserLocalizedLabel", out var ul) && ul.ValueKind == JsonValueKind.Object)
                    display = ul.GetStringOrNull("Label");

                attributes[name] = new BpfFieldInfo(a.GetStringOrEmpty("AttributeType"), display);
            }

            return (true, enabled, attributes);
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "Could not read metadata for table {Entity}", entity);
            return (false, false, []);
        }
    }

    /// <summary>
    /// The relationship must be a 1:N from the table the process comes from to the stage's table; the
    /// attribute is the lookup on the stage's table.
    /// </summary>
    private async Task<BpfRelationship?> CheckRelationshipAsync(
        string orgUrl, BpfRelationship rel, string? fromEntity, string toEntity, string path,
        List<WorkflowValidationIssue> issues, CancellationToken ct)
    {
        JsonElement r;
        try
        {
            var raw = await client.GetRawAsync(orgUrl,
                $"api/data/v9.2/RelationshipDefinitions(SchemaName='{Uri.EscapeDataString(rel.Name)}')", ct: ct);
            r = JsonDocument.Parse(raw).RootElement;
        }
        catch (HttpRequestException)
        {
            issues.Add(new("error", "BPF303", $"{path}.name", $"There is no relationship '{rel.Name}'.",
                "Use the schema name of a 1:N relationship; describe_table lists them per table."));
            return null;
        }

        var referenced = r.GetStringOrNull("ReferencedEntity");
        var referencing = r.GetStringOrNull("ReferencingEntity");
        var attribute = r.GetStringOrNull("ReferencingAttribute");

        if (referencing is null || referenced is null)
        {
            issues.Add(new("error", "BPF303", $"{path}.name", $"'{rel.Name}' is not a 1:N relationship.",
                "A cross-table stage needs a 1:N relationship (a lookup on the stage's table)."));
            return null;
        }

        var fitsTarget = string.Equals(referencing, toEntity, StringComparison.OrdinalIgnoreCase);
        var fitsSource = fromEntity is null || string.Equals(referenced, fromEntity, StringComparison.OrdinalIgnoreCase);

        if (!fitsTarget || !fitsSource)
        {
            issues.Add(new("error", "BPF304", $"{path}.name",
                $"'{rel.Name}' links {referenced} (1) to {referencing} (N), but the stage moves from "
                + $"{fromEntity ?? "?"} to {toEntity}.",
                $"Use a 1:N relationship from {fromEntity ?? "the previous table"} to {toEntity}: the stage's "
                + "table must hold the lookup to the table the process comes from."));
            return null;
        }

        if (rel.Attribute is not null && !string.Equals(rel.Attribute, attribute, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("error", "BPF304", $"{path}.attribute",
                $"The lookup of '{rel.Name}' is '{attribute}', not '{rel.Attribute}'.",
                $"Set 'attribute' to \"{attribute}\" or leave it out."));
            return null;
        }

        return rel with { Attribute = attribute };
    }

    // ---------------------------------------------------------------- write

    /// <summary>
    /// Creates a business process flow from a definition. Draft unless <paramref name="activate"/>.
    /// </summary>
    /// <param name="uniqueName">
    /// Becomes the logical name of the instance table, so it carries a publisher prefix
    /// ("sample_leadtoorder"). Derived from the name and the solution's publisher when omitted.
    /// </param>
    public async Task<(BpfSaveResult Result, string UniqueName)> CreateAsync(
        string orgUrl,
        string name,
        BpfDefinition definition,
        string? uniqueName = null,
        string? description = null,
        string? solutionUniqueName = null,
        bool activate = false,
        CancellationToken ct = default)
    {
        var (validation, completed, fields) = await ValidateAsync(orgUrl, definition, ct);
        uniqueName ??= await DeriveUniqueNameAsync(orgUrl, name, solutionUniqueName, ct);

        var nameIssues = await CheckUniqueNameAsync(orgUrl, uniqueName, ct);
        if (nameIssues.Count > 0)
            validation = new WorkflowValidationResult(false, validation.Issues.Concat(nameIssues).ToList());

        if (!validation.CanSave)
            return (new BpfSaveResult(false, Guid.Empty, [], validation, null,
                $"Nothing was created: {validation.ErrorCount} error(s) must be fixed first."), uniqueName);

        // The id is chosen here so the XAML's class name can carry it, as the designer's does.
        var id = Guid.NewGuid();
        var language = await LanguageAsync(orgUrl, ct);
        var build = BpfXamlBuilder.Build(completed, id, fields, language);

        var body = new Dictionary<string, object?>
        {
            ["workflowid"] = id,
            ["name"] = name,
            ["uniquename"] = uniqueName,
            ["description"] = description,
            ["category"] = 4,
            ["businessprocesstype"] = 0,
            ["type"] = 1,
            ["primaryentity"] = completed.PrimaryEntity,
            // Without scope the platform's validation dereferences null — see the class remarks.
            ["scope"] = 4,
            ["xaml"] = build.Xaml
        };

        var headers = string.IsNullOrWhiteSpace(solutionUniqueName)
            ? null
            : new Dictionary<string, string> { ["MSCRM.SolutionUniqueName"] = solutionUniqueName! };

        await client.PostRawAsync(orgUrl, "api/data/v9.2/workflows", body, headers, requestRepresentation: false, ct);
        logger.LogInformation("Created business process flow {Name} ({Id})", name, id);

        string? message = null;
        if (activate)
        {
            await SetStateAsync(orgUrl, id, true, ct);
            message = $"Created and activated. Instances are stored in table '{uniqueName}'.";
        }
        else
        {
            message = "Created as a draft. Activate it with bpf_set_state; the first activation creates the "
                      + $"instance table '{uniqueName}' and takes about two minutes.";
        }

        return (new BpfSaveResult(true, id, build.Stages, validation, null, message), uniqueName);
    }

    /// <summary>
    /// Rewrites a process's definition. Works on an activated process — the platform updates the
    /// stages and the instance table in place.
    /// </summary>
    /// <remarks>
    /// Stages and steps without an id are matched to the existing ones by name and table (steps by
    /// attribute), so a definition written from scratch keeps the ids running instances depend on.
    /// </remarks>
    public async Task<BpfSaveResult> SetDefinitionAsync(
        string orgUrl, Guid processId, BpfDefinition definition, bool dryRun = false, CancellationToken ct = default)
    {
        var (detail, parsed) = await GetDefinitionAsync(orgUrl, processId, ct);

        if (detail.BusinessProcessType != 0)
            throw new InvalidOperationException("This is a task flow; only business process flows can be written.");

        if (string.IsNullOrWhiteSpace(definition.PrimaryEntity))
            definition = definition with { PrimaryEntity = detail.PrimaryEntity ?? string.Empty };

        definition = AdoptExistingIds(definition, parsed.Definition);

        var (validation, completed, fields) = await ValidateAsync(orgUrl, definition, ct);

        if (!string.Equals(completed.PrimaryEntity, detail.PrimaryEntity, StringComparison.OrdinalIgnoreCase))
            validation = new WorkflowValidationResult(false, validation.Issues.Append(new WorkflowValidationIssue(
                "error", "BPF008", "$.primaryEntity",
                $"The process runs on '{detail.PrimaryEntity}'; its primary table cannot change.",
                "Keep primaryEntity, or create a new process with bpf_create.")).ToList());

        if (!validation.CanSave)
            return new BpfSaveResult(false, processId, [], validation, null,
                $"Nothing was written: {validation.ErrorCount} error(s) must be fixed first.");

        var issues = validation.Issues.ToList();
        if (!parsed.FullyUnderstood)
            issues.Add(new WorkflowValidationIssue("warning", "BPF061", "$",
                "The current process contains parts this server does not understand; writing drops them: "
                + string.Join("; ", parsed.Unrecognised),
                "Check the list. Run with dryRun=true first and keep the backup."));

        // Instances standing on a stage that disappears lose their place in the process.
        if (detail.StateCode == 1 && detail.UniqueName is not null)
            issues.AddRange(await RemovedStagesInUseAsync(orgUrl, detail.UniqueName, parsed.Definition, completed, ct));

        var language = await LanguageAsync(orgUrl, ct);
        var build = BpfXamlBuilder.Build(completed, processId, fields, language);
        var result = new WorkflowValidationResult(true, issues);

        if (dryRun)
            return new BpfSaveResult(false, processId, build.Stages, result, detail.Xaml, "Dry run — nothing was written.",
                DescribeChange(parsed.Definition, completed));

        await client.PatchAsync(orgUrl, $"api/data/v9.2/workflows({processId})",
            new Dictionary<string, object?> { ["xaml"] = build.Xaml }, ct);

        logger.LogInformation("Wrote definition of business process flow {Id} ({Stages} stages)", processId, build.Stages.Count);
        return new BpfSaveResult(true, processId, build.Stages, result, detail.Xaml,
            detail.StateCode == 1 ? "Written; the process stayed active." : "Written.");
    }

    /// <summary>Writes a previously exported XAML back verbatim.</summary>
    public Task RestoreXamlAsync(string orgUrl, Guid processId, string xaml, CancellationToken ct = default) =>
        client.PatchAsync(orgUrl, $"api/data/v9.2/workflows({processId})",
            new Dictionary<string, object?> { ["xaml"] = xaml }, ct);

    /// <summary>Renames or re-describes a process.</summary>
    public Task UpdatePropertiesAsync(string orgUrl, Guid processId, string? name, string? description, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>();
        if (name is not null) body["name"] = name;
        if (description is not null) body["description"] = description;
        return client.PatchAsync(orgUrl, $"api/data/v9.2/workflows({processId})", body, ct);
    }

    /// <summary>
    /// Activates or deactivates. The first activation creates the instance table (about two minutes,
    /// synchronous); later ones are quick.
    /// </summary>
    public Task SetStateAsync(string orgUrl, Guid processId, bool activate, CancellationToken ct = default) =>
        client.PatchAsync(orgUrl, $"api/data/v9.2/workflows({processId})",
            new Dictionary<string, object?>
            {
                ["statecode"] = activate ? 1 : 0,
                ["statuscode"] = activate ? 2 : 1
            }, ct);

    /// <summary>Deletes a process; an activated one is deactivated first when asked to.</summary>
    public async Task DeleteAsync(string orgUrl, Guid processId, bool deactivateFirst, CancellationToken ct = default)
    {
        var detail = await GetAsync(orgUrl, processId, ct)
                     ?? throw new InvalidOperationException($"Business process flow {processId} not found.");

        if (detail.StateCode == 1)
        {
            if (!deactivateFirst)
                throw new InvalidOperationException(
                    "The process is activated. Pass deactivateFirst=true, or deactivate it with bpf_set_state.");
            await SetStateAsync(orgUrl, processId, false, ct);
        }

        await client.DeleteAsync(orgUrl, $"api/data/v9.2/workflows({processId})", ct);
        logger.LogInformation("Deleted business process flow {Id}", processId);
    }

    /// <summary>
    /// Sets the order of a table's processes: the first one a user has access to is applied to new rows.
    /// </summary>
    public async Task<IReadOnlyList<BpfSummary>> SetOrderAsync(
        string orgUrl, string primaryEntity, IReadOnlyList<Guid> orderedIds, CancellationToken ct = default)
    {
        var existing = await ListAsync(orgUrl, primaryEntity, includeTaskFlows: true, ct);
        var unknown = orderedIds.Where(id => existing.All(e => e.ProcessId != id)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"Not a process of table '{primaryEntity}': {string.Join(", ", unknown)}. bpf_list shows them.");

        // Unlisted processes keep their relative order after the listed ones.
        var rest = existing.Where(e => !orderedIds.Contains(e.ProcessId)).Select(e => e.ProcessId);
        var order = orderedIds.Concat(rest).ToList();

        for (var i = 0; i < order.Count; i++)
        {
            var current = existing.First(e => e.ProcessId == order[i]);
            if (current.ProcessOrder == i + 1)
                continue;
            await client.PatchAsync(orgUrl, $"api/data/v9.2/workflows({order[i]})",
                new Dictionary<string, object?> { ["processorder"] = i + 1 }, ct);
        }

        return await ListAsync(orgUrl, primaryEntity, includeTaskFlows: true, ct);
    }

    /// <summary>
    /// Grants security roles access to a process: create/read/write (and the rest of the usual set) on
    /// its instance table, at organisation depth.
    /// </summary>
    /// <remarks>
    /// Access to a business process flow <em>is</em> access to its instance table — the designer's
    /// "Enable security roles" writes exactly these privileges. Until granted, only System
    /// Administrator and System Customizer see the process.
    /// </remarks>
    public async Task<IReadOnlyList<string>> GrantAccessAsync(
        string orgUrl, Guid processId, IReadOnlyList<Guid> roleIds, bool readOnly, CancellationToken ct = default)
    {
        var detail = await GetAsync(orgUrl, processId, ct)
                     ?? throw new InvalidOperationException($"Business process flow {processId} not found.");
        var table = detail.UniqueName
                    ?? throw new InvalidOperationException("The process has no unique name, so its instance table is unknown.");

        string raw;
        try
        {
            raw = await client.GetRawAsync(orgUrl,
                $"api/data/v9.2/EntityDefinitions(LogicalName='{Uri.EscapeDataString(table)}')?$select=Privileges", ct: ct);
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(
                $"The instance table '{table}' does not exist yet. Activate the process once with bpf_set_state.");
        }

        var wanted = readOnly
            ? new[] { "Read" }
            : new[] { "Create", "Read", "Write", "Delete", "Append", "AppendTo", "Assign", "Share" };

        var privileges = JsonDocument.Parse(raw).RootElement.GetProperty("Privileges").EnumerateArray()
            .Where(p => wanted.Contains(p.GetStringOrEmpty("PrivilegeType")))
            .Select(p => (Id: p.TryGetGuid("PrivilegeId"), Name: p.GetStringOrEmpty("Name")))
            .ToList();

        foreach (var role in roleIds)
        {
            await client.ExecuteActionAsync(orgUrl, $"roles({role})/Microsoft.Dynamics.CRM.AddPrivilegesRole", new
            {
                Privileges = privileges.Select(p => new Dictionary<string, object>
                {
                    ["PrivilegeId"] = p.Id,
                    ["Depth"] = "Global",
                    ["BusinessUnitId"] = Guid.Empty
                }).ToArray()
            }, ct);
        }

        return privileges.Select(p => p.Name).ToList();
    }

    // ---------------------------------------------------------------- instances

    /// <summary>The instance table of a process, with the lookup column per table it covers.</summary>
    public async Task<BpfInstanceTable> GetInstanceTableAsync(string orgUrl, string uniqueName, CancellationToken ct = default)
    {
        string raw;
        try
        {
            raw = await client.GetRawAsync(orgUrl,
                $"api/data/v9.2/EntityDefinitions(LogicalName='{Uri.EscapeDataString(uniqueName)}')"
                + "?$select=LogicalName,EntitySetName&$expand=ManyToOneRelationships($select=ReferencedEntity,ReferencingAttribute,ReferencingEntityNavigationPropertyName)",
                ct: ct);
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(
                $"Table '{uniqueName}' does not exist. A process gets its instance table on its first activation.");
        }

        var r = JsonDocument.Parse(raw).RootElement;
        var lookups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rel in r.GetProperty("ManyToOneRelationships").EnumerateArray())
        {
            var attribute = rel.GetStringOrEmpty("ReferencingAttribute");
            if (attribute.StartsWith("bpf_", StringComparison.OrdinalIgnoreCase)
                && attribute.EndsWith("id", StringComparison.OrdinalIgnoreCase)
                && rel.GetStringOrNull("ReferencedEntity") is { } referenced)
                lookups[referenced] = rel.GetStringOrNull("ReferencingEntityNavigationPropertyName") ?? attribute;
        }

        return new BpfInstanceTable(r.GetStringOrEmpty("LogicalName"), r.GetStringOrEmpty("EntitySetName"), lookups);
    }

    /// <summary>
    /// All instances running over one record, across every process — most recently touched first,
    /// which is the one the form shows.
    /// </summary>
    public async Task<IReadOnlyList<BpfInstance>> ListInstancesForRecordAsync(
        string orgUrl, string entity, Guid recordId, CancellationToken ct = default)
    {
        var raw = await client.GetRawAsync(orgUrl,
            $"api/data/v9.2/RetrieveProcessInstances(EntityLogicalName=@e,EntityId=@id)?@e='{Uri.EscapeDataString(entity)}'&@id={recordId}",
            ct: ct);

        // The function answers with a plain collection of businessprocessflowinstance rows.
        var root = JsonDocument.Parse(raw).RootElement;
        if (!root.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];

        var stageNames = new Dictionary<Guid, string>();
        var result = new List<BpfInstance>();

        foreach (var item in items.EnumerateArray())
        {
            var attributes = Flatten(item);

            var processId = GuidOf(attributes, "processid");
            var stageId = GuidOf(attributes, "processstageid");
            string? stageName = null;
            if (stageId is { } sid)
            {
                if (!stageNames.TryGetValue(sid, out stageName))
                {
                    stageName = await StageNameAsync(orgUrl, sid, ct);
                    if (stageName is not null)
                        stageNames[sid] = stageName;
                }
            }

            result.Add(new BpfInstance(
                GuidOf(attributes, "businessprocessflowinstanceid") ?? Guid.Empty,
                processId ?? Guid.Empty,
                attributes.GetValueOrDefault("name"),
                stageId,
                stageName,
                attributes.GetValueOrDefault("traversedpath"),
                StatusName(attributes.GetValueOrDefault("statuscode")),
                DateTime.TryParse(attributes.GetValueOrDefault("modifiedon"), out var mod) ? mod : null));
        }

        return result;
    }

    /// <summary>Starts a process on a record (or switches the record to it).</summary>
    /// <param name="stageId">Stage to start on; the first stage when omitted.</param>
    public async Task<Guid> StartInstanceAsync(
        string orgUrl, Guid processId, Guid recordId, Guid? stageId = null, CancellationToken ct = default)
    {
        var (detail, parsed) = await GetDefinitionAsync(orgUrl, processId, ct);
        var table = await GetInstanceTableAsync(orgUrl, detail.UniqueName ?? string.Empty, ct);
        var entity = detail.PrimaryEntity ?? string.Empty;

        if (!table.RecordLookups.TryGetValue(entity, out var navigation))
            throw new InvalidOperationException($"The instance table has no lookup to '{entity}'.");

        var stages = BpfStageResolver.Resolve(parsed.Definition, assignMissingIds: false).Stages;
        var first = stages.FirstOrDefault()?.StageId
                    ?? throw new InvalidOperationException("The process has no stages.");
        var target = stageId?.ToString("D") ?? first;

        var body = new Dictionary<string, object?>
        {
            [$"{navigation}@odata.bind"] = $"/{await EntitySetAsync(orgUrl, entity, ct)}({recordId})",
            ["activestageid@odata.bind"] = $"/processstages({target})"
        };

        // Starting anywhere but the first stage needs the way there.
        if (!string.Equals(target, first, StringComparison.OrdinalIgnoreCase))
            body["traversedpath"] = string.Join(",", PathTo(stages, target)
                ?? throw new InvalidOperationException($"Stage {target} cannot be reached on the main path; start at the first stage and move."));

        var id = await client.PostForIdAsync(orgUrl, $"api/data/v9.2/{table.EntitySetName}", body,
            "businessprocessflowinstanceid", ct);
        return id;
    }

    /// <summary>
    /// Moves an instance to another stage: back to any stage on its path, or forward to a stage that
    /// follows the active one (its next stage or a branch target).
    /// </summary>
    /// <param name="recordId">For a move onto another table: the record the new stage works on.</param>
    public async Task<string> MoveInstanceAsync(
        string orgUrl, Guid processId, Guid instanceId, Guid stageId, Guid? recordId = null, CancellationToken ct = default)
    {
        var (detail, parsed) = await GetDefinitionAsync(orgUrl, processId, ct);
        var table = await GetInstanceTableAsync(orgUrl, detail.UniqueName ?? string.Empty, ct);
        var resolved = BpfStageResolver.Resolve(parsed.Definition, assignMissingIds: false);

        var raw = await client.GetRawAsync(orgUrl,
            $"api/data/v9.2/{table.EntitySetName}({instanceId})?$select=traversedpath,_activestageid_value", ct: ct);
        var row = JsonDocument.Parse(raw).RootElement;
        var path = (row.GetStringOrNull("traversedpath") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant()).ToList();
        var active = row.GetStringOrNull("_activestageid_value")?.ToLowerInvariant();
        var target = stageId.ToString("D");

        var targetStage = resolved.Stages.FirstOrDefault(s => string.Equals(s.StageId, target, StringComparison.OrdinalIgnoreCase))
                          ?? throw new InvalidOperationException($"Stage {target} is not part of this process.");

        List<string> newPath;
        var back = path.IndexOf(target);
        if (back >= 0)
        {
            newPath = path.Take(back + 1).ToList();
        }
        else
        {
            var current = resolved.Stages.FirstOrDefault(s => string.Equals(s.StageId, active, StringComparison.OrdinalIgnoreCase))
                          ?? throw new InvalidOperationException("The instance's active stage is not part of the process definition.");
            var successors = new List<string?> { current.NextStageId };
            successors.AddRange(BpfStageResolver.BranchTargets(current.Source).Select(t => resolved.IdOf(t)));

            if (!successors.Any(s => string.Equals(s, target, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"Stage '{targetStage.Source.Name}' does not follow the active stage '{current.Source.Name}'. "
                    + "Forward moves go one stage at a time.");

            newPath = [.. path, target];
        }

        var body = new Dictionary<string, object?>
        {
            ["activestageid@odata.bind"] = $"/processstages({target})",
            ["traversedpath"] = string.Join(",", newPath)
        };

        var currentEntity = resolved.Stages.FirstOrDefault(s => string.Equals(s.StageId, active, StringComparison.OrdinalIgnoreCase))?.Entity;
        if (recordId is null && back < 0 && currentEntity is not null
            && !string.Equals(currentEntity, targetStage.Entity, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Stage '{targetStage.Source.Name}' is on '{targetStage.Entity}', the active stage on '{currentEntity}'. "
                + $"Pass recordId: the {targetStage.Entity} record the process continues on (the platform answers "
                + "\"Participating entity record of stage … is not valid\" otherwise).");

        if (recordId is { } rid)
        {
            if (!table.RecordLookups.TryGetValue(targetStage.Entity, out var navigation))
                throw new InvalidOperationException($"The instance table has no lookup to '{targetStage.Entity}'.");
            body[$"{navigation}@odata.bind"] = $"/{await EntitySetAsync(orgUrl, targetStage.Entity, ct)}({rid})";
        }

        await client.PatchAsync(orgUrl, $"api/data/v9.2/{table.EntitySetName}({instanceId})", body, ct);
        return targetStage.Source.Name;
    }

    /// <summary>Finishes, abandons or reactivates an instance.</summary>
    /// <param name="status">"active", "finished" or "aborted".</param>
    public async Task SetInstanceStatusAsync(
        string orgUrl, Guid processId, Guid instanceId, string status, CancellationToken ct = default)
    {
        var detail = await GetAsync(orgUrl, processId, ct)
                     ?? throw new InvalidOperationException($"Business process flow {processId} not found.");
        var table = await GetInstanceTableAsync(orgUrl, detail.UniqueName ?? string.Empty, ct);

        var (state, statusCode) = status.ToLowerInvariant() switch
        {
            "active" => (0, 1),
            "finished" => (1, 2),
            "aborted" or "abandoned" => (1, 3),
            _ => throw new ArgumentException("status must be 'active', 'finished' or 'aborted'.")
        };

        await client.PatchAsync(orgUrl, $"api/data/v9.2/{table.EntitySetName}({instanceId})",
            new Dictionary<string, object?> { ["statecode"] = state, ["statuscode"] = statusCode }, ct);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Copies the ids of existing stages and steps onto those of a new definition that have none, by
    /// name and table (steps: by attribute within the matched stage).
    /// </summary>
    public static BpfDefinition AdoptExistingIds(BpfDefinition definition, BpfDefinition existing)
    {
        var existingResolved = BpfStageResolver.Resolve(existing, assignMissingIds: false).Stages;
        var newResolved = BpfStageResolver.Resolve(definition, assignMissingIds: false).Stages;
        var taken = new HashSet<string>(definition.Stages.Where(s => s.StageId is not null).Select(s => s.StageId!.Trim('{', '}')),
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < definition.Stages.Count; i++)
        {
            var stage = definition.Stages[i];
            BpfResolvedStage? match = null;

            if (stage.StageId is null)
            {
                match = existingResolved.FirstOrDefault(e =>
                    !taken.Contains(e.StageId)
                    && string.Equals(e.Source.Name, stage.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Entity, newResolved[i].Entity, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    stage.StageId = match.StageId;
                    taken.Add(match.StageId);
                }
            }
            else
            {
                match = existingResolved.FirstOrDefault(e =>
                    string.Equals(e.StageId, stage.StageId.Trim('{', '}'), StringComparison.OrdinalIgnoreCase));
            }

            if (match is null)
                continue;

            var usedSteps = new HashSet<string>(stage.Steps.Where(s => s.StepId is not null).Select(s => s.StepId!),
                StringComparer.OrdinalIgnoreCase);

            foreach (var step in stage.Steps.Where(s => s.StepId is null))
            {
                // A data step is the same step when it shows the same field; a button when it runs the
                // same process.
                var old = match.Source.Steps.FirstOrDefault(o =>
                    o.StepId is not null && !usedSteps.Contains(o.StepId)
                    && string.Equals(o.Kind, step.Kind, StringComparison.OrdinalIgnoreCase)
                    && (step.Kind == BpfStepKind.Field
                        ? string.Equals(o.Attribute, step.Attribute, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(o.ProcessId, step.ProcessId?.Trim('{', '}'), StringComparison.OrdinalIgnoreCase)));
                if (old is not null)
                {
                    step.StepId = old.StepId;
                    usedSteps.Add(old.StepId!);
                }
            }

            AdoptTriggerIds(stage.Workflows, match.Source.Workflows);
        }

        AdoptTriggerIds(definition.Workflows, existing.Workflows);
        return definition;
    }

    private static void AdoptTriggerIds(List<BpfWorkflowTrigger> triggers, List<BpfWorkflowTrigger> existing)
    {
        var used = new HashSet<string>(triggers.Where(t => t.TriggerId is not null).Select(t => t.TriggerId!),
            StringComparer.OrdinalIgnoreCase);

        foreach (var trigger in triggers.Where(t => t.TriggerId is null))
        {
            var old = existing.FirstOrDefault(o =>
                o.TriggerId is not null && !used.Contains(o.TriggerId)
                && string.Equals(o.WorkflowId, trigger.WorkflowId.Trim('{', '}'), StringComparison.OrdinalIgnoreCase)
                && string.Equals(o.On, trigger.On, StringComparison.OrdinalIgnoreCase));
            if (old is not null)
            {
                trigger.TriggerId = old.TriggerId;
                used.Add(old.TriggerId!);
            }
        }
    }

    private async Task<List<WorkflowValidationIssue>> RemovedStagesInUseAsync(
        string orgUrl, string uniqueName, BpfDefinition before, BpfDefinition after, CancellationToken ct)
    {
        var kept = after.Stages.Where(s => s.StageId is not null).Select(s => s.StageId!.Trim('{', '}'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = before.Stages.Where(s => s.StageId is not null && !kept.Contains(s.StageId)).ToList();
        var issues = new List<WorkflowValidationIssue>();
        if (removed.Count == 0)
            return issues;

        BpfInstanceTable table;
        try
        {
            table = await GetInstanceTableAsync(orgUrl, uniqueName, ct);
        }
        catch (InvalidOperationException)
        {
            return issues;
        }

        foreach (var stage in removed)
        {
            var raw = await client.GetRawAsync(orgUrl,
                $"api/data/v9.2/{table.EntitySetName}?$select=businessprocessflowinstanceid&$top=1"
                + $"&$filter=_activestageid_value eq {stage.StageId} and statecode eq 0", ct: ct);

            if (JsonDocument.Parse(raw).RootElement.GetProperty("value").GetArrayLength() > 0)
                issues.Add(new WorkflowValidationIssue("warning", "BPF060", "$.stages",
                    $"Stage '{stage.Name}' is removed, but active instances stand on it. They keep pointing at a "
                    + "stage that no longer exists.",
                    "Move those instances first (bpf_instance_move), or keep the stage."));
        }

        return issues;
    }

    private static string DescribeChange(BpfDefinition before, BpfDefinition after)
    {
        var sb = new StringBuilder();
        string Describe(BpfDefinition d) => d.Stages.Count == 0
            ? "(none)"
            : string.Join(" → ", d.Stages.Select(s => $"{s.Name} [{s.Steps.Count}]"));

        sb.AppendLine($"Stages now:   {Describe(before)}");
        sb.AppendLine($"Stages after: {Describe(after)}");

        var beforeIds = before.Stages.Where(s => s.StageId is not null).ToDictionary(s => s.StageId!, StringComparer.OrdinalIgnoreCase);
        var afterIds = after.Stages.Where(s => s.StageId is not null).Select(s => s.StageId!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var removed in beforeIds.Where(kv => !afterIds.Contains(kv.Key)))
            sb.AppendLine($"Removed stage: {removed.Value.Name} ({removed.Key})");
        foreach (var added in after.Stages.Where(s => s.StageId is null || !beforeIds.ContainsKey(s.StageId)))
            sb.AppendLine($"New stage: {added.Name}");

        return sb.ToString().TrimEnd();
    }

    private async Task<string> DeriveUniqueNameAsync(string orgUrl, string name, string? solution, CancellationToken ct)
    {
        var prefix = "new";
        if (!string.IsNullOrWhiteSpace(solution))
        {
            var raw = await client.GetRawAsync(orgUrl,
                $"api/data/v9.2/solutions?$select=uniquename&$filter=uniquename eq '{Escape(solution!)}'"
                + "&$expand=publisherid($select=customizationprefix)", ct: ct);
            var row = JsonDocument.Parse(raw).RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("publisherid", out var pub)
                && pub.GetStringOrNull("customizationprefix") is { Length: > 0 } p)
                prefix = p;
            else
                throw new InvalidOperationException($"Solution '{solution}' not found.");
        }

        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]", string.Empty);
        if (slug.Length == 0)
            slug = "process";
        return $"{prefix}_{slug}"[..Math.Min(prefix.Length + 1 + slug.Length, 40)];
    }

    private async Task<List<WorkflowValidationIssue>> CheckUniqueNameAsync(string orgUrl, string uniqueName, CancellationToken ct)
    {
        var issues = new List<WorkflowValidationIssue>();

        if (!Regex.IsMatch(uniqueName, "^[a-z][a-z0-9]{1,7}_[a-z0-9_]+$"))
        {
            issues.Add(new("error", "BPF009", "uniqueName",
                $"'{uniqueName}' is not a valid unique name.",
                "Use '<prefix>_<name>' in lowercase letters and digits, e.g. \"sample_leadtoorder\". It becomes "
                + "the logical name of the instance table."));
            return issues;
        }

        var raw = await client.GetRawAsync(orgUrl,
            $"api/data/v9.2/workflows?$select=workflowid,name&$filter=uniquename eq '{Escape(uniqueName)}'", ct: ct);
        var taken = JsonDocument.Parse(raw).RootElement.GetProperty("value").GetArrayLength() > 0;

        if (!taken)
        {
            try
            {
                await client.GetRawAsync(orgUrl,
                    $"api/data/v9.2/EntityDefinitions(LogicalName='{uniqueName}')?$select=LogicalName", ct: ct);
                taken = true;
            }
            catch (HttpRequestException)
            {
                // No table of that name — free.
            }
        }

        if (taken)
            issues.Add(new("error", "BPF009", "uniqueName", $"'{uniqueName}' is already in use.",
                "Pick another uniqueName; it has to be unique among processes and tables."));

        return issues;
    }

    private async Task<int> LanguageAsync(string orgUrl, CancellationToken ct)
    {
        if (_languageCode is { } cached)
            return cached;

        var raw = await client.GetRawAsync(orgUrl, "api/data/v9.2/organizations?$select=languagecode", ct: ct);
        var row = JsonDocument.Parse(raw).RootElement.GetProperty("value").EnumerateArray().First();
        _languageCode = row.GetInt32OrZero("languagecode") is var code and > 0 ? code : 1033;
        return _languageCode.Value;
    }

    private async Task<string> EntitySetAsync(string orgUrl, string entity, CancellationToken ct)
    {
        var raw = await client.GetRawAsync(orgUrl,
            $"api/data/v9.2/EntityDefinitions(LogicalName='{Uri.EscapeDataString(entity)}')?$select=EntitySetName", ct: ct);
        return JsonDocument.Parse(raw).RootElement.GetStringOrEmpty("EntitySetName");
    }

    private async Task<string?> StageNameAsync(string orgUrl, Guid stageId, CancellationToken ct)
    {
        try
        {
            var raw = await client.GetRawAsync(orgUrl, $"api/data/v9.2/processstages({stageId})?$select=stagename", ct: ct);
            return JsonDocument.Parse(raw).RootElement.GetStringOrNull("stagename");
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Stage ids along the main path from the first stage to <paramref name="target"/>.</summary>
    private static List<string>? PathTo(IReadOnlyList<BpfResolvedStage> stages, string target)
    {
        var path = new List<string>();
        var current = stages.FirstOrDefault();
        while (current is not null && path.Count <= stages.Count)
        {
            path.Add(current.StageId);
            if (string.Equals(current.StageId, target, StringComparison.OrdinalIgnoreCase))
                return path;
            current = stages.FirstOrDefault(s => string.Equals(s.StageId, current.NextStageId, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    /// <summary>Attributes of an entity as returned by a function, either as a key/value list or plain properties.</summary>
    private static Dictionary<string, string> Flatten(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var kv in element.EnumerateArray())
            {
                var key = kv.GetStringOrNull("key") ?? kv.GetStringOrNull("Key");
                if (key is null || !(kv.TryGetProperty("value", out var v) || kv.TryGetProperty("Value", out v)))
                    continue;
                result[key] = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString()!,
                    JsonValueKind.Object when v.TryGetProperty("Id", out var id) => id.ToString(),
                    JsonValueKind.Object when v.TryGetProperty("Value", out var inner) => inner.ToString(),
                    _ => v.ToString()
                };
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                var key = p.Name.StartsWith('_') && p.Name.EndsWith("_value", StringComparison.Ordinal)
                    ? p.Name[1..^6]
                    : p.Name;
                if (!key.Contains('@'))
                    result[key] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.ToString();
            }
        }

        return result;
    }

    private static Guid? GuidOf(Dictionary<string, string> attributes, string key) =>
        attributes.TryGetValue(key, out var v) && Guid.TryParse(v, out var g) ? g : null;

    private static string StatusName(string? statusCode) => statusCode switch
    {
        "1" => "active",
        "2" => "finished",
        "3" => "aborted",
        _ => statusCode ?? "unknown"
    };

    private static int? NullableInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;

    private static string Escape(string value) => value.Replace("'", "''");
}
