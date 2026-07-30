namespace Dataverse.Core.Services;

using Dataverse.Core.Workflows;

/// <param name="Activated">True when the whole definition activated — nothing to diagnose.</param>
/// <param name="Error">The error the full definition produced.</param>
/// <param name="Culprit">
/// The smallest set of top-level steps that still fails, described in words. Null when the cause could
/// not be narrowed down.
/// </param>
/// <param name="Attempts">What was tried, in order — the evidence behind the conclusion.</param>
public sealed record WorkflowActivationDiagnosis(
    bool Activated,
    string? Error,
    string? Culprit,
    IReadOnlyList<string> Attempts);

/// <summary>
/// Narrows down which part of a definition Dataverse refuses to activate.
/// </summary>
/// <remarks>
/// <para>
/// Activation errors are close to useless on their own: <c>0x80040216</c> is literally "an unexpected
/// error occurred", and <c>0x80048455</c> names the composite but not the reason. The only reliable way
/// to find the cause is to write subsets and see which one still fails — that is what this does,
/// automatically, in a throwaway workflow that is deleted again.
/// </para>
/// <para>
/// It works top-down: first each step on its own, then, if a single step is the culprit, the step's own
/// branches. That is enough to point at a step and its case; finer granularity is rarely needed because
/// a step is small enough to read.
/// </para>
/// </remarks>
public sealed class WorkflowActivationDiagnoser(
    WorkflowService workflows,
    WorkflowAuthoringService authoring)
{
    public async Task<WorkflowActivationDiagnosis> DiagnoseAsync(
        string orgUrl,
        WorkflowDefinition definition,
        string primaryEntity,
        bool realtime,
        CancellationToken ct = default)
    {
        var attempts = new List<string>();

        // Does the whole thing fail at all?
        var whole = await TryActivateAsync(orgUrl, definition, primaryEntity, realtime, "everything", attempts, ct);
        if (whole is null)
            return new WorkflowActivationDiagnosis(true, null, null, attempts);

        // One step at a time.
        var failing = new List<int>();
        for (var i = 0; i < definition.Steps.Count; i++)
        {
            var single = definition with { Steps = [definition.Steps[i]] };
            var error = await TryActivateAsync(orgUrl, single, primaryEntity, realtime,
                $"step {i + 1} only ({Describe(definition.Steps[i])})", attempts, ct);

            if (error is not null)
                failing.Add(i);
        }

        if (failing.Count == 0)
            return new WorkflowActivationDiagnosis(false, whole,
                "No single step fails on its own — the cause is in how the steps combine, most often a "
                + "reference from one step to a record another step creates.", attempts);

        // A single failing step: try its branches to point at a case.
        if (failing.Count == 1)
        {
            var step = definition.Steps[failing[0]];
            var narrowed = await NarrowBranchesAsync(orgUrl, definition, step, primaryEntity, realtime, attempts, ct);

            return new WorkflowActivationDiagnosis(false, whole,
                narrowed ?? $"Step {failing[0] + 1} ({Describe(step)}) does not activate on its own.",
                attempts);
        }

        return new WorkflowActivationDiagnosis(false, whole,
            $"{failing.Count} steps fail on their own: "
            + string.Join(", ", failing.Select(i => $"step {i + 1} ({Describe(definition.Steps[i])})")),
            attempts);
    }

    /// <summary>For a condition step: which of its cases is the one that fails?</summary>
    private async Task<string?> NarrowBranchesAsync(
        string orgUrl, WorkflowDefinition definition, WorkflowStep step,
        string primaryEntity, bool realtime, List<string> attempts, CancellationToken ct)
    {
        if (step.Branches is not { Count: > 1 } branches)
            return null;

        for (var b = 0; b < branches.Count; b++)
        {
            var single = definition with
            {
                Steps = [step with { Branches = [branches[b]], Else = null }]
            };

            var error = await TryActivateAsync(orgUrl, single, primaryEntity, realtime,
                $"case {b + 1} of {Describe(step)}", attempts, ct);

            if (error is not null)
                return $"Case {b + 1} of step '{Describe(step)}' does not activate.";
        }

        if (step.Else is { Count: > 0 })
        {
            var elseOnly = definition with { Steps = [step with { Branches = null, Else = step.Else }] };
            var error = await TryActivateAsync(orgUrl, elseOnly, primaryEntity, realtime,
                $"default case of {Describe(step)}", attempts, ct);

            if (error is not null)
                return $"The default case of step '{Describe(step)}' does not activate.";
        }

        return null;
    }

    /// <summary>Writes and activates a subset in a throwaway workflow. Returns the error, or null.</summary>
    private async Task<string?> TryActivateAsync(
        string orgUrl, WorkflowDefinition definition, string primaryEntity, bool realtime,
        string what, List<string> attempts, CancellationToken ct)
    {
        var id = Guid.Empty;
        try
        {
            id = await workflows.CreateAsync(orgUrl, $"ZZ Diagnose {Guid.NewGuid():N}", primaryEntity,
                "Wegwerf-Workflow der Aktivierungsdiagnose", realtime, ct);

            await workflows.UpdateAsync(orgUrl, id, new Dictionary<string, object?>
            {
                ["ondemand"] = true
            }, ct);

            var save = await authoring.SetDefinitionAsync(orgUrl, id, definition, ct: ct);
            if (!save.Applied)
            {
                var codes = string.Join(", ", save.Validation.Issues
                    .Where(i => i.Severity == "error").Select(i => i.Code));
                attempts.Add($"{what}: not written ({codes})");
                return $"validation refused it: {codes}";
            }

            await workflows.SetStateAsync(orgUrl, id, activate: true, ct);
            attempts.Add($"{what}: activates");
            return null;
        }
        catch (Exception ex)
        {
            var code = System.Text.RegularExpressions.Regex.Match(ex.Message, @"0x[0-9a-fA-F]{8}").Value;
            attempts.Add($"{what}: FAILS {code}");
            return code.Length > 0 ? code : ex.Message;
        }
        finally
        {
            if (id != Guid.Empty)
            {
                try { await workflows.SetStateAsync(orgUrl, id, activate: false, ct); } catch { }
                try { await workflows.DeleteAsync(orgUrl, id, ct); } catch { }
            }
        }
    }

    private static string Describe(WorkflowStep step) =>
        string.IsNullOrWhiteSpace(step.Description) ? step.Kind : $"{step.Kind}: {step.Description}";
}
