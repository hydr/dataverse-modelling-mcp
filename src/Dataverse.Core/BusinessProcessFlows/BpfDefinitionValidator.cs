namespace Dataverse.Core.BusinessProcessFlows;

using Dataverse.Core.Workflows;

/// <summary>
/// Checks a <see cref="BpfDefinition"/> before it is turned into XAML.
/// </summary>
/// <remarks>
/// Model rules only — table, attribute and relationship existence are checked against live metadata
/// by <see cref="Services.BusinessProcessFlowService"/>. Findings reuse
/// <see cref="WorkflowValidationIssue"/>, so the shape of a finding is the same for both kinds of
/// process; the codes are listed in the business-process-flows skill.
/// </remarks>
public static class BpfDefinitionValidator
{
    /// <summary>Platform limits (not configurable): stages per table, steps per stage, tables.</summary>
    public const int MaxStagesPerTable = 30;
    public const int MaxStepsPerStage = 30;
    public const int MaxTables = 5;

    /// <summary>Operators a business-process-flow condition can use.</summary>
    /// <remarks>
    /// The condition is evaluated in the browser from the generated <c>uidata</c>; the designer offers
    /// the comparison operators and the null checks, not the relative date ones of classic workflows.
    /// </remarks>
    private static readonly string[] KnownOperators =
    [
        "Equal", "NotEqual", "Contains", "DoesNotContain", "BeginsWith", "DoesNotBeginWith",
        "EndsWith", "DoesNotEndWith", "NotNull", "Null", "GreaterThan", "GreaterEqual",
        "LessThan", "LessEqual"
    ];

    private static readonly string[] ValuelessOperators = ["Null", "NotNull"];

    public static WorkflowValidationResult Validate(BpfDefinition definition)
    {
        var issues = new List<WorkflowValidationIssue>();

        void Error(string code, string path, string problem, string fix) =>
            issues.Add(new WorkflowValidationIssue("error", code, path, problem, fix));

        void Warning(string code, string path, string problem, string fix) =>
            issues.Add(new WorkflowValidationIssue("warning", code, path, problem, fix));

        if (string.IsNullOrWhiteSpace(definition.PrimaryEntity))
            Error("BPF001", "$.primaryEntity", "The process names no primary table.",
                "Set 'primaryEntity' to the logical name of the table the process starts on, e.g. \"lead\".");

        var stages = definition.Stages;
        if (stages.Count == 0)
        {
            Error("BPF002", "$.stages", "The process has no stages.",
                "Add at least one stage: {\"name\":\"Qualify\",\"steps\":[{\"attribute\":\"subject\"}]}.");
            return new WorkflowValidationResult(false, issues);
        }

        var resolved = BpfStageResolver.Resolve(definition, assignMissingIds: false);

        if (!string.IsNullOrWhiteSpace(definition.PrimaryEntity)
            && !string.IsNullOrWhiteSpace(stages[0].Entity)
            && !string.Equals(stages[0].Entity, definition.PrimaryEntity, StringComparison.OrdinalIgnoreCase))
            Error("BPF004", "$.stages[0].entity",
                $"The first stage is on '{stages[0].Entity}', but the process starts on '{definition.PrimaryEntity}'.",
                "The first stage must be on the primary table. Remove 'entity' from it, or change 'primaryEntity'.");

        // ---- identity: keys and ids must be unique, ids must be GUIDs
        CheckUnique(stages.Select((s, i) => (s.Key, $"$.stages[{i}].key")), "key", Error);
        CheckGuids(stages.Select((s, i) => (s.StageId, $"$.stages[{i}].stageId")), Error);
        CheckUnique(stages.Select((s, i) => (s.StageId?.Trim('{', '}'), $"$.stages[{i}].stageId")), "stageId", Error);

        var allSteps = stages.SelectMany((s, i) => s.Steps.Select((step, j) => (step, path: $"$.stages[{i}].steps[{j}]"))).ToList();
        CheckGuids(allSteps.Select(x => (x.step.StepId, $"{x.path}.stepId")), Error);
        CheckUnique(allSteps.Select(x => (x.step.StepId?.Trim('{', '}'), $"{x.path}.stepId")), "stepId", Error);

        // A stage id doubles as the label id of its step, so the two must not collide either.
        var stageIds = stages.Where(s => s.StageId is not null).Select(s => s.StageId!.Trim('{', '}'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (step, path) in allSteps.Where(x => x.step.StepId is not null && stageIds.Contains(x.step.StepId!.Trim('{', '}'))))
            Error("BPF006", $"{path}.stepId", $"Step id {step.StepId} is also used as a stage id.",
                "Every stage and step needs its own id. Remove 'stepId' to have one assigned.");

        // ---- limits
        if (resolved.Stages.Select(s => s.Entity).Distinct(StringComparer.OrdinalIgnoreCase).Count() > MaxTables)
            Error("BPF032", "$.stages", $"The process spans more than {MaxTables} tables.",
                $"A business process flow can involve at most {MaxTables} tables. Split it into two processes.");

        foreach (var group in resolved.Stages.GroupBy(s => s.Entity, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > MaxStagesPerTable))
            Error("BPF030", "$.stages", $"Table '{group.Key}' has {group.Count()} stages.",
                $"At most {MaxStagesPerTable} stages per table are allowed.");

        // ---- per stage
        for (var i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            var path = $"$.stages[{i}]";
            var entity = resolved.Stages[i].Entity;

            if (string.IsNullOrWhiteSpace(stage.Name))
                Error("BPF003", $"{path}.name", "The stage has no name.", "Set 'name'; it is the label in the process bar.");

            if (!BpfStageCategory.IsValid(stage.Category))
                Error("BPF007", $"{path}.category", $"Unknown stage category '{stage.Category}'.",
                    $"Use one of: {string.Join(", ", BpfStageCategory.Names)} — or leave it out.");

            if (stage.Steps.Count > MaxStepsPerStage)
                Error("BPF031", $"{path}.steps", $"The stage has {stage.Steps.Count} steps.",
                    $"At most {MaxStepsPerStage} steps per stage are allowed. Move some into another stage.");

            if (!string.IsNullOrWhiteSpace(stage.Next)
                && !string.Equals(stage.Next, BpfStageResolver.End, StringComparison.OrdinalIgnoreCase))
            {
                var target = BpfStageResolver.Find(stages, stage.Next!);
                if (target is null)
                    Error("BPF010", $"{path}.next", $"'{stage.Next}' names no stage.",
                        "Use the key, name or id of a stage in this definition, or \"end\".");
                else if (ReferenceEquals(target, stage))
                    Error("BPF011", $"{path}.next", "The stage leads to itself.", "Point 'next' at another stage.");
            }

            for (var j = 0; j < stage.Steps.Count; j++)
                ValidateStep(stage.Steps[j], $"{path}.steps[{j}]", Error, Warning);

            ValidateTriggers(stage.Workflows, $"{path}.workflows", BpfTriggerEvent.StageEvents, Error);

            if (stage.Branch is { } branching)
                ValidateBranching(branching, stage, stages, $"{path}.branch", Error, Warning);

            // ---- cross-table transition
            var previousEntity = i == 0 ? definition.PrimaryEntity : resolved.Stages[i - 1].Entity;
            var crossesTables = i > 0 && !string.Equals(entity, previousEntity, StringComparison.OrdinalIgnoreCase);

            if (stage.Relationship is { } rel)
            {
                if (string.IsNullOrWhiteSpace(rel.Name))
                    Error("BPF041", $"{path}.relationship.name", "The relationship has no name.",
                        "Set 'name' to the schema name of the 1:N relationship, e.g. \"opportunity_originating_lead\".");

                if (!string.IsNullOrWhiteSpace(rel.FromStage) && BpfStageResolver.Find(stages, rel.FromStage!) is null)
                    Error("BPF042", $"{path}.relationship.fromStage", $"'{rel.FromStage}' names no stage.",
                        "Use the key, name or id of the stage the process comes from — or leave it out.");

                if (resolved.Stages[i].Relationship is { } r
                    && string.Equals(resolved.Stages[r.FromIndex].Entity, entity, StringComparison.OrdinalIgnoreCase))
                    Warning("BPF043", $"{path}.relationship",
                        $"The stage is entered from a stage on the same table ('{entity}'); the relationship is not used.",
                        "Remove 'relationship', or set 'fromStage' to the stage on the other table.");
            }
            else if (crossesTables)
            {
                Error("BPF040", $"{path}.relationship",
                    $"The stage moves the process from '{previousEntity}' to '{entity}' without a relationship.",
                    $"Add 'relationship' with the 1:N relationship between {previousEntity} and {entity}, "
                    + "e.g. {\"name\":\"opportunity_originating_lead\",\"attribute\":\"originatingleadid\"}. "
                    + "describe_table lists a table's relationships.");
            }
        }

        ValidateTriggers(definition.Workflows, "$.workflows", BpfTriggerEvent.ProcessEvents, Error);

        // ---- path: every stage reachable, no cycle on the main path
        ValidatePath(stages, Error, Warning);

        return new WorkflowValidationResult(issues.All(x => x.Severity != "error"), issues);
    }

    private static void ValidateStep(
        BpfStep step, string path, Action<string, string, string, string> error,
        Action<string, string, string, string> warning)
    {
        if (!BpfStepKind.All.Contains(step.Kind))
        {
            error("BPF020", $"{path}.kind", $"Unknown step kind '{step.Kind}'.",
                $"Use one of: {string.Join(", ", BpfStepKind.All)}.");
            return;
        }

        if (step.Kind == BpfStepKind.Field)
        {
            if (string.IsNullOrWhiteSpace(step.Attribute))
                error("BPF021", $"{path}.attribute", "The data step names no attribute.",
                    "Set 'attribute' to the logical name of a column on the stage's table.");
            return;
        }

        if (!Guid.TryParse(step.ProcessId, out _))
            error("BPF023", $"{path}.processId",
                step.ProcessId is null ? $"The {step.Kind} step names no process." : $"'{step.ProcessId}' is not a GUID.",
                step.Kind == BpfStepKind.Flow
                    ? "Set 'processId' to the id of an instant flow (flow_list shows them)."
                    : "Set 'processId' to the id of an activated on-demand workflow or custom action on the stage's table.");

        if (step.Kind == BpfStepKind.Action && step.Required)
            warning("BPF027", $"{path}.required", "An action step cannot be required; the flag is ignored.",
                "Remove 'required'. Use a required data step that the workflow fills, if the stage must wait for it.");

        if (!string.IsNullOrWhiteSpace(step.Attribute))
            warning("BPF027", $"{path}.attribute", $"A {step.Kind} step has no field; 'attribute' is ignored.",
                "Remove 'attribute'.");
    }

    private static void ValidateTriggers(
        List<BpfWorkflowTrigger> triggers, string path, string[] allowed,
        Action<string, string, string, string> error)
    {
        for (var i = 0; i < triggers.Count; i++)
        {
            var trigger = triggers[i];

            if (!Guid.TryParse(trigger.WorkflowId, out _))
                error("BPF023", $"{path}[{i}].workflowId", $"'{trigger.WorkflowId}' is not a workflow id.",
                    "Set 'workflowId' to the id of an activated on-demand classic workflow.");

            if (!allowed.Contains(trigger.On))
                error("BPF025", $"{path}[{i}].on", $"'{trigger.On}' is not a trigger here.",
                    $"Use one of: {string.Join(", ", allowed)}. Stage workflows fire on stage events, the "
                    + "process-level 'workflows' on the instance's state.");

            if (trigger.TriggerId is not null && !Guid.TryParse(trigger.TriggerId, out _))
                error("BPF006", $"{path}[{i}].triggerId", $"'{trigger.TriggerId}' is not a GUID.",
                    "Remove it to have one assigned.");
        }
    }

    private static void ValidateBranching(
        BpfBranching branching, BpfStage stage, List<BpfStage> stages, string path,
        Action<string, string, string, string> error, Action<string, string, string, string> warning)
    {
        if (branching.Branches.Count == 0)
        {
            error("BPF013", $"{path}.branches", "The branching has no cases.",
                "Add at least one case with 'conditions' and 'next', or remove 'branch'.");
            return;
        }

        for (var b = 0; b < branching.Branches.Count; b++)
        {
            var branch = branching.Branches[b];
            var branchPath = $"{path}.branches[{b}]";

            if (branch.Conditions.Count == 0)
                error("BPF013", $"{branchPath}.conditions", "The case has no comparisons.",
                    "Add a comparison, e.g. {\"attribute\":\"budgetamount\",\"operator\":\"GreaterThan\","
                    + "\"value\":{\"kind\":\"literal\",\"dataType\":\"Money\",\"literal\":\"10000\"}}.");

            if (branch.LogicalOperator is { } op && op is not ("And" or "Or" or "and" or "or"))
                error("BPF016", $"{branchPath}.logicalOperator", $"Unknown logical operator '{op}'.", "Use \"And\" or \"Or\".");

            CheckTarget(branch.Next, $"{branchPath}.next");
            ValidateComparisons(branch.Conditions, $"{branchPath}.conditions", error, warning);
        }

        if (!string.IsNullOrWhiteSpace(branching.Else))
            CheckTarget(branching.Else!, $"{path}.else");
        else
            warning("BPF026", $"{path}.else", "The condition has no 'else' stage.",
                "The process runs without one — the path simply continues with 'next'. The designer, though, "
                + "reports such a condition as an empty branch and refuses to save it until one is added.");

        // The designer only offers the stage's own data steps as condition fields, and rejects anything
        // else on save ("only steps of the previous stage can be used").
        var stepFields = stage.Steps.Where(s => s.Kind == BpfStepKind.Field && !string.IsNullOrWhiteSpace(s.Attribute))
            .Select(s => s.Attribute!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var b = 0; b < branching.Branches.Count; b++)
            CheckStepFields(branching.Branches[b].Conditions, $"{path}.branches[{b}].conditions");

        void CheckStepFields(List<WorkflowCondition> conditions, string conditionsPath)
        {
            for (var c = 0; c < conditions.Count; c++)
            {
                var condition = conditions[c];
                if (condition.IsGroup)
                {
                    CheckStepFields(condition.Conditions!, $"{conditionsPath}[{c}].conditions");
                    continue;
                }

                var used = new List<(string Field, string FieldPath)>();
                if (!string.IsNullOrWhiteSpace(condition.Attribute))
                    used.Add((condition.Attribute, $"{conditionsPath}[{c}].attribute"));
                foreach (var (field, f) in (condition.Value?.Fields ?? []).Select((x, n) => (x, n)))
                    used.Add((field.Contains('.') ? field.Split('.', 2)[1] : field, $"{conditionsPath}[{c}].value.fields[{f}]"));

                foreach (var (field, fieldPath) in used.Where(u => !stepFields.Contains(u.Field)))
                    error("BPF024", fieldPath,
                        $"'{field}' is not a data step of stage '{stage.Name}'.",
                        "A branch can only compare the fields this stage asks for. Add a data step for "
                        + $"'{field}' to the stage, or compare another field.");
            }
        }

        void CheckTarget(string reference, string targetPath)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                error("BPF014", targetPath, "The case leads nowhere.", "Set it to the key, name or id of a stage.");
                return;
            }

            var target = BpfStageResolver.Find(stages, reference);
            if (target is null)
                error("BPF012", targetPath, $"'{reference}' names no stage.",
                    "Use the key, name or id of a stage in this definition.");
            else if (ReferenceEquals(target, stage))
                error("BPF015", targetPath, "The case leads back to its own stage.", "Point it at another stage.");
        }
    }

    private static void ValidateComparisons(
        List<WorkflowCondition> conditions, string path,
        Action<string, string, string, string> error, Action<string, string, string, string> warning)
    {
        for (var i = 0; i < conditions.Count; i++)
        {
            var condition = conditions[i];
            var conditionPath = $"{path}[{i}]";

            if (condition.IsGroup)
            {
                if (condition.GroupOperator is { } groupOperator && groupOperator is not ("And" or "Or" or "and" or "or"))
                    error("BPF016", $"{conditionPath}.groupOperator", $"Unknown logical operator '{groupOperator}'.",
                        "Use \"And\" or \"Or\".");
                ValidateComparisons(condition.Conditions!, $"{conditionPath}.conditions", error, warning);
                continue;
            }

            if (string.IsNullOrWhiteSpace(condition.Attribute))
                error("BPF017", $"{conditionPath}.attribute", "The comparison names no attribute.",
                    "Set 'attribute' to a column of the stage's table.");

            if (!string.IsNullOrWhiteSpace(condition.Entity) || !string.IsNullOrWhiteSpace(condition.Via)
                || !string.IsNullOrWhiteSpace(condition.FromStep) || !string.IsNullOrWhiteSpace(condition.FromStepOutput)
                || !string.IsNullOrWhiteSpace(condition.StepOutput))
                error("BPF018", conditionPath, "A branch can only compare columns of the stage's own table.",
                    "Remove 'entity', 'via', 'fromStep', 'fromStepOutput' and 'stepOutput'. To branch on "
                    + "another table, put the condition on a stage of that table.");

            var op = WorkflowXamlBuilder.MapOperator(condition.Operator ?? string.Empty);
            if (!KnownOperators.Contains(op))
                error("BPF019", $"{conditionPath}.operator", $"Operator '{condition.Operator}' is not available in a business process flow.",
                    $"Use one of: {string.Join(", ", KnownOperators)}.");

            var needsValue = !ValuelessOperators.Contains(op);
            if (needsValue && condition.Value is null)
                error("BPF019", $"{conditionPath}.value", $"Operator '{op}' requires a value.",
                    "Add 'value', e.g. {\"kind\":\"literal\",\"literal\":\"Foo\"}.");
            if (!needsValue && condition.Value is not null)
                warning("BPF019", $"{conditionPath}.value", $"Operator '{op}' takes no value; it is ignored.",
                    "Remove 'value'.");

            if (condition.Value is { } value
                && value.Kind is not (WorkflowValueKind.Literal or WorkflowValueKind.Field))
                error("BPF019", $"{conditionPath}.value.kind", $"A branch cannot compare against a '{value.Kind}' value.",
                    "Use {\"kind\":\"literal\",…} or {\"kind\":\"field\",\"fields\":[\"<column>\"]} on the stage's table.");
        }
    }

    private static void ValidatePath(
        List<BpfStage> stages, Action<string, string, string, string> error, Action<string, string, string, string> warning)
    {
        // Reachability from the first stage, over the main path and every branch.
        var reached = new HashSet<BpfStage>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<BpfStage>([stages[0]]);
        while (queue.Count > 0)
        {
            var stage = queue.Dequeue();
            if (!reached.Add(stage))
                continue;

            var index = stages.IndexOf(stage);
            if (BpfStageResolver.NextOf(stages, index) is { } next)
                queue.Enqueue(next);
            foreach (var target in BpfStageResolver.BranchTargets(stage))
                if (BpfStageResolver.Find(stages, target) is { } t)
                    queue.Enqueue(t);
        }

        for (var i = 0; i < stages.Count; i++)
            if (!reached.Contains(stages[i]))
                warning("BPF050", $"$.stages[{i}]", $"Stage '{stages[i].Name}' cannot be reached from the first stage.",
                    "Point a stage's 'next' or a branch at it, or remove it.");

        // A loop on the main path would never end; the designer does not allow one either.
        for (var i = 0; i < stages.Count; i++)
        {
            var seen = new HashSet<BpfStage>(ReferenceEqualityComparer.Instance);
            var current = stages[i];
            while (current is not null && seen.Add(current))
                current = BpfStageResolver.NextOf(stages, stages.IndexOf(current))!;

            if (current is not null && ReferenceEquals(current, stages[i]))
            {
                error("BPF051", $"$.stages[{i}].next", $"The main path loops back to stage '{stages[i].Name}'.",
                    "Break the loop: the path has to end. Use \"end\" as 'next' on the last stage of a branch.");
                break;
            }
        }
    }

    private static void CheckUnique(
        IEnumerable<(string? Value, string Path)> values, string what, Action<string, string, string, string> error)
    {
        foreach (var group in values.Where(v => !string.IsNullOrWhiteSpace(v.Value))
                     .GroupBy(v => v.Value!, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
            error("BPF005", group.Skip(1).First().Path, $"'{group.Key}' is used as {what} more than once.",
                $"Every {what} must be unique within the process.");
    }

    private static void CheckGuids(
        IEnumerable<(string? Value, string Path)> values, Action<string, string, string, string> error)
    {
        foreach (var (value, path) in values)
            if (!string.IsNullOrWhiteSpace(value) && !Guid.TryParse(value, out _))
                error("BPF006", path, $"'{value}' is not a GUID.",
                    "Remove it to have one assigned, or keep the id read from the existing process.");
    }
}
