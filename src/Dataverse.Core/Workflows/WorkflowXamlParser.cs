namespace Dataverse.Core.Workflows;

using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <param name="Definition">The reconstructed logic.</param>
/// <param name="FullyUnderstood">
/// True when every activity was recognised. When false, <see cref="Unrecognised"/> lists what was
/// skipped — rebuilding from this definition would silently drop those parts, so callers must not
/// write it back.
/// </param>
public sealed record WorkflowParseResult(
    WorkflowDefinition Definition,
    bool FullyUnderstood,
    IReadOnlyList<string> Unrecognised,
    IReadOnlyList<string> Notes);

/// <summary>
/// Reads Classic Workflow XAML back into a <see cref="WorkflowDefinition"/>.
/// </summary>
/// <remarks>
/// Element names are matched by local name, so the parser does not depend on namespace prefixes.
/// It is deliberately conservative: anything it cannot map is reported rather than guessed.
/// </remarks>
public static class WorkflowXamlParser
{
    public static WorkflowParseResult Parse(string? xaml, string primaryEntity)
    {
        var unrecognised = new List<string>();
        var notes = new List<string>();

        if (string.IsNullOrWhiteSpace(xaml))
            return new WorkflowParseResult(
                new WorkflowDefinition { PrimaryEntity = primaryEntity },
                true, unrecognised, ["Workflow has no XAML (empty draft)."]);

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xaml);
        }
        catch (System.Xml.XmlException ex)
        {
            return new WorkflowParseResult(
                new WorkflowDefinition { PrimaryEntity = primaryEntity },
                false, [$"XAML is not well-formed: {ex.Message}"], notes);
        }

        var workflow = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Workflow");
        if (workflow is null)
            return new WorkflowParseResult(
                new WorkflowDefinition { PrimaryEntity = primaryEntity },
                false, ["No <mxswa:Workflow> root element found."], notes);

        var steps = ParseSteps(workflow.Elements(), primaryEntity, unrecognised, notes);

        return new WorkflowParseResult(
            new WorkflowDefinition { PrimaryEntity = primaryEntity, Steps = steps },
            unrecognised.Count == 0, unrecognised, notes);
    }

    private static List<WorkflowStep> ParseSteps(
        IEnumerable<XElement> elements, string primaryEntity, List<string> unrecognised, List<string> notes)
    {
        var steps = new List<WorkflowStep>();

        foreach (var element in elements)
        {
            switch (element.Name.LocalName)
            {
                case "Workflow.Variables":
                case "Persist":
                    continue; // structural, carries no logic

                case "Sequence":
                    steps.AddRange(ParseSequence(element, primaryEntity, unrecognised, notes));
                    continue;

                case "SetState":
                    steps.Add(ParseSetState(element));
                    continue;

                case "ActivityReference":
                    var step = ParseActivityReference(element, primaryEntity, unrecognised, notes);
                    if (step is not null)
                        steps.Add(step);
                    continue;

                default:
                    unrecognised.Add($"Unhandled element <{element.Name.LocalName}>");
                    continue;
            }
        }

        return steps;
    }

    /// <summary>A Sequence wraps exactly one logical step (update/create/assign/childWorkflow/stop).</summary>
    private static List<WorkflowStep> ParseSequence(
        XElement sequence, string primaryEntity, List<string> unrecognised, List<string> notes)
    {
        var (stepId, description) = SplitDisplayName(Attr(sequence, "DisplayName"));
        var result = new List<WorkflowStep>();

        var update = Child(sequence, "UpdateEntity");
        var create = Child(sequence, "CreateEntity");
        var assign = Child(sequence, "AssignEntity");
        var child = Child(sequence, "StartChildWorkflow");
        var terminate = Child(sequence, "TerminateWorkflow");

        if (update is not null)
        {
            result.Add(new WorkflowStep
            {
                Kind = WorkflowStepKind.UpdateRecord,
                StepId = stepId,
                Description = description,
                Entity = Attr(update, "EntityName"),
                Attributes = ParseAttributeAssignments(sequence, primaryEntity, notes)
            });
        }
        else if (create is not null)
        {
            result.Add(new WorkflowStep
            {
                Kind = WorkflowStepKind.CreateRecord,
                StepId = stepId,
                Description = description,
                Entity = Attr(create, "EntityName"),
                Attributes = ParseAttributeAssignments(sequence, primaryEntity, notes)
            });
        }
        else if (assign is not null)
        {
            result.Add(new WorkflowStep
            {
                Kind = WorkflowStepKind.AssignRecord,
                StepId = stepId,
                Description = description,
                Entity = Attr(assign, "EntityName"),
                OwnerId = ExtractGuid(Attr(assign, "Owner"))
            });
        }
        else if (child is not null)
        {
            result.Add(new WorkflowStep
            {
                Kind = WorkflowStepKind.StartChildWorkflow,
                StepId = stepId,
                Description = description,
                Entity = Attr(child, "EntityName"),
                ChildWorkflowId = Attr(child, "WorkflowId")
            });
        }
        else if (terminate is not null)
        {
            result.Add(new WorkflowStep
            {
                Kind = WorkflowStepKind.StopWorkflow,
                StepId = stepId,
                Description = description,
                Outcome = (Attr(terminate, "Exception") ?? string.Empty).Contains("Canceled") ? "cancelled" : "succeeded",
                Reason = new WorkflowValue { Kind = WorkflowValueKind.Literal, Literal = FindLiteral(sequence) }
            });
        }
        else if (Child(sequence, "SendEmail") is not null)
        {
            result.Add(new WorkflowStep
            {
                Kind = WorkflowStepKind.SendEmail,
                StepId = stepId,
                Description = description,
                Entity = "email"
            });
            notes.Add($"{stepId}: send-email configuration (template, recipients) is not parsed.");
        }
        else
        {
            unrecognised.Add($"Sequence '{stepId ?? "?"}' contains no recognised action activity");
        }

        return result;
    }

    private static WorkflowStep ParseSetState(XElement element)
    {
        var (stepId, description) = SplitDisplayName(Attr(element, "DisplayName"));
        return new WorkflowStep
        {
            Kind = WorkflowStepKind.ChangeStatus,
            StepId = stepId,
            Description = description,
            Entity = Attr(element, "EntityName"),
            State = OptionSetValueOf(element, "SetState.State"),
            Status = OptionSetValueOf(element, "SetState.Status")
        };
    }

    private static WorkflowStep? ParseActivityReference(
        XElement element, string primaryEntity, List<string> unrecognised, List<string> notes)
    {
        var aqn = Attr(element, "AssemblyQualifiedName") ?? string.Empty;
        var (stepId, description) = SplitDisplayName(Attr(element, "DisplayName"));
        var typeName = aqn.Split(',')[0].Trim();

        // ConditionSequence => condition or wait step
        if (typeName.EndsWith(".ConditionSequence", StringComparison.Ordinal))
            return ParseConditionSequence(element, stepId, description, primaryEntity, unrecognised, notes);

        // Composite => stage, or the wrapper around a custom activity / SDK message
        if (typeName.EndsWith(".Composite", StringComparison.Ordinal))
        {
            var activities = ActivitiesOf(element).ToList();

            var sdk = activities.FirstOrDefault(a => a.Name.LocalName == "InvokeSdkMessageActivity");
            if (sdk is not null)
            {
                notes.Add($"{stepId}: 'perform action' steps are reported but cannot be regenerated.");
                return new WorkflowStep
                {
                    Kind = WorkflowStepKind.PerformAction,
                    StepId = stepId,
                    Description = description
                };
            }

            var codeActivity = activities.FirstOrDefault(a =>
                a.Name.LocalName == "ActivityReference" && !IsPlatformActivity(Attr(a, "AssemblyQualifiedName")));

            if (codeActivity is not null)
                return ParseCustomActivity(element, codeActivity, stepId, description,
                    unrecognised, notes);

            // Otherwise treat it as a stage container.
            var children = ParseSteps(activities, primaryEntity, unrecognised, notes);
            return new WorkflowStep
            {
                Kind = WorkflowStepKind.Stage,
                StepId = stepId,
                Description = description,
                Children = children
            };
        }

        // Helper activities appear inside steps and are consumed there; at top level they are noise.
        if (typeName.EndsWith(".EvaluateExpression", StringComparison.Ordinal)
            || typeName.EndsWith(".ConvertCrmXrmTypes", StringComparison.Ordinal)
            || typeName.EndsWith(".EvaluateCondition", StringComparison.Ordinal)
            || typeName.EndsWith(".EvaluateLogicalCondition", StringComparison.Ordinal))
            return null;

        unrecognised.Add($"Unhandled ActivityReference '{typeName}' (DisplayName '{stepId}')");
        return null;
    }

    private static WorkflowStep ParseConditionSequence(
        XElement element, string? stepId, string? description, string primaryEntity,
        List<string> unrecognised, List<string> notes)
    {
        var isWait = string.Equals(ArgumentValue(element, "Wait"), "True", StringComparison.OrdinalIgnoreCase);
        var activities = ActivitiesOf(element).ToList();

        // Reconstruct comparisons: each EvaluateCondition refers to a left operand variable that a
        // GetEntityProperty filled, and parameters holding the right-hand side.
        var reads = activities
            .Where(a => a.Name.LocalName == "GetEntityProperty")
            .ToDictionary(
                a => (Attr(a, "Value") ?? string.Empty).Trim('[', ']'),
                a => (Entity: Attr(a, "EntityName"), Attribute: Attr(a, "Attribute"),
                      Via: ViaOf(Attr(a, "Entity"))));

        var literals = activities
            .Where(a => a.Name.LocalName == "ActivityReference"
                        && (Attr(a, "AssemblyQualifiedName") ?? "").Contains(".EvaluateExpression"))
            .ToDictionary(
                a => (OutArgumentValue(a, "Result") ?? string.Empty).Trim('[', ']'),
                a => ExtractCreateCrmTypeLiteral(ArgumentValue(a, "Parameters")));

        var conditions = new List<WorkflowCondition>();

        foreach (var evaluate in activities.Where(a => a.Name.LocalName == "ActivityReference"
                     && (Attr(a, "AssemblyQualifiedName") ?? "").Contains(".EvaluateCondition")))
        {
            var operand = (ArgumentValue(evaluate, "Operand") ?? string.Empty).Trim('[', ']');
            var op = ArgumentValue(evaluate, "ConditionOperator") ?? "Equal";
            var parameters = ArgumentValue(evaluate, "Parameters") ?? string.Empty;

            reads.TryGetValue(operand, out var left);

            WorkflowValue? value = null;
            var rightVar = FirstVariableIn(parameters);
            if (rightVar is not null)
            {
                if (literals.TryGetValue(rightVar, out var literal) && literal is not null)
                    value = new WorkflowValue { Kind = WorkflowValueKind.Literal, Literal = literal };
                else if (reads.TryGetValue(rightVar, out var rightField))
                    value = new WorkflowValue
                    {
                        Kind = WorkflowValueKind.Field,
                        Fields = [$"{rightField.Entity}.{rightField.Attribute}"],
                        Via = rightField.Via
                    };
            }

            // No GetEntityProperty behind the operand? Then it is the output variable of an earlier
            // code activity — recognising it keeps the comparison intact on a rewrite.
            var stepOutput = StepOutputOf(operand);

            conditions.Add(new WorkflowCondition
            {
                Entity = stepOutput is null ? left.Entity ?? primaryEntity : null,
                Attribute = stepOutput is null ? left.Attribute ?? "?" : string.Empty,
                StepOutput = stepOutput,
                Via = left.Via,
                Operator = op,
                Value = value
            });
        }

        var logical = activities.FirstOrDefault(a => a.Name.LocalName == "ActivityReference"
            && (Attr(a, "AssemblyQualifiedName") ?? "").Contains(".EvaluateLogicalCondition"));

        // Branches: the first ConditionBranch is "then", a second one with Condition=True is "else".
        var branches = activities.Where(a => a.Name.LocalName == "ActivityReference"
            && (Attr(a, "AssemblyQualifiedName") ?? "").Contains(".ConditionBranch")).ToList();

        List<WorkflowStep>? then = null;
        List<WorkflowStep>? @else = null;

        for (var i = 0; i < branches.Count; i++)
        {
            var inner = PropertyElement(branches[i], "Then");
            var branchSteps = inner is null
                ? []
                : ParseSteps(ActivitiesOf(inner), primaryEntity, unrecognised, notes);

            var isElseBranch = string.Equals(ArgumentValue(branches[i], "Condition"), "True", StringComparison.OrdinalIgnoreCase);
            if (isElseBranch && i > 0)
                @else = branchSteps;
            else if (then is null)
                then = branchSteps;
            else
                notes.Add($"{stepId}: additional condition branch found; only the first two are modelled.");
        }

        return new WorkflowStep
        {
            Kind = isWait ? WorkflowStepKind.Wait : WorkflowStepKind.Condition,
            StepId = stepId,
            Description = description,
            Conditions = conditions,
            LogicalOperator = logical is null ? null : ArgumentValue(logical, "LogicalOperator"),
            Then = then,
            Else = @else
        };
    }

    /// <remarks>
    /// The value-preparation activities in the composite are not reduced back to a
    /// <see cref="WorkflowValue"/>, so an input argument is only reported as the expression it is.
    /// That makes the reading incomplete on purpose: rewriting such a workflow would turn the
    /// expression into a string literal and silently change what the step does. Hence the
    /// <paramref name="unrecognised"/> entry, which sets <c>fullyUnderstood</c> to false.
    /// </remarks>
    private static WorkflowStep ParseCustomActivity(
        XElement composite, XElement codeActivity, string? stepId, string? description,
        List<string> unrecognised, List<string> notes)
    {
        var inputs = new Dictionary<string, WorkflowValue>();
        var outputs = new List<string>();

        var argumentsElement = codeActivity.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "ActivityReference.Arguments");

        foreach (var argument in argumentsElement?.Elements() ?? [])
        {
            var key = Attr(argument, "Key") ?? argument.Attributes()
                .FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;
            if (key is null)
                continue;

            if (argument.Name.LocalName == "OutArgument")
                outputs.Add(key);
            else
                // The concrete source expression is reported as a note rather than reverse-engineered.
                inputs[key] = new WorkflowValue
                {
                    Kind = WorkflowValueKind.Literal,
                    Literal = argument.Value.Trim()
                };
        }

        if (inputs.Count > 0)
        {
            notes.Add($"{stepId}: input arguments are shown as raw expressions; " +
                      "they are not reduced back to field references.");
            unrecognised.Add($"{stepId}: input arguments of a code activity " +
                             $"({string.Join(", ", inputs.Keys)}). Change this step in the designer — " +
                             "rewriting the workflow from this reading would replace the expressions " +
                             "with string literals.");
        }

        return new WorkflowStep
        {
            Kind = WorkflowStepKind.CustomActivity,
            StepId = stepId,
            Description = description,
            AssemblyQualifiedName = Attr(codeActivity, "AssemblyQualifiedName"),
            Inputs = inputs.Count > 0 ? inputs : null,
            Outputs = outputs.Count > 0 ? outputs : null
        };
    }

    private static List<WorkflowAttributeAssignment> ParseAttributeAssignments(
        XElement scope, string primaryEntity, List<string> notes)
    {
        var result = new List<WorkflowAttributeAssignment>();

        var literals = scope.Descendants()
            .Where(a => a.Name.LocalName == "ActivityReference"
                        && (Attr(a, "AssemblyQualifiedName") ?? "").Contains(".EvaluateExpression"))
            .ToDictionary(
                a => (OutArgumentValue(a, "Result") ?? string.Empty).Trim('[', ']'),
                a => new
                {
                    Literal = ExtractCreateCrmTypeLiteral(ArgumentValue(a, "Parameters")),
                    Operator = ArgumentValue(a, "ExpressionOperator"),
                    Parameters = ArgumentValue(a, "Parameters")
                });

        var reads = scope.Descendants()
            .Where(a => a.Name.LocalName == "GetEntityProperty")
            .ToDictionary(
                a => (Attr(a, "Value") ?? string.Empty).Trim('[', ']'),
                a => (Reference: $"{Attr(a, "EntityName")}.{Attr(a, "Attribute")}",
                      Via: ViaOf(Attr(a, "Entity"))));

        foreach (var set in scope.Descendants().Where(e => e.Name.LocalName == "SetEntityProperty"))
        {
            var attribute = Attr(set, "Attribute") ?? "?";
            var sourceVar = (Attr(set, "Value") ?? string.Empty).Trim('[', ']');

            WorkflowValue value;
            if (literals.TryGetValue(sourceVar, out var expression))
            {
                if (expression.Operator == "SelectFirstNonNull")
                {
                    var sources = VariablesIn(expression.Parameters)
                        .Select(v => reads.TryGetValue(v, out var f) ? f : default)
                        .Where(f => f.Reference is not null)
                        .ToList();

                    var fallback = VariablesIn(expression.Parameters)
                        .Select(v => literals.TryGetValue(v, out var l) ? l.Literal : null)
                        .FirstOrDefault(l => l is not null);

                    value = sources.Count > 0
                        ? new WorkflowValue
                        {
                            Kind = WorkflowValueKind.Field,
                            Fields = [.. sources.Select(f => f.Reference)],
                            // One 'via' per value; the first related read determines it.
                            Via = sources.Select(f => f.Via).FirstOrDefault(v => v is not null),
                            Fallback = fallback
                        }
                        : new WorkflowValue { Kind = WorkflowValueKind.Literal, Literal = fallback };
                }
                else
                {
                    value = new WorkflowValue { Kind = WorkflowValueKind.Literal, Literal = expression.Literal };
                }
            }
            else
            {
                value = new WorkflowValue { Kind = WorkflowValueKind.Literal, Literal = null };
                notes.Add($"Attribute '{attribute}': value expression could not be resolved.");
            }

            result.Add(new WorkflowAttributeAssignment { Attribute = attribute, Value = value });
        }

        return result;
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsPlatformActivity(string? aqn) =>
        aqn is not null && aqn.Contains("Microsoft.Crm.Workflow", StringComparison.Ordinal);

    private static IEnumerable<XElement> ActivitiesOf(XElement activityReference)
    {
        var properties = activityReference.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "ActivityReference.Properties");

        var collection = properties?.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Collection"
                                 && KeyOf(e) == "Activities");

        return collection?.Elements() ?? [];
    }

    private static XElement? PropertyElement(XElement activityReference, string key)
    {
        var properties = activityReference.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "ActivityReference.Properties");
        return properties?.Elements().FirstOrDefault(e => KeyOf(e) == key && e.Name.LocalName != "Null");
    }

    private static string? KeyOf(XElement element) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;

    private static string? ArgumentValue(XElement activityReference, string key)
    {
        var arguments = activityReference.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "ActivityReference.Arguments");
        var argument = arguments?.Elements().FirstOrDefault(e => KeyOf(e) == key && e.Name.LocalName == "InArgument");
        if (argument is null)
            return null;

        // Either simple text or a nested ReferenceLiteral carrying Value=".."
        var nested = argument.Descendants().FirstOrDefault(d => d.Attribute("Value") is not null);
        return nested?.Attribute("Value")?.Value ?? argument.Value.Trim();
    }

    private static string? OutArgumentValue(XElement activityReference, string key)
    {
        var arguments = activityReference.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "ActivityReference.Arguments");
        return arguments?.Elements()
            .FirstOrDefault(e => KeyOf(e) == key && e.Name.LocalName == "OutArgument")?.Value.Trim();
    }

    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? Attr(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    /// <summary>
    /// The lookup attribute out of a related-record read, i.e. "opportunityid" from
    /// <c>[InputEntities("related_opportunityid#opportunity")]</c>. Null for the primary record.
    /// </summary>
    private static string? ViaOf(string? entityExpression)
    {
        if (string.IsNullOrWhiteSpace(entityExpression))
            return null;

        var match = Regex.Match(entityExpression, @"related_([A-Za-z0-9_]+)#");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// "&lt;stepId&gt;.&lt;Parameter&gt;" out of a code activity's output variable
    /// <c>CustomActivityStep9isUserInTeam_localParameter</c>. Null for anything else.
    /// </summary>
    private static string? StepOutputOf(string? variable)
    {
        if (string.IsNullOrWhiteSpace(variable))
            return null;

        var match = Regex.Match(variable, @"^([A-Za-z]+Step\d+)(.+)_localParameter$");
        return match.Success ? $"{match.Groups[1].Value}.{match.Groups[2].Value}" : null;
    }

    private static int? OptionSetValueOf(XElement setState, string propertyLocalName)
    {
        var property = setState.Elements().FirstOrDefault(e => e.Name.LocalName == propertyLocalName);
        var optionSet = property?.Descendants().FirstOrDefault(d => d.Name.LocalName == "OptionSetValue");
        var raw = optionSet?.Attribute("Value")?.Value;
        return int.TryParse(raw, out var v) ? v : null;
    }

    /// <summary>"UpdateStep6: Update Lead.Domain" -> ("UpdateStep6", "Update Lead.Domain")</summary>
    public static (string? StepId, string? Description) SplitDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return (null, null);

        var index = displayName.IndexOf(':');
        return index < 0
            ? (displayName.Trim(), null)
            : (displayName[..index].Trim(), displayName[(index + 1)..].Trim());
    }

    /// <summary>Pulls the literal out of a CreateCrmType parameter array.</summary>
    public static string? ExtractCreateCrmTypeLiteral(string? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
            return null;

        var match = Regex.Match(parameters, "WorkflowPropertyType\\.\\w+\\s*,\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? FindLiteral(XElement scope) =>
        scope.Descendants()
            .Where(a => a.Name.LocalName == "ActivityReference")
            .Select(a => ExtractCreateCrmTypeLiteral(ArgumentValue(a, "Parameters")))
            .FirstOrDefault(l => l is not null);

    private static string? ExtractGuid(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;
        var match = Regex.Match(expression, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return match.Success ? match.Value : null;
    }

    /// <summary>Variable names inside "[New Object() { A, B, C }]".</summary>
    public static List<string> VariablesIn(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return [];

        var braces = Regex.Match(expression, @"\{(.*)\}");
        if (!braces.Success)
            return [];

        return braces.Groups[1].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(part => Regex.IsMatch(part, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            .ToList();
    }

    private static string? FirstVariableIn(string? expression) => VariablesIn(expression).FirstOrDefault();
}
