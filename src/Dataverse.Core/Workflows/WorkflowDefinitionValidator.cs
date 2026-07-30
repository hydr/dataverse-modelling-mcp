namespace Dataverse.Core.Workflows;

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dataverse.Core.Models;

/// <summary>A single finding. Every field is aimed at making the fix obvious.</summary>
/// <param name="Severity">"error" blocks saving, "warning" does not.</param>
/// <param name="Code">Stable code, see the table in the classic-workflows skill.</param>
/// <param name="Path">JSON-ish path into the definition, e.g. "steps[0].then[1].attributes[0].value".</param>
/// <param name="Problem">What is wrong.</param>
/// <param name="Fix">What to do about it.</param>
public sealed record WorkflowValidationIssue(
    string Severity,
    string Code,
    string Path,
    string Problem,
    string Fix);

/// <param name="CanSave">False when at least one error was found.</param>
public sealed record WorkflowValidationResult(
    bool CanSave,
    IReadOnlyList<WorkflowValidationIssue> Issues)
{
    public int ErrorCount => Issues.Count(i => i.Severity == "error");
    public int WarningCount => Issues.Count(i => i.Severity == "warning");
}

/// <summary>
/// Validates a <see cref="WorkflowDefinition"/> before it is turned into XAML, and re-checks the
/// generated XAML afterwards.
/// </summary>
/// <remarks>
/// Two layers guard against a broken workflow:
/// <list type="number">
/// <item>Model checks — completeness and consistency of what the caller asked for.</item>
/// <item>Output checks — the generated XAML is well-formed, every referenced variable is declared
/// and every step id is unique. This catches builder bugs before they reach Dataverse.</item>
/// </list>
/// Attribute existence is verified separately against live metadata (see WorkflowService).
/// </remarks>
public static class WorkflowDefinitionValidator
{
    private static readonly string[] KnownOperators =
    [
        "Equal", "NotEqual", "Contains", "DoesNotContain", "BeginsWith", "DoesNotBeginWith",
        "EndsWith", "DoesNotEndWith", "NotNull", "Null", "GreaterThan", "GreaterEqual",
        "LessThan", "LessEqual", "In", "NotIn"
    ];

    private static readonly string[] ValuelessOperators = ["Null", "NotNull"];

    private static readonly string[] KnownDataTypes =
    [
        "String", "Integer", "Int32", "Boolean", "Bool", "DateTime", "Decimal", "Double", "Float",
        "Money", "OptionSetValue", "OptionSet", "Picklist", "EntityReference", "Lookup", "Guid",
        "UniqueIdentifier"
    ];

    /// <param name="activities">
    /// Parameter metadata of the referenced code activities. When supplied, the arguments of a
    /// <c>customActivity</c> step are checked against the real signature (WF085–WF089); without it
    /// those four error classes only surface on activation, as <c>0x80048455</c>.
    /// </param>
    public static WorkflowValidationResult Validate(
        WorkflowDefinition? definition, WorkflowActivityCatalog? activities = null)
    {
        var issues = new List<WorkflowValidationIssue>();
        activities ??= WorkflowActivityCatalog.Empty;

        if (definition is null)
        {
            issues.Add(new WorkflowValidationIssue("error", "WF001", "$",
                "The definition is null or could not be parsed as JSON.",
                "Pass a JSON object with 'primaryEntity' and a 'steps' array."));
            return new WorkflowValidationResult(false, issues);
        }

        if (string.IsNullOrWhiteSpace(definition.PrimaryEntity))
            issues.Add(new WorkflowValidationIssue("error", "WF002", "$.primaryEntity",
                "The primary entity is missing.",
                "Set 'primaryEntity' to the logical name of the triggering table, e.g. \"lead\"."));

        if (definition.Steps.Count == 0)
            issues.Add(new WorkflowValidationIssue("error", "WF003", "$.steps",
                "The workflow has no steps.",
                "Add at least one step. A workflow without steps cannot be activated usefully."));

        // Collect the outputs available for stepOutput references, in document order.
        var availableOutputs = new List<string>();
        ValidateSteps(definition.Steps, "$.steps", definition, issues, availableOutputs,
            insideStage: false, activities);

        return new WorkflowValidationResult(issues.All(i => i.Severity != "error"), issues);
    }

    private static void ValidateSteps(
        List<WorkflowStep> steps, string path, WorkflowDefinition definition,
        List<WorkflowValidationIssue> issues, List<string> availableOutputs, bool insideStage,
        WorkflowActivityCatalog activities)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var stepPath = $"{path}[{i}]";

            if (string.IsNullOrWhiteSpace(step.Kind))
            {
                issues.Add(new WorkflowValidationIssue("error", "WF010", $"{stepPath}.kind",
                    "The step has no kind.",
                    $"Set 'kind' to one of: {string.Join(", ", WorkflowStepKind.Writable)}."));
                continue;
            }

            if (!WorkflowStepKind.All.Contains(step.Kind))
            {
                issues.Add(new WorkflowValidationIssue("error", "WF011", $"{stepPath}.kind",
                    $"Unknown step kind '{step.Kind}'.",
                    $"Use one of: {string.Join(", ", WorkflowStepKind.Writable)}."));
                continue;
            }

            if (!WorkflowStepKind.Writable.Contains(step.Kind))
            {
                issues.Add(new WorkflowValidationIssue("error", "WF012", $"{stepPath}.kind",
                    $"Step kind '{step.Kind}' can be read from existing workflows but not generated, " +
                    "because its configuration format is not fully verified.",
                    "Configure this step in the Dataverse designer, or model the logic with a " +
                    $"supported kind: {string.Join(", ", WorkflowStepKind.Writable)}."));
                continue;
            }

            switch (step.Kind)
            {
                case WorkflowStepKind.Condition:
                case WorkflowStepKind.Wait:
                    ValidateCondition(step, stepPath, definition, issues, availableOutputs, insideStage,
                        activities);
                    break;

                case WorkflowStepKind.Stage:
                    if (insideStage)
                        issues.Add(new WorkflowValidationIssue("error", "WF013", stepPath,
                            "Stages cannot be nested.",
                            "Move this step out of the surrounding stage."));
                    if (step.Children is null || step.Children.Count == 0)
                        issues.Add(new WorkflowValidationIssue("error", "WF014", $"{stepPath}.children",
                            "The stage contains no steps.",
                            "Add steps to 'children', or remove the stage."));
                    else
                        ValidateSteps(step.Children, $"{stepPath}.children", definition, issues,
                            availableOutputs, insideStage: true, activities);

                    // The designer always labels stages (it inserts a placeholder text), so an
                    // unnamed stage looks broken to whoever opens the process next.
                    if (string.IsNullOrWhiteSpace(step.Description))
                        issues.Add(new WorkflowValidationIssue("warning", "WF015", $"{stepPath}.description",
                            "The stage has no description. Stages are the process outline in the " +
                            "designer, and an unnamed one is hard to read.",
                            "Set 'description' to a short label, e.g. \"Qualifizierung\"."));
                    break;

                case WorkflowStepKind.CreateRecord:
                    if (string.IsNullOrWhiteSpace(step.Entity))
                        issues.Add(new WorkflowValidationIssue("error", "WF020", $"{stepPath}.entity",
                            "A create step needs a target entity.",
                            "Set 'entity' to the logical name of the table to create, e.g. \"task\"."));
                    ValidateAssignments(step, stepPath, definition, issues, availableOutputs, requireAny: true);
                    break;

                case WorkflowStepKind.UpdateRecord:
                    ValidateAssignments(step, stepPath, definition, issues, availableOutputs, requireAny: true);
                    if (step.Entity is { } updateEntity
                        && !string.Equals(updateEntity, definition.PrimaryEntity, StringComparison.OrdinalIgnoreCase))
                        issues.Add(new WorkflowValidationIssue("error", "WF021", $"{stepPath}.entity",
                            $"An update step can only target the primary entity ('{definition.PrimaryEntity}'), " +
                            $"but '{updateEntity}' was given.",
                            "Remove 'entity' (it defaults to the primary entity). To change a related " +
                            "record, use a child workflow on that table."));
                    break;

                case WorkflowStepKind.AssignRecord:
                    if (string.IsNullOrWhiteSpace(step.OwnerId))
                        issues.Add(new WorkflowValidationIssue("error", "WF030", $"{stepPath}.ownerId",
                            "An assign step needs a target owner.",
                            "Set 'ownerId' to a systemuser or team id and 'ownerType' accordingly."));
                    else if (!Guid.TryParse(step.OwnerId, out _))
                        issues.Add(new WorkflowValidationIssue("error", "WF031", $"{stepPath}.ownerId",
                            $"'{step.OwnerId}' is not a valid GUID.",
                            "Provide the owner id as a GUID."));
                    if (step.OwnerType is { } ot && ot is not ("systemuser" or "team"))
                        issues.Add(new WorkflowValidationIssue("error", "WF032", $"{stepPath}.ownerType",
                            $"Unknown owner type '{ot}'.",
                            "Use \"systemuser\" or \"team\"."));
                    break;

                case WorkflowStepKind.ChangeStatus:
                    if (step.State is null && step.Status is null)
                        issues.Add(new WorkflowValidationIssue("error", "WF040", stepPath,
                            "A change-status step needs at least a state or a status value.",
                            "Set 'state' (statecode) and/or 'status' (statuscode) to the numeric values " +
                            "of the target status. Look them up with describe_table."));
                    break;

                case WorkflowStepKind.CustomActivity:
                    ValidateCustomActivity(step, stepPath, definition, issues, availableOutputs, activities);
                    break;

                case WorkflowStepKind.StartChildWorkflow:
                    if (string.IsNullOrWhiteSpace(step.ChildWorkflowId))
                        issues.Add(new WorkflowValidationIssue("error", "WF050", $"{stepPath}.childWorkflowId",
                            "A child-workflow step needs the id of the workflow to start.",
                            "Set 'childWorkflowId'. The target must exist, be activated and have " +
                            "'subprocess' enabled (workflow_update with {\"subprocess\": true})."));
                    else if (!Guid.TryParse(step.ChildWorkflowId, out _))
                        issues.Add(new WorkflowValidationIssue("error", "WF051", $"{stepPath}.childWorkflowId",
                            $"'{step.ChildWorkflowId}' is not a valid GUID.",
                            "Provide the child workflow id as a GUID."));
                    break;

                case WorkflowStepKind.StopWorkflow:
                    if (step.Outcome is { } outcome && outcome is not ("succeeded" or "cancelled"))
                        issues.Add(new WorkflowValidationIssue("error", "WF060", $"{stepPath}.outcome",
                            $"Unknown outcome '{outcome}'.",
                            "Use \"succeeded\" or \"cancelled\"."));
                    if (step.Reason is { } reason)
                        ValidateValue(reason, $"{stepPath}.reason", definition, issues, availableOutputs);
                    break;
            }

            // Outputs of this step become referencable by later steps.
            if (step.Kind == WorkflowStepKind.CustomActivity && step.Outputs is { } outputs)
                foreach (var output in outputs)
                    availableOutputs.Add($"{step.StepId ?? PlaceholderId(step, i)}.{output}");
        }
    }

    /// <summary>
    /// Before building, step ids are not assigned yet. References are therefore validated against
    /// the parameter name alone, and a note explains the ordering rule.
    /// </summary>
    private static string PlaceholderId(WorkflowStep step, int index) =>
        $"{WorkflowStepKind.PrefixFor(step.Kind)}#{index}";

    /// <summary>
    /// The parameter name of a step-output reference. Both "&lt;stepId&gt;.&lt;Parameter&gt;" and the
    /// bare parameter name are accepted, matching how the builder resolves them — the step id cannot
    /// be known before building, so requiring it would be a rule callers cannot satisfy.
    /// </summary>
    private static string ParameterPart(string reference)
    {
        var parameter = reference.Contains('.') ? reference.Split('.', 2)[1] : reference;
        return parameter.Trim();
    }

    private static void ValidateCondition(
        WorkflowStep step, string path, WorkflowDefinition definition,
        List<WorkflowValidationIssue> issues, List<string> availableOutputs, bool insideStage,
        WorkflowActivityCatalog activities)
    {
        if (step.Conditions is null || step.Conditions.Count == 0)
        {
            issues.Add(new WorkflowValidationIssue("error", "WF070", $"{path}.conditions",
                "The condition has no comparisons.",
                "Add at least one entry to 'conditions' with attribute, operator and value."));
        }
        else
        {
            for (var i = 0; i < step.Conditions.Count; i++)
            {
                var condition = step.Conditions[i];
                var conditionPath = $"{path}.conditions[{i}]";

                var comparesOutput = !string.IsNullOrWhiteSpace(condition.StepOutput);

                if (string.IsNullOrWhiteSpace(condition.Attribute) && !comparesOutput)
                    issues.Add(new WorkflowValidationIssue("error", "WF071", $"{conditionPath}.attribute",
                        "The comparison has neither an attribute nor a step output.",
                        "Set 'attribute' to a field's logical name, or 'stepOutput' to " +
                        "\"<stepId>.<ParameterName>\" of an earlier custom activity step."));

                if (comparesOutput)
                {
                    var parameter = ParameterPart(condition.StepOutput!);
                    if (parameter.Length == 0)
                        issues.Add(new WorkflowValidationIssue("error", "WF077", $"{conditionPath}.stepOutput",
                            $"'{condition.StepOutput}' names no parameter.",
                            "Use the parameter name, e.g. \"isUserInTeam\", or qualify it as " +
                            "\"<stepId>.<ParameterName>\"."));
                    else if (!availableOutputs.Any(o => o.EndsWith("." + parameter, StringComparison.Ordinal)))
                        issues.Add(new WorkflowValidationIssue("error", "WF078", $"{conditionPath}.stepOutput",
                            $"No earlier custom activity step declares an output named '{parameter}'.",
                            "Add it to that step's 'outputs' list and place the step before this condition."));
                }

                // Reading a related record needs the lookup attribute that leads there.
                if (condition.Entity is { } conditionEntity
                    && !string.Equals(conditionEntity, definition.PrimaryEntity, StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(condition.Via))
                    issues.Add(new WorkflowValidationIssue("error", "WF079", $"{conditionPath}.via",
                        $"The comparison reads '{conditionEntity}', which is not the primary entity, " +
                        "but does not say how to get there.",
                        $"Add 'via' with the lookup attribute of {definition.PrimaryEntity} that points " +
                        $"to {conditionEntity}, e.g. \"opportunityid\"."));

                var op = WorkflowXamlBuilder.MapOperator(condition.Operator ?? string.Empty);
                if (!KnownOperators.Contains(op))
                    issues.Add(new WorkflowValidationIssue("error", "WF072", $"{conditionPath}.operator",
                        $"Unknown operator '{condition.Operator}'.",
                        $"Use one of: {string.Join(", ", KnownOperators)}."));

                var needsValue = !ValuelessOperators.Contains(op);
                if (needsValue && condition.Value is null)
                    issues.Add(new WorkflowValidationIssue("error", "WF073", $"{conditionPath}.value",
                        $"Operator '{op}' requires a value.",
                        "Add 'value', e.g. {\"kind\":\"literal\",\"literal\":\"Foo\"}."));
                if (!needsValue && condition.Value is not null)
                    issues.Add(new WorkflowValidationIssue("warning", "WF074", $"{conditionPath}.value",
                        $"Operator '{op}' takes no value; the given value is ignored.",
                        "Remove 'value' to make the intent clear."));

                if (condition.Value is not null)
                    ValidateValue(condition.Value, $"{conditionPath}.value", definition, issues, availableOutputs);
            }
        }

        var hasThen = step.Then is { Count: > 0 };
        var hasElse = step.Else is { Count: > 0 };

        if (!hasThen && !hasElse)
            issues.Add(new WorkflowValidationIssue("error", "WF075", path,
                "The condition has neither a 'then' nor an 'else' branch, so it does nothing.",
                "Add steps to 'then' (executed when the condition holds) or to 'else'."));

        if (hasThen)
            ValidateSteps(step.Then!, $"{path}.then", definition, issues, availableOutputs, insideStage,
                activities);
        if (hasElse)
            ValidateSteps(step.Else!, $"{path}.else", definition, issues, availableOutputs, insideStage,
                activities);

        if (step.Conditions is { Count: > 1 }
            && step.LogicalOperator is { } logical
            && logical is not ("And" or "Or" or "and" or "or"))
            issues.Add(new WorkflowValidationIssue("error", "WF076", $"{path}.logicalOperator",
                $"Unknown logical operator '{logical}'.",
                "Use \"And\" or \"Or\"."));
    }

    private static void ValidateCustomActivity(
        WorkflowStep step, string path, WorkflowDefinition definition,
        List<WorkflowValidationIssue> issues, List<string> availableOutputs,
        WorkflowActivityCatalog activities)
    {
        if (string.IsNullOrWhiteSpace(step.AssemblyQualifiedName))
        {
            issues.Add(new WorkflowValidationIssue("error", "WF080", $"{path}.assemblyQualifiedName",
                "A custom activity step needs the AssemblyQualifiedName of the code activity.",
                "Look it up with workflow_list_activities / workflow_get_activity_parameters and " +
                "copy the value verbatim — it contains the assembly's real PublicKeyToken."));
        }
        else
        {
            if (step.AssemblyQualifiedName.Contains("PublicKeyToken=null", StringComparison.OrdinalIgnoreCase))
                issues.Add(new WorkflowValidationIssue("warning", "WF081", $"{path}.assemblyQualifiedName",
                    "The AssemblyQualifiedName says PublicKeyToken=null. Signed assemblies have a real " +
                    "token, and the workflow will fail to load if it is wrong.",
                    "Take the value from workflow_get_activity_parameters, which reads it from " +
                    "customworkflowactivityinfo."));

            if (!step.AssemblyQualifiedName.Contains(',') )
                issues.Add(new WorkflowValidationIssue("error", "WF082", $"{path}.assemblyQualifiedName",
                    "The AssemblyQualifiedName is not assembly-qualified (no comma present).",
                    "Expected form: \"Namespace.Type, Assembly, Version=…, Culture=…, PublicKeyToken=…\"."));
        }

        // Known signature? Then the arguments are checked against it. Otherwise these four error
        // classes stay invisible until activation reports 0x80048455.
        var parameters = activities.Parameters(step.AssemblyQualifiedName);

        foreach (var (key, value) in step.Inputs ?? [])
        {
            if (string.IsNullOrWhiteSpace(key))
                issues.Add(new WorkflowValidationIssue("error", "WF083", $"{path}.inputs",
                    "An input argument has an empty name.",
                    "Use the parameter's DependencyPropertyName as the key (not its display name)."));
            ValidateValue(value, $"{path}.inputs['{key}']", definition, issues, availableOutputs);

            if (parameters is null || string.IsNullOrWhiteSpace(key))
                continue;

            var inputPath = $"{path}.inputs['{key}']";
            var parameter = activities.Parameter(step.AssemblyQualifiedName, key, "Input");

            if (parameter is null)
            {
                var known = parameters.Where(p => p.Direction == "Input").ToList();
                issues.Add(new WorkflowValidationIssue("error", "WF085", inputPath,
                    $"The activity has no input parameter '{key}'.",
                    known.Count == 0
                        ? "This activity takes no inputs. Remove 'inputs'."
                        : "Use one of its DependencyPropertyNames: " +
                          string.Join(", ", known.Select(Describe)) +
                          ". The display name is not the key."));
                continue;
            }

            CheckParameterType(parameter, value, inputPath, issues);
            CheckLookupTarget(parameter, value, inputPath, issues);
        }

        if (parameters is not null)
            foreach (var missing in parameters.Where(p =>
                         p.Direction == "Input" && p.IsRequired
                         && !(step.Inputs ?? []).Keys.Any(k =>
                             string.Equals(k, p.DependencyPropertyName, StringComparison.OrdinalIgnoreCase))))
                issues.Add(new WorkflowValidationIssue("error", "WF087", $"{path}.inputs",
                    $"The required input '{missing.DependencyPropertyName}' is not set.",
                    $"Add it to 'inputs' with dataType \"{missing.DataType ?? "String"}\"" +
                    (missing.EntityNames is { Count: > 0 } targets
                        ? $" pointing at {string.Join(" or ", targets)}."
                        : ".")));

        foreach (var output in step.Outputs ?? [])
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                issues.Add(new WorkflowValidationIssue("error", "WF084", $"{path}.outputs",
                    "An output name is empty.",
                    "List the DependencyPropertyName of each output you want to capture."));
                continue;
            }

            if (parameters is null)
                continue;

            if (activities.Parameter(step.AssemblyQualifiedName, output, "Output") is null)
            {
                var known = parameters.Where(p => p.Direction == "Output").ToList();
                issues.Add(new WorkflowValidationIssue("error", "WF089", $"{path}.outputs",
                    $"The activity has no output parameter '{output}'.",
                    known.Count == 0
                        ? "This activity returns nothing. Remove 'outputs'."
                        : "Use one of its DependencyPropertyNames: " +
                          string.Join(", ", known.Select(Describe)) + "."));
            }
        }
    }

    private static string Describe(WorkflowActivityParameter p) =>
        p.EntityNames is { Count: > 0 } targets
            ? $"{p.DependencyPropertyName} ({p.DataType ?? p.TypeName.Split(',')[0]} → {string.Join("/", targets)})"
            : $"{p.DependencyPropertyName} ({p.DataType ?? p.TypeName.Split(',')[0]})";

    /// <summary>
    /// The caller's <c>dataType</c> must describe the same CRM type as the parameter, because the
    /// XAML argument is typed from it and a mismatch is an invalid property bag.
    /// </summary>
    private static void CheckParameterType(
        WorkflowActivityParameter parameter, WorkflowValue value, string path,
        List<WorkflowValidationIssue> issues)
    {
        if (parameter.DataType is null)
        {
            issues.Add(new WorkflowValidationIssue("warning", "WF086", $"{path}.dataType",
                $"Parameter '{parameter.DependencyPropertyName}' has type '{parameter.TypeName.Split(',')[0]}', " +
                "which this server cannot map to a dataType, so the value cannot be type-checked.",
                "Configure this step in the Dataverse designer if activation rejects it."));
            return;
        }

        // Both sides are normalised, so "Lookup" and "EntityReference" count as the same request.
        var expected = WorkflowXamlBuilder.CrmPropertyType(parameter.DataType);
        var given = WorkflowXamlBuilder.CrmPropertyType(value.DataType);

        // An unset dataType means String by default — that is only a match if String is wanted.
        if (expected == given)
            return;

        issues.Add(new WorkflowValidationIssue("error", "WF086", $"{path}.dataType",
            value.DataType is null
                ? $"Parameter '{parameter.DependencyPropertyName}' is a {parameter.DataType}, but no " +
                  "dataType is given and it therefore defaults to String."
                : $"Parameter '{parameter.DependencyPropertyName}' is a {parameter.DataType}, but the " +
                  $"value declares dataType '{value.DataType}'.",
            $"Set dataType to \"{parameter.DataType}\"."));
    }

    /// <summary>
    /// A lookup parameter only accepts the entities named in its metadata; anything else fails at
    /// runtime. Checkable for fixed references, whose target is part of the literal.
    /// </summary>
    private static void CheckLookupTarget(
        WorkflowActivityParameter parameter, WorkflowValue value, string path,
        List<WorkflowValidationIssue> issues)
    {
        if (parameter.EntityNames is not { Count: > 0 } targets
            || value.Kind != WorkflowValueKind.Literal
            || string.IsNullOrWhiteSpace(value.Literal))
            return;

        var entity = value.Literal.Split(':')[0].Trim();
        if (entity.Length == 0 || targets.Contains(entity, StringComparer.OrdinalIgnoreCase))
            return;

        issues.Add(new WorkflowValidationIssue("error", "WF088", $"{path}.literal",
            $"Parameter '{parameter.DependencyPropertyName}' only accepts references to " +
            $"{string.Join(" or ", targets)}, but the value points at '{entity}'.",
            $"Use a record of {string.Join(" or ", targets)}, written as " +
            $"\"{targets[0]}:<guid>\"."));
    }

    private static void ValidateAssignments(
        WorkflowStep step, string path, WorkflowDefinition definition,
        List<WorkflowValidationIssue> issues, List<string> availableOutputs, bool requireAny)
    {
        if (step.Attributes is null || step.Attributes.Count == 0)
        {
            if (requireAny)
                issues.Add(new WorkflowValidationIssue("error", "WF090", $"{path}.attributes",
                    "The step sets no attributes, so it would write an empty record.",
                    "Add entries to 'attributes', each with 'attribute' and 'value'."));
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < step.Attributes.Count; i++)
        {
            var assignment = step.Attributes[i];
            var assignmentPath = $"{path}.attributes[{i}]";

            if (string.IsNullOrWhiteSpace(assignment.Attribute))
                issues.Add(new WorkflowValidationIssue("error", "WF091", $"{assignmentPath}.attribute",
                    "The assignment has no attribute name.",
                    "Set 'attribute' to the logical name of the field to write."));
            else if (!seen.Add(assignment.Attribute))
                issues.Add(new WorkflowValidationIssue("error", "WF092", $"{assignmentPath}.attribute",
                    $"Attribute '{assignment.Attribute}' is assigned more than once in this step.",
                    "Remove the duplicate — the last assignment would silently win."));

            ValidateValue(assignment.Value, $"{assignmentPath}.value", definition, issues, availableOutputs);
        }
    }

    private static void ValidateValue(
        WorkflowValue? value, string path, WorkflowDefinition definition,
        List<WorkflowValidationIssue> issues, List<string> availableOutputs)
    {
        if (value is null)
        {
            issues.Add(new WorkflowValidationIssue("error", "WF100", path,
                "The value is missing.",
                "Provide a value object, e.g. {\"kind\":\"literal\",\"literal\":\"Foo\"}."));
            return;
        }

        if (!WorkflowValueKind.All.Contains(value.Kind))
        {
            issues.Add(new WorkflowValidationIssue("error", "WF101", $"{path}.kind",
                $"Unknown value kind '{value.Kind}'.",
                $"Use one of: {string.Join(", ", WorkflowValueKind.All)}."));
            return;
        }

        if (value.DataType is { } dataType && !KnownDataTypes.Contains(dataType))
            issues.Add(new WorkflowValidationIssue("error", "WF102", $"{path}.dataType",
                $"Unknown data type '{dataType}'.",
                $"Use one of: {string.Join(", ", KnownDataTypes.Distinct())}."));

        switch (value.Kind)
        {
            case WorkflowValueKind.Literal:
                if (value.Literal is null)
                    issues.Add(new WorkflowValidationIssue("warning", "WF110", $"{path}.literal",
                        "The literal is null and will be written as an empty string.",
                        "Set 'literal' explicitly, or use a field value instead."));
                if (value.Fields is { Count: > 0 })
                    issues.Add(new WorkflowValidationIssue("warning", "WF111", $"{path}.fields",
                        "'fields' is ignored because kind is 'literal'.",
                        "Set kind to \"field\" if you meant to read from a field."));
                break;

            case WorkflowValueKind.Field:
                if (value.Fields is null || value.Fields.Count == 0)
                {
                    issues.Add(new WorkflowValidationIssue("error", "WF120", $"{path}.fields",
                        "A field value needs at least one field reference.",
                        "Add entries like \"lead.companyname\". Several are evaluated in order and " +
                        "the first non-empty one wins."));
                }
                else
                {
                    for (var i = 0; i < value.Fields.Count; i++)
                    {
                        var reference = value.Fields[i];
                        if (!Regex.IsMatch(reference ?? string.Empty, @"^[a-z_][a-z0-9_]*\.[a-z_][a-z0-9_]*$",
                                RegexOptions.IgnoreCase))
                            issues.Add(new WorkflowValidationIssue("error", "WF121", $"{path}.fields[{i}]",
                                $"'{reference}' is not a valid field reference.",
                                "Use the form \"entity.attribute\" with logical names, e.g. \"lead.companyname\"."));
                        else
                        {
                            var (entity, _) = WorkflowXamlBuilder.SplitFieldReference(reference!, definition.PrimaryEntity);
                            // Related records are readable one level deep, but only when 'via' names
                            // the lookup attribute leading there.
                            if (!string.Equals(entity, definition.PrimaryEntity, StringComparison.OrdinalIgnoreCase)
                                && string.IsNullOrWhiteSpace(value.Via))
                                issues.Add(new WorkflowValidationIssue("error", "WF122", $"{path}.fields[{i}]",
                                    $"'{reference}' points at '{entity}', which is not the primary entity " +
                                    $"('{definition.PrimaryEntity}'), but 'via' is missing.",
                                    $"Add 'via' with the lookup attribute of {definition.PrimaryEntity} that " +
                                    $"points to {entity} (e.g. \"opportunityid\"). Only one level of " +
                                    "traversal is possible; for deeper paths use a child workflow."));
                        }
                    }
                }
                break;

            case WorkflowValueKind.StepOutput:
                if (string.IsNullOrWhiteSpace(value.StepOutput))
                {
                    issues.Add(new WorkflowValidationIssue("error", "WF130", $"{path}.stepOutput",
                        "A stepOutput value needs a reference.",
                        "Use the parameter name, e.g. \"Domain\", or qualify it as " +
                        "\"<stepId>.<ParameterName>\"."));
                }
                else if (ParameterPart(value.StepOutput).Length == 0)
                {
                    issues.Add(new WorkflowValidationIssue("error", "WF131", $"{path}.stepOutput",
                        $"'{value.StepOutput}' names no parameter.",
                        "Use the parameter name, e.g. \"Domain\", or qualify it as " +
                        "\"<stepId>.<ParameterName>\"."));
                }
                else
                {
                    var parameter = ParameterPart(value.StepOutput);
                    var known = availableOutputs.Any(o => o.EndsWith("." + parameter, StringComparison.Ordinal));
                    if (!known)
                        issues.Add(new WorkflowValidationIssue("error", "WF132", $"{path}.stepOutput",
                            $"No earlier custom activity step declares an output named '{parameter}'.",
                            "Add the parameter to the 'outputs' list of the custom activity step, and " +
                            "make sure that step comes before this one."));
                }
                break;
        }
    }

    // ---------------------------------------------------------------- output checks

    /// <summary>
    /// Re-checks generated XAML: well-formedness, variable declarations and unique step ids. This is
    /// the safety net against builder defects and runs before anything is written to Dataverse.
    /// </summary>
    public static WorkflowValidationResult ValidateGeneratedXaml(string xaml)
    {
        var issues = new List<WorkflowValidationIssue>();

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xaml);
        }
        catch (System.Xml.XmlException ex)
        {
            issues.Add(new WorkflowValidationIssue("error", "WF200", "$.xaml",
                $"The generated XAML is not well-formed: {ex.Message}",
                "This is an internal builder defect. Please report the definition that triggered it."));
            return new WorkflowValidationResult(false, issues);
        }

        // Every [Variable] reference must be declared somewhere.
        var declared = doc.Descendants()
            .Where(e => e.Name.LocalName == "Variable")
            .Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

        var referenced = Regex.Matches(xaml, @"\[([A-Za-z_][A-Za-z0-9_]*)\]")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(name => name is not ("Nothing" or "True" or "False"))
            .ToList();

        foreach (var name in referenced.Where(r => !declared.Contains(r)))
            issues.Add(new WorkflowValidationIssue("error", "WF201", "$.xaml",
                $"Variable '{name}' is referenced but never declared.",
                "This is an internal builder defect. Please report the definition that triggered it."));

        // Step ids must be unique across the document.
        var stepIds = doc.Descendants()
            .Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "DisplayName")?.Value)
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => WorkflowXamlParser.SplitDisplayName(d).StepId)
            .Where(id => id is not null && Regex.IsMatch(id, @"^[A-Za-z]+Step\d+$"))
            .Select(id => id!)
            .ToList();

        foreach (var duplicate in stepIds.GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 2))
            issues.Add(new WorkflowValidationIssue("error", "WF202", "$.xaml",
                $"Step id '{duplicate.Key}' occurs {duplicate.Count()} times.",
                "Step ids must be unique (a step and its inner activity may share one). " +
                "This is an internal builder defect."));

        return new WorkflowValidationResult(issues.All(i => i.Severity != "error"), issues);
    }
}
