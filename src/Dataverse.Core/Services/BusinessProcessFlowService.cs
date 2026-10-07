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
/// <param name="Stages">
/// The stages as written (or as they would be written), with their ids. On a dry run, stages and steps
/// that are new have no id yet: it is assigned by the real write.
/// </param>
/// <param name="Backup">The previous XAML, to undo the change with <c>bpf_restore_xaml</c>. Not on a dry run.</param>
/// <param name="Diff">What the write changes (or would change), one line per change.</param>
public sealed record BpfSaveResult(
    bool Applied,
    Guid ProcessId,
    IReadOnlyList<BpfBuiltStage> Stages,
    WorkflowValidationResult Validation,
    string? Backup,
    string? Message,
    IReadOnlyList<string>? Diff = null);

/// <summary>A definition checked against a process it is meant to replace.</summary>
/// <param name="Definition">The definition with the existing ids adopted and relationships completed.</param>
public sealed record BpfWritePlan(
    BpfDetail Detail,
    BpfParseResult Current,
    WorkflowValidationResult Validation,
    BpfDefinition Definition,
    BpfCatalog Catalog);

/// <param name="Created">False when the record already had an instance of the process.</param>
public sealed record BpfStartResult(Guid InstanceId, bool Created, string? Message);

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
    /// <remarks>
    /// The metadata checks run even when the model rules already found errors, so that one call reports
    /// everything there is to fix — only a definition without table or stages stops early.
    /// </remarks>
    public async Task<(WorkflowValidationResult Result, BpfDefinition Definition, BpfCatalog Fields)> ValidateAsync(
        string orgUrl, BpfDefinition definition, CancellationToken ct = default)
    {
        var model = BpfDefinitionValidator.Validate(definition);
        if (string.IsNullOrWhiteSpace(definition.PrimaryEntity) || definition.Stages.Count == 0)
            return (model, definition, BpfCatalog.Empty);

        var (issues, completed, fields) = await ValidateAgainstMetadataAsync(orgUrl, definition, ct);
        var modelIssues = await WithRelationshipCandidatesAsync(orgUrl, definition, model.Issues, ct);
        var all = modelIssues.Concat(issues).ToList();
        return (new WorkflowValidationResult(all.All(i => i.Severity != "error"), all), completed, fields);
    }

    /// <summary>
    /// The 1:N relationships a stage on <paramref name="toEntity"/> can be reached through from
    /// <paramref name="fromEntity"/>: those whose lookup sits on <paramref name="toEntity"/>.
    /// </summary>
    public async Task<IReadOnlyList<BpfRelationshipCandidate>> FindRelationshipsAsync(
        string orgUrl, string fromEntity, string toEntity, CancellationToken ct = default)
    {
        string raw;
        try
        {
            raw = await client.GetRawAsync(orgUrl,
                $"api/data/v9.2/EntityDefinitions(LogicalName='{Uri.EscapeDataString(toEntity)}')/ManyToOneRelationships"
                + "?$select=SchemaName,ReferencedEntity,ReferencingAttribute", ct: ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"Table '{toEntity}' does not exist.");
        }

        return JsonDocument.Parse(raw).RootElement.GetProperty("value").EnumerateArray()
            .Where(r => string.Equals(r.GetStringOrNull("ReferencedEntity"), fromEntity, StringComparison.OrdinalIgnoreCase))
            .Select(r => new BpfRelationshipCandidate(
                r.GetStringOrEmpty("SchemaName"), r.GetStringOrEmpty("ReferencingAttribute"), fromEntity, toEntity))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Puts the candidate relationships into the fix of every missing-relationship error.</summary>
    private async Task<IReadOnlyList<WorkflowValidationIssue>> WithRelationshipCandidatesAsync(
        string orgUrl, BpfDefinition definition, IReadOnlyList<WorkflowValidationIssue> issues, CancellationToken ct)
    {
        if (issues.All(i => i.Code != "BPF040"))
            return issues;

        var resolved = BpfStageResolver.Resolve(definition, assignMissingIds: false).Stages;
        var result = new List<WorkflowValidationIssue>();

        foreach (var issue in issues)
        {
            var match = Regex.Match(issue.Path, @"^\$\.stages\[(\d+)\]\.relationship$");
            if (issue.Code != "BPF040" || !match.Success)
            {
                result.Add(issue);
                continue;
            }

            var stage = resolved[int.Parse(match.Groups[1].Value)];
            var from = stage.Predecessors.Select(p => resolved[p])
                .FirstOrDefault(p => !string.Equals(p.Entity, stage.Entity, StringComparison.OrdinalIgnoreCase));
            if (from is null)
            {
                result.Add(issue);
                continue;
            }

            try
            {
                result.Add(issue with { Fix = issue.Fix + " " + DescribeCandidates(await FindRelationshipsAsync(orgUrl, from.Entity, stage.Entity, ct), from.Entity, stage.Entity) });
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                result.Add(issue);
            }
        }

        return result;
    }

    private static string DescribeCandidates(IReadOnlyList<BpfRelationshipCandidate> candidates, string from, string to) =>
        candidates.Count == 0
            ? $"There is no 1:N relationship from {from} to {to} — no lookup on {to} points at {from}. Create one, or route the process through a table that has it."
            : $"Relationships from {from} to {to}: "
              + string.Join(", ", candidates.Take(15).Select(c => $"{c.Name} (lookup {c.Attribute})"))
              + (candidates.Count > 15 ? $", and {candidates.Count - 15} more (bpf_find_relationships)." : ".");

    private async Task<(List<WorkflowValidationIssue> Issues, BpfDefinition Definition, BpfCatalog Fields)>
        ValidateAgainstMetadataAsync(string orgUrl, BpfDefinition definition, CancellationToken ct)
    {
        var issues = new List<WorkflowValidationIssue>();
        var catalog = new BpfCatalog();
        var resolved = BpfStageResolver.Resolve(definition, assignMissingIds: false);
        var language = definition.LanguageCode ?? await LanguageAsync(orgUrl, ct);

        foreach (var entity in resolved.Stages.Select(s => s.Entity).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (exists, enabled, attributes) = await ReadTableAsync(orgUrl, entity, language, ct);
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
        var literals = new List<(string Entity, WorkflowCondition Condition, BpfFieldInfo Info, string Path)>();

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

        // Literals must fit the column: the branch check compares strictly, so "1" never equals the
        // choice value 1. A missing dataType is taken from the column.
        var completedConditions = new Dictionary<WorkflowCondition, WorkflowCondition>(ReferenceEqualityComparer.Instance);
        foreach (var (entity, condition, info, path) in literals)
            if (await CheckLiteralAsync(orgUrl, entity, condition, info, path, issues, ct) is { } completedCondition)
                completedConditions[condition] = completedCondition;

        if (completedConditions.Count > 0)
            for (var i = 0; i < stages.Count; i++)
                if (stages[i].Branch is { } branching)
                    stages[i] = stages[i] with
                    {
                        Branch = branching with
                        {
                            Branches = branching.Branches
                                .Select(b => b with { Conditions = Replace(b.Conditions, completedConditions) }).ToList()
                        }
                    };

        return (issues, definition with { Stages = stages }, catalog);

        static List<WorkflowCondition> Replace(List<WorkflowCondition> conditions, Dictionary<WorkflowCondition, WorkflowCondition> map) =>
            conditions.Select(c => map.TryGetValue(c, out var replaced)
                ? replaced
                : c.IsGroup ? c with { Conditions = Replace(c.Conditions!, map) } : c).ToList();

        BpfFieldInfo? CheckAttribute(string entity, string attribute, string path, bool displayable)
        {
            if (!catalog.Knows(entity) || catalog.IsMissing(entity))
                return null;

            var info = catalog.Find(entity, attribute);
            if (info is null)
            {
                var hint = catalog.AttributesOf(entity)
                    .Where(a => a.Contains(attribute, StringComparison.OrdinalIgnoreCase)
                                || attribute.Contains(a, StringComparison.OrdinalIgnoreCase))
                    .Take(5).ToList();
                issues.Add(new("error", "BPF301", path, $"Table '{entity}' has no attribute '{attribute}'.",
                    hint.Count > 0
                        ? $"Did you mean: {string.Join(", ", hint)}? table_get lists all columns of the table."
                        : "Look up the logical name with table_get, which lists all columns of the table."));
                return null;
            }

            if (displayable && BpfControlClass.Unsupported.Contains(info.AttributeType))
                issues.Add(new("error", "BPF302", path,
                    $"'{entity}.{attribute}' is of type {info.AttributeType}, which a data step cannot show.",
                    "Pick a column of a type the form can edit (text, number, date, choice, lookup, …)."));

            return info;
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

                if (!string.IsNullOrWhiteSpace(condition.Attribute)
                    && CheckAttribute(entity, condition.Attribute, $"{path}[{c}].attribute", displayable: false) is { } info
                    && condition.Value is { Kind: WorkflowValueKind.Literal })
                    literals.Add((entity, condition, info, $"{path}[{c}]"));

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

            if (kind == BpfStepKind.Flow && IsAutomatedTrigger(info.FlowTrigger))
                issues.Add(new("warning", "BPF311", path,
                    $"'{info.Name}' starts on its own (trigger {info.FlowTrigger}); a flow step's button cannot run it.",
                    "Use an instant flow — a manual trigger, or the Dataverse trigger for flow steps run from a "
                    + "business process flow. flow_get_clientdata shows a flow's trigger."));

            if (kind != BpfStepKind.Flow
                && !string.Equals(info.PrimaryEntity, entity, StringComparison.OrdinalIgnoreCase))
                issues.Add(new("error", "BPF310", path,
                    $"'{info.Name}' runs on '{info.PrimaryEntity}', but is used on '{entity}'.",
                    $"Use a {(kind == "trigger" ? "workflow" : "process")} whose primary table is '{entity}'."));
        }
    }

    /// <summary>
    /// Checks a literal against the column it is compared with. Returns the comparison with its
    /// dataType filled in when the caller left it out, or null when nothing changes.
    /// </summary>
    private async Task<WorkflowCondition?> CheckLiteralAsync(
        string orgUrl, string entity, WorkflowCondition condition, BpfFieldInfo info, string path,
        List<WorkflowValidationIssue> issues, CancellationToken ct)
    {
        var value = condition.Value!;
        var expected = ExpectedDataType(info.AttributeType);
        if (expected is null)
            return null;

        var column = $"{entity}.{condition.Attribute}";
        if (value.DataType is not null && WorkflowXamlBuilder.CrmPropertyType(value.DataType) != expected)
        {
            issues.Add(new("error", "BPF306", $"{path}.value.dataType",
                $"'{column}' is a {info.AttributeType} column, but the value is declared as {value.DataType}. "
                + "The branch would compare values of different types and never apply.",
                $"Use \"dataType\": \"{expected}\", or leave dataType out — it is then taken from the column. {LiteralExample(expected)}"));
            return null;
        }

        var literal = value.Literal ?? string.Empty;
        var options = expected == "OptionSetValue" ? await ReadOptionsAsync(orgUrl, entity, condition.Attribute, info.AttributeType, ct) : null;

        if (!LiteralFits(expected, literal))
        {
            issues.Add(new("error", "BPF306", $"{path}.value.literal",
                $"'{literal}' is not a valid {expected} value for '{column}' ({info.AttributeType}).",
                expected == "OptionSetValue" && options is not null ? OptionHint(literal, options) : LiteralExample(expected)));
            return null;
        }

        if (options is not null && int.TryParse(literal, out var number) && !options.ContainsKey(number))
            issues.Add(new("warning", "BPF312", $"{path}.value.literal",
                $"{literal} is not an option of '{column}'; the branch can never apply.",
                OptionHint(literal, options)));

        return value.DataType is null ? condition with { Value = value with { DataType = expected } } : null;
    }

    /// <summary>The literal data type a column is compared with; null for types not checked.</summary>
    public static string? ExpectedDataType(string attributeType) => attributeType switch
    {
        "Picklist" or "State" or "Status" => "OptionSetValue",
        "Boolean" => "Boolean",
        "Lookup" or "Customer" or "Owner" => "EntityReference",
        "Integer" or "BigInt" => "Integer",
        "Decimal" => "Decimal",
        "Double" => "Double",
        "Money" => "Money",
        "DateTime" => "DateTime",
        "String" or "Memo" => "String",
        "Uniqueidentifier" => "Guid",
        _ => null
    };

    public static bool LiteralFits(string dataType, string literal)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        return dataType switch
        {
            "OptionSetValue" or "Integer" => int.TryParse(literal, System.Globalization.NumberStyles.Integer, invariant, out _),
            "Decimal" or "Double" or "Money" => decimal.TryParse(literal, System.Globalization.NumberStyles.Number, invariant, out _),
            "Boolean" => literal.ToLowerInvariant() is "true" or "false" or "1" or "0",
            "DateTime" => DateTime.TryParse(literal, invariant, System.Globalization.DateTimeStyles.None, out _),
            "Guid" => Guid.TryParse(literal, out _),
            "EntityReference" => literal.Split(':', 3) is { Length: >= 2 } parts
                                 && parts[0].Length > 0 && Guid.TryParse(parts[1], out _),
            _ => true
        };
    }

    private static string LiteralExample(string dataType) => dataType switch
    {
        "OptionSetValue" => "A choice is compared by the option's number: {\"kind\":\"literal\",\"dataType\":\"OptionSetValue\",\"literal\":\"1\"}.",
        "Boolean" => "A yes/no column takes \"true\" or \"false\" (\"1\"/\"0\" work too).",
        "EntityReference" => "A lookup takes \"<table>:<guid>:<label>\", e.g. \"account:<guid>:Contoso\"; the label is shown in the designer.",
        "Integer" => "A whole number, e.g. \"50\".",
        "Decimal" or "Double" or "Money" => "A number with a dot as decimal separator, e.g. \"10000.50\".",
        "DateTime" => "A date as yyyy-MM-dd, e.g. \"2024-01-31\".",
        "Guid" => "A GUID.",
        _ => "Any text."
    };

    private static string OptionHint(string literal, IReadOnlyDictionary<int, string> options)
    {
        var byLabel = options.Where(o => string.Equals(o.Value, literal, StringComparison.OrdinalIgnoreCase)).ToList();
        var listed = string.Join(", ", options.Take(30).Select(o => $"{o.Key} = {o.Value}")) + (options.Count > 30 ? ", …" : "");
        return (byLabel.Count > 0 ? $"Did you mean {byLabel[0].Key} ({byLabel[0].Value})? " : string.Empty)
               + "Compare with the option's number, e.g. {\"kind\":\"literal\",\"dataType\":\"OptionSetValue\",\"literal\":\""
               + (byLabel.Count > 0 ? byLabel[0].Key : options.Keys.FirstOrDefault()) + $"\"}}. Options: {listed}.";
    }

    /// <summary>Value → label of a choice column, or null when they cannot be read.</summary>
    private async Task<IReadOnlyDictionary<int, string>?> ReadOptionsAsync(
        string orgUrl, string entity, string attribute, string attributeType, CancellationToken ct)
    {
        var cast = attributeType switch
        {
            "State" => "StateAttributeMetadata",
            "Status" => "StatusAttributeMetadata",
            _ => "PicklistAttributeMetadata"
        };

        try
        {
            var raw = await client.GetRawAsync(orgUrl,
                $"api/data/v9.2/EntityDefinitions(LogicalName='{Uri.EscapeDataString(entity)}')/Attributes(LogicalName='{Uri.EscapeDataString(attribute)}')"
                + $"/Microsoft.Dynamics.CRM.{cast}?$select=LogicalName&$expand=OptionSet($select=Options)", ct: ct);

            var options = new SortedDictionary<int, string>();
            var root = JsonDocument.Parse(raw).RootElement;
            if (!root.TryGetProperty("OptionSet", out var set) || set.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var option in set.GetProperty("Options").EnumerateArray())
            {
                if (!option.TryGetProperty("Value", out var v) || v.ValueKind != JsonValueKind.Number)
                    continue;
                options[v.GetInt32()] = LabelOf(option, "Label", null) ?? v.GetInt32().ToString();
            }

            return options;
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "Could not read the options of {Entity}.{Attribute}", entity, attribute);
            return null;
        }
    }

    /// <summary>A label in the given language, else the user's.</summary>
    private static string? LabelOf(JsonElement element, string property, int? language)
    {
        if (!element.TryGetProperty(property, out var label) || label.ValueKind != JsonValueKind.Object)
            return null;

        if (language is { } code && label.TryGetProperty("LocalizedLabels", out var all) && all.ValueKind == JsonValueKind.Array)
            foreach (var localized in all.EnumerateArray())
                if (localized.GetInt32OrZero("LanguageCode") == code && localized.GetStringOrNull("Label") is { } text)
                    return text;

        return label.TryGetProperty("UserLocalizedLabel", out var user) && user.ValueKind == JsonValueKind.Object
            ? user.GetStringOrNull("Label")
            : null;
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

            // A flow's trigger decides whether a button can start it; it sits in the definition.
            string? trigger = null;
            if (r.GetInt32OrZero("category") == 5)
            {
                var flowRaw = await client.GetRawAsync(orgUrl, $"api/data/v9.2/workflows({id})?$select=clientdata", ct: ct);
                trigger = FlowTriggerOf(JsonDocument.Parse(flowRaw).RootElement.GetStringOrNull("clientdata"));
            }

            return new BpfProcessInfo(
                id,
                r.GetStringOrEmpty("name"),
                uniqueName,
                r.GetInt32OrZero("category"),
                r.GetStringOrNull("primaryentity"),
                r.GetInt32OrZero("statecode") == 1,
                r.TryGetProperty("ondemand", out var od) && od.ValueKind == JsonValueKind.True,
                trigger);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>"type", "type/kind" or "type/operationId" of a flow's (first) trigger.</summary>
    public static string? FlowTriggerOf(string? clientData)
    {
        if (string.IsNullOrWhiteSpace(clientData))
            return null;

        try
        {
            var root = JsonDocument.Parse(clientData).RootElement;
            if (!root.TryGetProperty("properties", out var properties)
                || !properties.TryGetProperty("definition", out var definition)
                || !definition.TryGetProperty("triggers", out var triggers)
                || triggers.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var t in triggers.EnumerateObject())
            {
                var type = t.Value.GetStringOrNull("type") ?? "?";
                if (t.Value.GetStringOrNull("kind") is { } kind)
                    return $"{type}/{kind}";
                if (t.Value.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Object
                    && inputs.TryGetProperty("host", out var host) && host.ValueKind == JsonValueKind.Object
                    && host.GetStringOrNull("operationId") is { } operation)
                    return $"{type}/{operation}";
                return type;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>
    /// Triggers that start a flow on their own — on a schedule, a row change, an e-mail, an HTTP call.
    /// A flow step's button cannot start such a flow.
    /// </summary>
    public static bool IsAutomatedTrigger(string? trigger) =>
        trigger is not null
        && (trigger == "Recurrence"
            || trigger.EndsWith("/SubscribeWebhookTrigger", StringComparison.Ordinal)
            || trigger.EndsWith("/BusinessEventsTrigger", StringComparison.Ordinal)
            || trigger.Contains("OnNewEmail", StringComparison.Ordinal)
            || trigger is "Request/Http" or "Request/Skills" or "Request/VirtualAgent" or "Request/ApiConnection");

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
        string orgUrl, string entity, int language, CancellationToken ct)
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

                // The label goes into the XAML tagged with the process language, so it has to be in it.
                attributes[name] = new BpfFieldInfo(a.GetStringOrEmpty("AttributeType"), LabelOf(a, "DisplayName", language));
            }

            return (true, enabled, attributes);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogDebug(ex, "Table {Entity} not found", entity);
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
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            issues.Add(new("error", "BPF303", $"{path}.name", $"There is no relationship '{rel.Name}'.",
                "Use the schema name of a 1:N relationship. "
                + (fromEntity is null
                    ? "bpf_find_relationships lists them for two tables."
                    : await CandidatesTextAsync(fromEntity, toEntity))));
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
                + "table must hold the lookup to the table the process comes from. "
                + (fromEntity is null ? string.Empty : await CandidatesTextAsync(fromEntity, toEntity))));
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

        async Task<string> CandidatesTextAsync(string from, string to)
        {
            try
            {
                return DescribeCandidates(await FindRelationshipsAsync(orgUrl, from, to, ct), from, to);
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException)
            {
                return "bpf_find_relationships lists the candidates.";
            }
        }
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
            try
            {
                await SetStateAsync(orgUrl, id, true, ct);
                message = $"Created and activated. Instances are stored in table '{uniqueName}'.";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // The process exists either way; losing its id here would leave an orphan behind.
                logger.LogWarning(ex, "Activation of business process flow {Id} failed", id);
                message = $"Created as a draft, but the activation failed: {ex.Message} — fix the cause and "
                          + "activate it with bpf_set_state, or remove it with bpf_delete.";
            }
        }
        else
        {
            message = "Created as a draft. Activate it with bpf_set_state; the first activation creates the "
                      + $"instance table '{uniqueName}' and takes about two minutes.";
        }

        return (new BpfSaveResult(true, id, build.Stages, validation, null, message), uniqueName);
    }

    /// <summary>
    /// Checks a definition as a replacement for an existing process, without writing: adopts the ids of
    /// stages and steps it keeps, then validates — including what the change does to running instances.
    /// </summary>
    /// <param name="allowStageRemoval">
    /// Accept removing stages that active instances stand on (reported as a warning instead of an error).
    /// </param>
    public async Task<BpfWritePlan> PlanWriteAsync(
        string orgUrl, Guid processId, BpfDefinition definition, bool allowStageRemoval = false, CancellationToken ct = default)
    {
        var (detail, current) = await GetDefinitionAsync(orgUrl, processId, ct);

        if (detail.BusinessProcessType != 0)
            throw new InvalidOperationException("This is a task flow; only business process flows can be written.");

        if (string.IsNullOrWhiteSpace(definition.PrimaryEntity))
            definition = definition with { PrimaryEntity = detail.PrimaryEntity ?? string.Empty };

        definition = AdoptExistingIds(definition, current.Definition);

        var (validation, completed, catalog) = await ValidateAsync(orgUrl, definition, ct);
        var issues = validation.Issues.ToList();

        if (!string.Equals(completed.PrimaryEntity, detail.PrimaryEntity, StringComparison.OrdinalIgnoreCase))
            issues.Add(new WorkflowValidationIssue("error", "BPF008", "$.primaryEntity",
                $"The process runs on '{detail.PrimaryEntity}'; its primary table cannot change.",
                "Keep primaryEntity, or create a new process with bpf_create."));

        if (!current.FullyUnderstood)
            issues.Add(new WorkflowValidationIssue("warning", "BPF061", "$",
                "The current process contains parts this server does not understand; writing drops them: "
                + string.Join("; ", current.Unrecognised),
                "Check the list. Run with dryRun=true first and keep the backup."));

        issues.AddRange(LooksRenamed(current.Definition, completed));

        // Instances standing on a stage that disappears lose their place in the process — whatever the
        // process's state: a deactivated process keeps its instances.
        if (detail.UniqueName is not null)
            issues.AddRange(await RemovedStagesInUseAsync(orgUrl, detail.UniqueName, current.Definition, completed, allowStageRemoval, ct));

        return new BpfWritePlan(detail, current,
            new WorkflowValidationResult(issues.All(x => x.Severity != "error"), issues), completed, catalog);
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
        string orgUrl, Guid processId, BpfDefinition definition, bool dryRun = false, bool allowStageRemoval = false,
        CancellationToken ct = default)
    {
        var plan = await PlanWriteAsync(orgUrl, processId, definition, allowStageRemoval, ct);
        var detail = plan.Detail;

        if (!plan.Validation.CanSave)
            return new BpfSaveResult(false, processId, [], plan.Validation, null,
                $"Nothing was written: {plan.Validation.ErrorCount} error(s) must be fixed first.");

        // Described before building: the builder gives new stages their ids.
        var diff = DescribeChange(plan.Current.Definition, plan.Definition);
        var newStages = plan.Definition.Stages.Where(st => st.StageId is null).ToHashSet(ReferenceEqualityComparer.Instance);
        var newSteps = plan.Definition.Stages.SelectMany(st => st.Steps).Where(st => st.StepId is null)
            .Select(st => (object)st).ToHashSet(ReferenceEqualityComparer.Instance);

        var language = await LanguageAsync(orgUrl, ct);
        var build = BpfXamlBuilder.Build(plan.Definition, processId, plan.Catalog, language);

        if (dryRun)
        {
            // The ids a real write assigns are new random ones; showing these would suggest otherwise.
            var stages = build.Stages.Select((built, n) => built with
            {
                StageId = newStages.Contains(plan.Definition.Stages[n]) ? null : built.StageId,
                StepIds = plan.Definition.Stages[n].Steps.Select(st => newSteps.Contains(st) ? null : st.StepId).ToList()
            }).ToList();

            return new BpfSaveResult(false, processId, stages, plan.Validation, null,
                "Dry run — nothing was written. New stages and steps get their ids on the real write.", diff);
        }

        await client.PatchAsync(orgUrl, $"api/data/v9.2/workflows({processId})",
            new Dictionary<string, object?> { ["xaml"] = build.Xaml }, ct);

        logger.LogInformation("Wrote definition of business process flow {Id} ({Stages} stages)", processId, build.Stages.Count);
        return new BpfSaveResult(true, processId, build.Stages, plan.Validation, detail.Xaml,
            detail.StateCode == 1 ? "Written; the process stayed active." : "Written.", diff);
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
        // Task flows are not applied to records, so they take no part in the order.
        var existing = await ListAsync(orgUrl, primaryEntity, includeTaskFlows: false, ct);
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

        return await ListAsync(orgUrl, primaryEntity, includeTaskFlows: false, ct);
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
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
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
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Table '{uniqueName}' does not exist. A process gets its instance table on its first activation.");
        }

        var r = JsonDocument.Parse(raw).RootElement;
        var lookups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rel in r.GetProperty("ManyToOneRelationships").EnumerateArray())
        {
            var attribute = rel.GetStringOrEmpty("ReferencingAttribute");
            if (attribute.StartsWith("bpf_", StringComparison.OrdinalIgnoreCase)
                && attribute.EndsWith("id", StringComparison.OrdinalIgnoreCase)
                && rel.GetStringOrNull("ReferencedEntity") is { } referenced)
            {
                lookups[referenced] = rel.GetStringOrNull("ReferencingEntityNavigationPropertyName") ?? attribute;
                columns[referenced] = attribute;
            }
        }

        return new BpfInstanceTable(r.GetStringOrEmpty("LogicalName"), r.GetStringOrEmpty("EntitySetName"), lookups, columns);
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

    /// <summary>Starts a process on a record.</summary>
    /// <param name="stageId">Stage to start on; the first stage when omitted.</param>
    /// <remarks>
    /// A record holds at most one instance per process. When it already has one, the platform accepts
    /// a second create and changes nothing — so the existing one is looked up first and returned.
    /// </remarks>
    public async Task<BpfStartResult> StartInstanceAsync(
        string orgUrl, Guid processId, Guid recordId, Guid? stageId = null, CancellationToken ct = default)
    {
        var (detail, parsed) = await GetDefinitionAsync(orgUrl, processId, ct);
        var table = await GetInstanceTableAsync(orgUrl, detail.UniqueName ?? string.Empty, ct);
        var entity = detail.PrimaryEntity ?? string.Empty;

        if (!table.RecordLookups.TryGetValue(entity, out var navigation))
            throw new InvalidOperationException($"The instance table has no lookup to '{entity}'.");

        if (table.RecordColumns?.GetValueOrDefault(entity) is { } column)
        {
            var existingRaw = await client.GetRawAsync(orgUrl,
                $"api/data/v9.2/{table.EntitySetName}?$select=businessprocessflowinstanceid,statuscode,_activestageid_value"
                + $"&$filter=_{column}_value eq {recordId}&$top=1", ct: ct);
            var existing = JsonDocument.Parse(existingRaw).RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();

            if (existing.ValueKind == JsonValueKind.Object)
            {
                var stageName = Guid.TryParse(existing.GetStringOrNull("_activestageid_value"), out var active)
                    ? await StageNameAsync(orgUrl, active, ct)
                    : null;
                var status = StatusName(existing.TryGetProperty("statuscode", out var sc) ? sc.ToString() : null);

                return new BpfStartResult(existing.TryGetGuid("businessprocessflowinstanceid"), false,
                    $"The record already has an instance of this process (stage '{stageName ?? "?"}', {status}); "
                    + "a record holds one per process, so nothing was started. Move it with bpf_instance_move"
                    + (status == "active" ? "." : ", after reactivating it with bpf_instance_set_status."));
            }
        }

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
        {
            var path = PathTo(stages, target)
                       ?? throw new InvalidOperationException(
                           $"Stage {target} is not on the main path (the 'next' chain from the first stage). Start at "
                           + "the first stage and move the instance there with bpf_instance_move.");
            if (path.Any(id => !string.Equals(stages.First(st => st.StageId == id).Entity, entity, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "The way to that stage leaves the primary table; start at the first stage and move the instance "
                    + "with bpf_instance_move, which takes the record of the other table.");
            body["traversedpath"] = string.Join(",", path);
        }

        var id = await client.PostForIdAsync(orgUrl, $"api/data/v9.2/{table.EntitySetName}", body,
            "businessprocessflowinstanceid", ct);
        return new BpfStartResult(id, true, null);
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
            $"api/data/v9.2/{table.EntitySetName}({instanceId})?$select=traversedpath,_activestageid_value,statecode,statuscode", ct: ct);
        var row = JsonDocument.Parse(raw).RootElement;

        if (row.GetInt32OrZero("statecode") != 0)
            throw new InvalidOperationException(
                $"The instance is {StatusName(row.TryGetProperty("statuscode", out var sc) ? sc.ToString() : null)}; only an "
                + "active one can move. Reactivate it with bpf_instance_set_status status='active' first.");
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

        if (statusCode == 2)
        {
            // The platform refuses to finish anywhere but at the end of a path, with a bare error.
            var raw = await client.GetRawAsync(orgUrl,
                $"api/data/v9.2/{table.EntitySetName}({instanceId})?$select=_activestageid_value", ct: ct);
            var active = JsonDocument.Parse(raw).RootElement.GetStringOrNull("_activestageid_value");
            var resolved = BpfStageResolver.Resolve(
                BpfXamlParser.Parse(detail.Xaml, detail.PrimaryEntity ?? string.Empty).Definition, assignMissingIds: false);
            var stage = resolved.Stages.FirstOrDefault(st => string.Equals(st.StageId, active, StringComparison.OrdinalIgnoreCase));

            if (stage is not null && (stage.NextStageId is not null || BpfStageResolver.BranchTargets(stage.Source).Any()))
                throw new InvalidOperationException(
                    $"The instance stands on '{stage.Source.Name}', which leads on to other stages; only an instance on "
                    + "the last stage of its path can be finished. Move it there with bpf_instance_move, or use 'aborted'.");
        }

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
        string orgUrl, string uniqueName, BpfDefinition before, BpfDefinition after, bool allowStageRemoval, CancellationToken ct)
    {
        var kept = after.Stages.Where(st => st.StageId is not null).Select(st => st.StageId!.Trim('{', '}'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = before.Stages.Where(st => st.StageId is not null && !kept.Contains(st.StageId)).ToList();
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
            return issues;   // never activated: no instances
        }

        foreach (var stage in removed)
        {
            var raw = await client.GetRawAsync(orgUrl,
                $"api/data/v9.2/{table.EntitySetName}?$select=businessprocessflowinstanceid&$top=5000"
                + $"&$filter=_activestageid_value eq {stage.StageId} and statecode eq 0", ct: ct);
            var count = JsonDocument.Parse(raw).RootElement.GetProperty("value").GetArrayLength();
            if (count == 0)
                continue;

            issues.Add(new WorkflowValidationIssue(allowStageRemoval ? "warning" : "error", "BPF060", "$.stages",
                $"Stage '{stage.Name}' ({stage.StageId}) is removed, but {count} active instance(s) stand on it. "
                + "They would keep pointing at a stage that no longer exists.",
                "If the stage was renamed or changed, keep its 'stageId'. Otherwise move those instances first "
                + "(bpf_instance_move), or pass allowStageRemoval=true to remove it anyway."));
        }

        return issues;
    }

    /// <summary>
    /// A stage that disappears while a new one appears on the same table, at the same position or as
    /// the only candidate, is most likely the same stage renamed without its id.
    /// </summary>
    public static IEnumerable<WorkflowValidationIssue> LooksRenamed(BpfDefinition before, BpfDefinition after)
    {
        var beforeResolved = BpfStageResolver.Resolve(before, assignMissingIds: false).Stages;
        var afterResolved = BpfStageResolver.Resolve(after, assignMissingIds: false).Stages;
        var kept = after.Stages.Where(st => st.StageId is not null).Select(st => st.StageId!.Trim('{', '}'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = beforeResolved.Where(st => !string.IsNullOrEmpty(st.StageId) && !kept.Contains(st.StageId)).ToList();
        var added = afterResolved.Where(st => st.Source.StageId is null).ToList();

        foreach (var old in removed)
        {
            var sameTable = added.Where(n => string.Equals(n.Entity, old.Entity, StringComparison.OrdinalIgnoreCase)).ToList();
            var candidate = sameTable.FirstOrDefault(n => n.Index == old.Index) ?? (sameTable.Count == 1 ? sameTable[0] : null);
            if (candidate is null)
                continue;

            yield return new WorkflowValidationIssue("warning", "BPF062", $"$.stages[{candidate.Index}].stageId",
                $"Stage '{old.Source.Name}' is removed and '{candidate.Source.Name}' is new, both on '{old.Entity}'. "
                + "If this is a rename, the stage gets a new id and instances on it lose their place.",
                $"To rename, keep the id: set \"stageId\": \"{old.StageId}\" on '{candidate.Source.Name}'.");
        }
    }

    /// <summary>One line per change between two definitions, matched by stage and step id.</summary>
    public static IReadOnlyList<string> DescribeChange(BpfDefinition before, BpfDefinition after)
    {
        var lines = new List<string>();
        var beforeResolved = BpfStageResolver.Resolve(before, assignMissingIds: false).Stages;
        var afterResolved = BpfStageResolver.Resolve(after, assignMissingIds: false).Stages;

        static string Id(string? id) => id?.Trim('{', '}').ToLowerInvariant() ?? string.Empty;
        string NameOf(IReadOnlyList<BpfResolvedStage> stages, string? id) =>
            id is null ? "end" : stages.FirstOrDefault(st => Id(st.StageId) == Id(id))?.Source.Name ?? id;
        string Targets(IReadOnlyList<BpfResolvedStage> stages, BpfResolvedStage stage) =>
            string.Join(", ", BpfStageResolver.BranchTargets(stage.Source)
                .Select(t => BpfStageResolver.Find(stages.Select(x => x.Source).ToList(), t)?.Name ?? t));

        var old = beforeResolved.Where(st => !string.IsNullOrEmpty(st.StageId)).ToDictionary(st => Id(st.StageId));
        var matched = new HashSet<string>();

        foreach (var stage in afterResolved)
        {
            var name = stage.Source.Name;
            if (stage.Source.StageId is null || !old.TryGetValue(Id(stage.StageId), out var was))
            {
                lines.Add($"+ stage '{name}' on {stage.Entity} ({stage.Source.Steps.Count} steps)");
                continue;
            }

            matched.Add(Id(stage.StageId));
            if (was.Source.Name != name)
                lines.Add($"~ stage '{was.Source.Name}' renamed to '{name}'");
            if (!string.Equals(was.Entity, stage.Entity, StringComparison.OrdinalIgnoreCase))
                lines.Add($"~ stage '{name}': table {was.Entity} → {stage.Entity}");
            if (BpfStageCategory.ToNumber(was.Source.Category) != BpfStageCategory.ToNumber(stage.Source.Category))
                lines.Add($"~ stage '{name}': category {was.Source.Category ?? "none"} → {stage.Source.Category ?? "none"}");

            // Compared by stage id, so that renaming the next stage is not reported here as well; a new
            // next stage has no id yet and counts by name.
            var nextStage = BpfStageResolver.NextOf(after.Stages, stage.Index);
            if (Target(before, was.NextStageId) != Target(after, nextStage?.StageId ?? nextStage?.Name))
                lines.Add($"~ stage '{name}': next {NameOf(beforeResolved, was.NextStageId)} → {nextStage?.Name ?? "end"}");

            if (Json(Normalised(was.Source.Branch, before)) != Json(Normalised(stage.Source.Branch, after)))
                lines.Add(stage.Source.Branch is null
                    ? $"- stage '{name}': branching removed"
                    : was.Source.Branch is null
                        ? $"+ stage '{name}': branching to {Targets(afterResolved, stage)}"
                        : $"~ stage '{name}': branching changed (now to {Targets(afterResolved, stage)})");

            if (Json(was.Source.Relationship) != Json(stage.Source.Relationship))
                lines.Add($"~ stage '{name}': relationship {was.Source.Relationship?.Name ?? "none"} → {stage.Source.Relationship?.Name ?? "none"}");

            DescribeSteps(name, was.Source.Steps, stage.Source.Steps);
            DescribeTriggers($"stage '{name}'", was.Source.Workflows, stage.Source.Workflows);
        }

        foreach (var gone in beforeResolved.Where(st => !string.IsNullOrEmpty(st.StageId) && !matched.Contains(Id(st.StageId))))
            lines.Add($"- stage '{gone.Source.Name}' ({gone.StageId})");

        DescribeTriggers("process", before.Workflows, after.Workflows);

        if (lines.Count == 0)
            lines.Add("No change.");
        return lines;

        void DescribeSteps(string stage, List<BpfStep> was, List<BpfStep> now)
        {
            static string What(BpfStep st) => st.Kind == BpfStepKind.Field
                ? st.Attribute ?? "?"
                : $"{st.Kind} {st.Label ?? st.ProcessId}";

            var oldSteps = was.Where(st => st.StepId is not null).ToDictionary(st => Id(st.StepId));
            var seen = new HashSet<string>();
            foreach (var step in now)
            {
                if (step.StepId is null || !oldSteps.TryGetValue(Id(step.StepId), out var before))
                {
                    lines.Add($"+ stage '{stage}': step {What(step)}{(step.Required ? " (required)" : "")}");
                    continue;
                }

                seen.Add(Id(step.StepId));
                if (!string.Equals(before.Attribute, step.Attribute, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(before.ProcessId, step.ProcessId, StringComparison.OrdinalIgnoreCase))
                    lines.Add($"~ stage '{stage}': step {What(before)} → {What(step)}");
                if (step.Label is not null && before.Label != step.Label)
                    lines.Add($"~ stage '{stage}': step {What(step)} label '{before.Label}' → '{step.Label}'");
                if (before.Required != step.Required)
                    lines.Add($"~ stage '{stage}': step {What(step)} {(step.Required ? "now required" : "no longer required")}");
            }

            foreach (var gone in was.Where(st => st.StepId is not null && !seen.Contains(Id(st.StepId))))
                lines.Add($"- stage '{stage}': step {What(gone)}");
        }

        void DescribeTriggers(string where, List<BpfWorkflowTrigger> was, List<BpfWorkflowTrigger> now)
        {
            static string Key(BpfWorkflowTrigger t) => $"{t.WorkflowId.Trim('{', '}').ToLowerInvariant()}@{t.On.ToLowerInvariant()}";
            var oldKeys = was.Select(Key).ToHashSet();
            var newKeys = now.Select(Key).ToHashSet();
            foreach (var t in now.Where(t => !oldKeys.Contains(Key(t))))
                lines.Add($"+ {where}: workflow {t.WorkflowId} on {t.On}");
            foreach (var t in was.Where(t => !newKeys.Contains(Key(t))))
                lines.Add($"- {where}: workflow {t.WorkflowId} on {t.On}");
        }
    }

    /// <summary>A stage reference reduced to the stage's id where it has one.</summary>
    private static string Target(BpfDefinition definition, string? reference)
    {
        if (reference is null)
            return "end";
        var stage = BpfStageResolver.Find(definition.Stages, reference);
        return (stage?.StageId ?? stage?.Name ?? reference).Trim('{', '}').ToLowerInvariant();
    }

    private static BpfBranching? Normalised(BpfBranching? branching, BpfDefinition definition) =>
        branching is null
            ? null
            : branching with
            {
                Branches = branching.Branches.Select(b => b with { Next = Target(definition, b.Next) }).ToList(),
                Else = branching.Else is null ? null : Target(definition, branching.Else)
            };

    private static readonly JsonSerializerOptions DiffJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Comparable form of a branching or relationship; tables in conditions are left out.</summary>
    private static string Json(object? value) => value switch
    {
        null => "null",
        BpfBranching b => JsonSerializer.Serialize(b with
        {
            Branches = b.Branches.Select(x => x with { Conditions = x.Conditions.Select(StripEntity).ToList() }).ToList()
        }, DiffJson),
        BpfRelationship r => JsonSerializer.Serialize(r with { Attribute = null }, DiffJson).ToLowerInvariant(),
        _ => JsonSerializer.Serialize(value, DiffJson)
    };

    private static WorkflowCondition StripEntity(WorkflowCondition c) =>
        c.IsGroup ? c with { Conditions = c.Conditions!.Select(StripEntity).ToList() } : c with { Entity = null };

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
