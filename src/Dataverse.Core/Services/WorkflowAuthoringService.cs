namespace Dataverse.Core.Services;

using System.Text.Json;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Dataverse.Core.Workflows;
using Microsoft.Extensions.Logging;

/// <param name="Applied">True when the XAML was written.</param>
/// <param name="Diff">
/// On a dry run: what the write would change, as a readable summary. Null otherwise.
/// </param>
public sealed record WorkflowSaveResult(
    bool Applied,
    Guid WorkflowId,
    IReadOnlyList<string> StepIds,
    WorkflowValidationResult Validation,
    string? Backup,
    string? Message,
    string? Diff = null);

/// <summary>
/// Authoring operations for Classic Workflows: read the logic as a definition, validate it against
/// live metadata, and write it back as XAML.
/// </summary>
/// <remarks>
/// Every write goes through three gates — model validation, metadata validation, and a re-check of
/// the generated XAML — so a malformed workflow cannot reach Dataverse. See
/// <c>docs/classic-workflows-reference.md</c> for the format itself.
/// </remarks>
public sealed class WorkflowAuthoringService(
    DataverseHttpClient client,
    WorkflowService workflows,
    ILogger<WorkflowAuthoringService> logger)
{
    /// <summary>Reads a workflow and reconstructs its logic.</summary>
    public async Task<(WorkflowDetail Detail, WorkflowParseResult Parsed)> GetDefinitionAsync(
        string orgUrl, Guid workflowId, CancellationToken ct = default)
    {
        var detail = await workflows.GetAsync(orgUrl, workflowId, ct)
            ?? throw new InvalidOperationException($"Workflow {workflowId} not found.");

        var parsed = WorkflowXamlParser.Parse(detail.Xaml, detail.PrimaryEntity ?? string.Empty);
        return (detail, parsed);
    }

    /// <summary>
    /// Validates a definition without writing anything: model rules first, then attribute and
    /// entity existence against live metadata.
    /// </summary>
    public async Task<WorkflowValidationResult> ValidateAsync(
        string orgUrl, WorkflowDefinition definition, CancellationToken ct = default)
    {
        var activities = await LoadActivityCatalogAsync(orgUrl, definition, ct);
        return await ValidateAsync(orgUrl, definition, activities, ct);
    }

    private async Task<WorkflowValidationResult> ValidateAsync(
        string orgUrl, WorkflowDefinition definition, WorkflowActivityCatalog activities,
        CancellationToken ct)
    {
        var result = WorkflowDefinitionValidator.Validate(definition, activities);
        var issues = result.Issues.ToList();

        // Metadata checks only make sense once the model itself is sound.
        if (result.CanSave)
            issues.AddRange(await ValidateAgainstMetadataAsync(orgUrl, definition, ct));

        return new WorkflowValidationResult(issues.All(i => i.Severity != "error"), issues);
    }

    /// <summary>
    /// Reads the parameter metadata of every code activity the definition refers to. Both the
    /// validator and the builder need it: the validator to check the arguments against the real
    /// signature, the builder to type them.
    /// </summary>
    private async Task<WorkflowActivityCatalog> LoadActivityCatalogAsync(
        string orgUrl, WorkflowDefinition definition, CancellationToken ct)
    {
        var names = new List<string>();

        void Collect(List<WorkflowStep>? steps)
        {
            foreach (var step in steps ?? [])
            {
                if (step.Kind == WorkflowStepKind.CustomActivity
                    && !string.IsNullOrWhiteSpace(step.AssemblyQualifiedName))
                    names.Add(step.AssemblyQualifiedName!);

                Collect(step.Then);
                Collect(step.Else);
                Collect(step.Children);

                foreach (var branch in step.Branches ?? [])
                    Collect(branch.Steps);
            }
        }

        Collect(definition.Steps);
        return names.Count == 0
            ? WorkflowActivityCatalog.Empty
            : await workflows.GetActivityCatalogAsync(orgUrl, names, ct);
    }

    /// <summary>
    /// Writes the definition as XAML. Refuses to write when validation reports an error or when the
    /// workflow is activated.
    /// </summary>
    /// <param name="dryRun">
    /// Validate and build, then report what would change — without writing. The safe first step on a
    /// workflow one did not author.
    /// </param>
    public async Task<WorkflowSaveResult> SetDefinitionAsync(
        string orgUrl,
        Guid workflowId,
        WorkflowDefinition definition,
        bool reactivate = false,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        var detail = await workflows.GetAsync(orgUrl, workflowId, ct)
            ?? throw new InvalidOperationException($"Workflow {workflowId} not found.");

        // Fill in the primary entity from the record when the caller omitted it.
        if (string.IsNullOrWhiteSpace(definition.PrimaryEntity))
            definition = definition with { PrimaryEntity = detail.PrimaryEntity ?? string.Empty };

        // The mode belongs to the record, not to the definition, and it decides whether persistence
        // points are allowed — a real-time process must not contain any.
        definition = definition with
        {
            Realtime = string.Equals(detail.Mode, "Realtime", StringComparison.OrdinalIgnoreCase)
        };

        // One read of the activity metadata, used for validating and for typing the arguments.
        var activities = await LoadActivityCatalogAsync(orgUrl, definition, ct);

        var validation = await ValidateAsync(orgUrl, definition, activities, ct);
        if (!validation.CanSave)
            return new WorkflowSaveResult(false, workflowId, [], validation, null,
                $"Nothing was written: {validation.ErrorCount} error(s) must be fixed first.");

        var wasActive = detail.StateCode == 1;
        if (wasActive && !reactivate)
        {
            var blocked = validation.Issues.Append(new WorkflowValidationIssue("error", "WF210",
                "$.workflow",
                "The workflow is activated; Dataverse does not allow changing an active workflow.",
                "Deactivate it first (workflow_set_state activate=false), or pass reactivate=true to " +
                "have this tool deactivate, write and re-activate in one go.")).ToList();

            return new WorkflowSaveResult(false, workflowId, [], new WorkflowValidationResult(false, blocked),
                null, "Nothing was written: the workflow is active.");
        }

        var build = WorkflowXamlBuilder.Build(definition, workflowId, activities);

        // Safety net against builder defects.
        var xamlCheck = WorkflowDefinitionValidator.ValidateGeneratedXaml(build.Xaml);
        if (!xamlCheck.CanSave)
            return new WorkflowSaveResult(false, workflowId, build.StepIds, xamlCheck, null,
                "Nothing was written: the generated XAML failed its self-check.");

        var backup = detail.Xaml;

        if (dryRun)
            return new WorkflowSaveResult(false, workflowId, build.StepIds,
                new WorkflowValidationResult(true, validation.Issues.Concat(xamlCheck.Issues).ToList()),
                backup, "Dry run — nothing was written.",
                DescribeChange(detail.Xaml, build.Xaml, definition));

        if (wasActive)
            await workflows.SetStateAsync(orgUrl, workflowId, activate: false, ct);

        await workflows.UpdateAsync(orgUrl, workflowId,
            new Dictionary<string, object?> { ["xaml"] = build.Xaml }, ct);

        logger.LogInformation("Wrote XAML for workflow {Id} ({Steps} steps)", workflowId, build.StepIds.Count);

        string? message = null;
        if (wasActive)
        {
            try
            {
                await workflows.SetStateAsync(orgUrl, workflowId, activate: true, ct);
                message = "Workflow was deactivated, updated and re-activated.";
            }
            catch (Exception ex)
            {
                // The XAML is written; only re-activation failed. Say so plainly.
                message = "XAML was written, but re-activation failed: " + ex.Message +
                          " The workflow is currently a draft.";
                logger.LogWarning(ex, "Re-activation of workflow {Id} failed", workflowId);
            }
        }

        var allIssues = validation.Issues.Concat(xamlCheck.Issues).ToList();
        return new WorkflowSaveResult(true, workflowId, build.StepIds,
            new WorkflowValidationResult(true, allIssues), backup, message);
    }

    /// <summary>
    /// A readable summary of what writing this definition would change.
    /// </summary>
    /// <remarks>
    /// Comparing XAML line by line is useless here — the whole document is one line, and step
    /// renumbering shifts everything. What a caller needs to know is which steps and which activities
    /// come and go, so that is what this reports.
    /// </remarks>
    private static string DescribeChange(string? before, string after, WorkflowDefinition definition)
    {
        var sb = new System.Text.StringBuilder();

        if (string.IsNullOrWhiteSpace(before))
        {
            sb.AppendLine("The workflow has no logic yet; this would be the first write.");
        }
        else
        {
            var wasParsed = WorkflowXamlParser.Parse(before, definition.PrimaryEntity);
            sb.AppendLine($"Steps now: {Describe(wasParsed.Definition.Steps)}");
            sb.AppendLine($"Steps after: {Describe(definition.Steps)}");

            if (!wasParsed.FullyUnderstood)
                sb.AppendLine($"WARNING: the current logic has {wasParsed.Unrecognised.Count} construct(s) "
                    + "this server does not understand. Writing would drop them: "
                    + string.Join("; ", wasParsed.Unrecognised));

            sb.AppendLine($"XAML size: {before!.Length} -> {after.Length} characters");
        }

        var activities = CountActivities(after);
        var activitiesBefore = string.IsNullOrWhiteSpace(before) ? [] : CountActivities(before!);

        var changed = activities.Keys.Union(activitiesBefore.Keys)
            .Where(k => activitiesBefore.GetValueOrDefault(k) != activities.GetValueOrDefault(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => $"  {k}: {activitiesBefore.GetValueOrDefault(k)} -> {activities.GetValueOrDefault(k)}")
            .ToList();

        if (changed.Count > 0)
        {
            sb.AppendLine("Activities that change in number:");
            foreach (var line in changed)
                sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    private static string Describe(List<WorkflowStep> steps) =>
        steps.Count == 0
            ? "(none)"
            : string.Join(", ", steps.Select(s => s.Kind + (s.Description is null ? "" : $" \"{s.Description}\"")));

    private static Dictionary<string, int> CountActivities(string xaml)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var m in System.Text.RegularExpressions.Regex
                     .Matches(xaml, @"AssemblyQualifiedName=""([^,""]+)").Cast<System.Text.RegularExpressions.Match>())
        {
            var name = m.Groups[1].Value.Split('.').Last();
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        foreach (var m in System.Text.RegularExpressions.Regex
                     .Matches(xaml, @"<mxswa:(\w+)[ />]").Cast<System.Text.RegularExpressions.Match>())
        {
            var name = m.Groups[1].Value;
            if (name is "ActivityReference" or "ReferenceLiteral") continue;
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        return counts;
    }

    /// <summary>Restores a previously exported XAML verbatim (used to undo a bad write).</summary>
    public async Task RestoreXamlAsync(string orgUrl, Guid workflowId, string xaml, CancellationToken ct = default)
    {
        await workflows.UpdateAsync(orgUrl, workflowId,
            new Dictionary<string, object?> { ["xaml"] = xaml }, ct);
    }

    // ---------------------------------------------------------------- metadata validation

    private async Task<List<WorkflowValidationIssue>> ValidateAgainstMetadataAsync(
        string orgUrl, WorkflowDefinition definition, CancellationToken ct)
    {
        var issues = new List<WorkflowValidationIssue>();
        var cache = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);

        async Task<HashSet<string>?> AttributesOf(string entity)
        {
            if (cache.TryGetValue(entity, out var cached))
                return cached;

            HashSet<string>? attributes = null;
            try
            {
                var url = $"api/data/v9.2/EntityDefinitions(LogicalName='{Uri.EscapeDataString(entity)}')" +
                          "/Attributes?$select=LogicalName";
                var raw = await client.GetRawAsync(orgUrl, url, ct: ct);
                var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("value", out var items))
                {
                    attributes = items.EnumerateArray()
                        .Select(a => a.GetStringOrNull("LogicalName"))
                        .Where(n => n is not null)
                        .Select(n => n!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                // Unknown entity or transient failure — reported by the caller as a warning.
                logger.LogDebug(ex, "Could not read metadata for entity {Entity}", entity);
            }

            cache[entity] = attributes;
            return attributes;
        }

        async Task CheckAttribute(string entity, string attribute, string path)
        {
            if (string.IsNullOrWhiteSpace(entity) || string.IsNullOrWhiteSpace(attribute))
                return;

            var attributes = await AttributesOf(entity);
            if (attributes is null)
            {
                issues.Add(new WorkflowValidationIssue("error", "WF300", path,
                    $"Table '{entity}' does not exist or its metadata is not readable.",
                    "Check the logical name with table_list. Logical names are lowercase, " +
                    "e.g. \"lead\", \"sample_accrual\"."));
                return;
            }

            if (!attributes.Contains(attribute))
            {
                var hint = attributes
                    .Where(a => a.Contains(attribute, StringComparison.OrdinalIgnoreCase)
                                || attribute.Contains(a, StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .ToList();

                issues.Add(new WorkflowValidationIssue("error", "WF301", path,
                    $"Table '{entity}' has no attribute '{attribute}'.",
                    hint.Count > 0
                        ? $"Did you mean: {string.Join(", ", hint)}? Use describe_table for the full list."
                        : "Look up the logical name with describe_table."));
            }
        }

        async Task WalkSteps(List<WorkflowStep> steps, string path)
        {
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var stepPath = $"{path}[{i}]";

                // A send-email step always writes an 'email' record, whatever the primary entity is.
                var entity = step.Kind == WorkflowStepKind.SendEmail
                    ? "email"
                    : step.Entity ?? definition.PrimaryEntity;

                foreach (var (assignment, index) in (step.Attributes ?? []).Select((a, n) => (a, n)))
                {
                    await CheckAttribute(entity, assignment.Attribute, $"{stepPath}.attributes[{index}].attribute");
                    await CheckValueFields(assignment.Value, $"{stepPath}.attributes[{index}].value");
                }

                async Task WalkConditions(List<WorkflowCondition> conditions, string basePath)
                {
                    foreach (var (condition, index) in conditions.Select((c, n) => (c, n)))
                    {
                        // A bracketed group holds comparisons instead of being one.
                        if (condition.IsGroup)
                        {
                            await WalkConditions(condition.Conditions!, $"{basePath}.conditions[{index}]");
                            continue;
                        }

                        // A comparison on a step output has no attribute of its own.
                        if (string.IsNullOrWhiteSpace(condition.StepOutput))
                            await CheckAttribute(condition.Entity ?? definition.PrimaryEntity, condition.Attribute,
                                $"{basePath}.conditions[{index}].attribute");
                        if (condition.Value is not null)
                            await CheckValueFields(condition.Value, $"{basePath}.conditions[{index}].value");
                    }
                }

                await WalkConditions(step.Conditions ?? [], stepPath);

                foreach (var (key, value) in step.Inputs ?? [])
                    await CheckValueFields(value, $"{stepPath}.inputs['{key}']");

                // Every case of an if/else-if chain, including its own comparisons.
                foreach (var (branch, index) in (step.Branches ?? []).Select((b, n) => (b, n)))
                {
                    await WalkConditions(branch.Conditions, $"{stepPath}.branches[{index}]");
                    if (branch.Steps is { } branchSteps)
                        await WalkSteps(branchSteps, $"{stepPath}.branches[{index}].steps");
                }

                if (step.Then is { } then) await WalkSteps(then, $"{stepPath}.then");
                if (step.Else is { } @else) await WalkSteps(@else, $"{stepPath}.else");
                if (step.Children is { } children) await WalkSteps(children, $"{stepPath}.children");
            }
        }

        async Task CheckValueFields(WorkflowValue value, string path)
        {
            if (value.Kind != WorkflowValueKind.Field || value.Fields is null)
                return;

            foreach (var (reference, index) in value.Fields.Select((f, n) => (f, n)))
            {
                var (entity, attribute) = WorkflowXamlBuilder.SplitFieldReference(reference, definition.PrimaryEntity);
                await CheckAttribute(entity, attribute, $"{path}.fields[{index}]");
            }
        }

        await WalkSteps(definition.Steps, "$.steps");
        return issues;
    }
}
