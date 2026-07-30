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
                // The reason is a full value, not just a constant: this workflow builds one by
                // concatenating text with a field.
                Reason = ReadTerminateReason(terminate, sequence)
            });
        }
        else if (Child(sequence, "SendEmail") is not null)
        {
            result.Add(new WorkflowStep
            {
                Kind = WorkflowStepKind.SendEmail,
                StepId = stepId,
                Description = description,
                Entity = "email",
                Attributes = ParseAttributeAssignments(sequence, primaryEntity, notes)
            });
        }
        else
        {
            unrecognised.Add($"Sequence '{stepId ?? "?"}' contains no recognised action activity");
        }

        return result;
    }

    /// <summary>The reason text of a TerminateWorkflow, resolved through the preparation chain.</summary>
    private static WorkflowValue ReadTerminateReason(XElement terminate, XElement scope)
    {
        var variable = SourceVariableOf(Attr(terminate, "Reason"));
        var resolved = variable is null ? null : ValueChain.Of(scope).Resolve(variable, "String");

        return resolved
               ?? new WorkflowValue { Kind = WorkflowValueKind.Literal, Literal = FindLiteral(scope) };
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
        var chain = ValueChain.Of(element);

        // Reconstruct comparisons: each EvaluateCondition refers to a left operand variable that a
        // GetEntityProperty filled, and parameters holding the right-hand side.
        var reads = activities
            .Where(a => a.Name.LocalName == "GetEntityProperty")
            .ToDictionary(
                a => (Attr(a, "Value") ?? string.Empty).Trim('[', ']'),
                a => (Entity: Attr(a, "EntityName"), Attribute: Attr(a, "Attribute"),
                      Via: ViaOf(Attr(a, "Entity")),
                      Origin: OriginOf(Attr(a, "Entity"))));

        var literals = activities
            .Where(a => a.Name.LocalName == "ActivityReference"
                        && (Attr(a, "AssemblyQualifiedName") ?? "").Contains(".EvaluateExpression"))
            .ToDictionary(
                a => (OutArgumentValue(a, "Result") ?? string.Empty).Trim('[', ']'),
                a => (Literal: ExtractCreateCrmTypeLiteral(ArgumentValue(a, "Parameters")),
                      DataType: CreateCrmTypeDataType(ArgumentValue(a, "Parameters"))));

        // The activities appear in execution order: the comparisons of a case, then its
        // ConditionBranch. Walking them in that order assigns every comparison to the right case —
        // no need to decode the branch-based variable names.
        var branches = new List<WorkflowConditionBranch>();
        List<WorkflowStep>? @else = null;

        var pending = new List<WorkflowCondition>();
        string? pendingLogical = null;

        foreach (var activity in activities)
        {
            var aqn = Attr(activity, "AssemblyQualifiedName") ?? string.Empty;

            if (activity.Name.LocalName != "ActivityReference")
                continue;

            if (aqn.Contains(".EvaluateCondition"))
            {
                pending.Add(ReadComparison(activity, reads, literals, chain, primaryEntity));
                continue;
            }

            if (aqn.Contains(".EvaluateLogicalCondition"))
            {
                pendingLogical ??= ArgumentValue(activity, "LogicalOperator");
                continue;
            }

            if (!aqn.Contains(".ConditionBranch"))
                continue;

            var inner = PropertyElement(activity, "Then");
            var branchSteps = inner is null
                ? []
                : ParseSteps(ActivitiesOf(inner), primaryEntity, unrecognised, notes);

            // Condition="True" with no comparisons of its own is the default case.
            var isElseBranch =
                string.Equals(ArgumentValue(activity, "Condition"), "True", StringComparison.OrdinalIgnoreCase)
                && pending.Count == 0;

            if (isElseBranch)
            {
                @else = branchSteps;
            }
            else
            {
                branches.Add(new WorkflowConditionBranch
                {
                    BranchId = SplitDisplayName(Attr(activity, "DisplayName")).StepId,
                    Conditions = [.. pending],
                    LogicalOperator = pendingLogical,
                    Steps = branchSteps
                });
            }

            pending.Clear();
            pendingLogical = null;
        }

        var single = branches.Count == 1 ? branches[0] : null;

        return new WorkflowStep
        {
            Kind = isWait ? WorkflowStepKind.Wait : WorkflowStepKind.Condition,
            StepId = stepId,
            Description = description,
            // A single case keeps the short form, so simple conditions read as before.
            Conditions = single?.Conditions,
            LogicalOperator = single?.LogicalOperator,
            Then = single?.Steps,
            Branches = single is null ? branches : null,
            Else = @else
        };
    }

    /// <summary>One EvaluateCondition back into a comparison.</summary>
    private static WorkflowCondition ReadComparison(
        XElement evaluate,
        Dictionary<string, (string? Entity, string? Attribute, string? Via,
            (string? FromStep, string? FromStepOutput) Origin)> reads,
        Dictionary<string, (string? Literal, string? DataType)> literals,
        ValueChain chain,
        string primaryEntity)
    {
        var operand = (ArgumentValue(evaluate, "Operand") ?? string.Empty).Trim('[', ']');
        var op = ArgumentValue(evaluate, "ConditionOperator") ?? "Equal";
        var parameters = ArgumentValue(evaluate, "Parameters") ?? string.Empty;

        reads.TryGetValue(operand, out var left);

        WorkflowValue? value = null;
        var rightVars = VariablesIn(parameters);

        // In / NotIn compare against a set, so every constant in the array belongs to the comparison.
        var constants = rightVars
            .Select(v => literals.TryGetValue(v, out var l) ? l : default)
            .Where(l => l.Literal is not null)
            .ToList();

        // The declared type comes from the CreateCrmType marker, not from the caller.
        var constantType = constants.Select(c => c.DataType).FirstOrDefault(t => t is not null);

        if (constants.Count > 1)
            value = new WorkflowValue
            {
                Kind = WorkflowValueKind.Literal,
                DataType = constantType,
                Literals = [.. constants.Select(c => c.Literal!)]
            };
        else if (rightVars.Count > 0)
        {
            var rightVar = rightVars[0];
            if (constants.Count == 1)
                value = new WorkflowValue
                {
                    Kind = WorkflowValueKind.Literal,
                    DataType = constantType,
                    Literal = constants[0].Literal
                };
            else if (reads.TryGetValue(rightVar, out var rightField))
                value = new WorkflowValue
                {
                    Kind = WorkflowValueKind.Field,
                    Fields = [$"{rightField.Entity}.{rightField.Attribute}"],
                    Via = rightField.Via,
                    FromStep = rightField.Origin.FromStep,
                    FromStepOutput = rightField.Origin.FromStepOutput
                };
            else if (chain.Resolve(rightVar, null) is { } resolved)
                // Anything else the preparation chain can explain — "now", most importantly.
                value = resolved;
        }

        // No GetEntityProperty behind the operand? Then it is the output variable of an earlier
        // code activity — recognising it keeps the comparison intact on a rewrite.
        var stepOutput = StepOutputOf(operand);

        return new WorkflowCondition
        {
            Entity = stepOutput is null ? left.Entity ?? primaryEntity : null,
            Attribute = stepOutput is null ? left.Attribute ?? "?" : string.Empty,
            StepOutput = stepOutput,
            Via = left.Via,
            FromStep = left.Origin.FromStep,
            FromStepOutput = left.Origin.FromStepOutput,
            Operator = op,
            Value = value
        };
    }

    /// <remarks>
    /// An input argument holds no value, only a reference to the end of a preparation chain —
    /// <c>[DirectCast(Step1_1_converted, Microsoft.Xrm.Sdk.EntityReference)]</c>. What the input
    /// really is follows from the activities before it in the same composite, so
    /// <see cref="ValueChain"/> walks that chain backwards. An argument that cannot be resolved is
    /// reported as unrecognised instead of being passed off as a string literal, because rewriting
    /// the workflow from such a reading would silently change what the step does.
    /// </remarks>
    private static WorkflowStep ParseCustomActivity(
        XElement composite, XElement codeActivity, string? stepId, string? description,
        List<string> unrecognised, List<string> notes)
    {
        var inputs = new Dictionary<string, WorkflowValue>();
        var outputs = new List<string>();
        var unresolved = new List<string>();

        var argumentsElement = codeActivity.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "ActivityReference.Arguments");

        var chain = ValueChain.Of(composite);

        foreach (var argument in argumentsElement?.Elements() ?? [])
        {
            var key = Attr(argument, "Key") ?? argument.Attributes()
                .FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;
            if (key is null)
                continue;

            if (argument.Name.LocalName == "OutArgument")
            {
                outputs.Add(key);
                continue;
            }

            var dataType = DataTypeForXamlType(Attr(argument, "TypeArguments"));
            var variable = SourceVariableOf(argument.Value);
            var resolved = variable is null ? null : chain.Resolve(variable, dataType);

            if (resolved is not null)
            {
                inputs[key] = resolved;
                continue;
            }

            // Kept verbatim so the reader still sees what is configured, but flagged below.
            inputs[key] = new WorkflowValue
            {
                Kind = WorkflowValueKind.Literal,
                DataType = dataType,
                Literal = argument.Value.Trim()
            };
            unresolved.Add(key);
        }

        if (unresolved.Count > 0)
        {
            notes.Add($"{stepId}: input(s) {string.Join(", ", unresolved)} are shown as raw " +
                      "expressions; the preparation chain behind them could not be reduced to a value.");
            unrecognised.Add($"{stepId}: input arguments of a code activity " +
                             $"({string.Join(", ", unresolved)}). Change this step in the designer — " +
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

    /// <remarks>
    /// Uses the same <see cref="ValueChain"/> as a code activity's inputs, so every value shape —
    /// constant, field list with fallback, "now", concatenation, activity output — is read the same way
    /// wherever it appears.
    /// </remarks>
    private static List<WorkflowAttributeAssignment> ParseAttributeAssignments(
        XElement scope, string primaryEntity, List<string> notes)
    {
        var result = new List<WorkflowAttributeAssignment>();
        var chain = ValueChain.Of(scope);

        foreach (var set in scope.Descendants().Where(e => e.Name.LocalName == "SetEntityProperty"))
        {
            var attribute = Attr(set, "Attribute") ?? "?";
            var sourceVar = SourceVariableOf(Attr(set, "Value"));
            var dataType = DataTypeForXamlType(TargetTypeOf(set));

            var value = sourceVar is null ? null : chain.Resolve(sourceVar, dataType);

            if (value is null)
            {
                value = new WorkflowValue { Kind = WorkflowValueKind.Literal, DataType = dataType, Literal = null };
                notes.Add($"Attribute '{attribute}': value expression could not be resolved.");
            }

            result.Add(new WorkflowAttributeAssignment { Attribute = attribute, Value = value });
        }

        return result;
    }

    /// <summary>
    /// The declared type of a Set/GetEntityProperty, read from its nested TargetType argument.
    /// </summary>
    private static string? TargetTypeOf(XElement property)
    {
        var targetType = property.Elements()
            .FirstOrDefault(e => e.Name.LocalName.EndsWith(".TargetType", StringComparison.Ordinal));

        var literal = targetType?.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ReferenceLiteral");

        return literal is null ? null : Attr(literal, "Value");
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
    /// <summary>
    /// Where a read gets its record from, when it is not the triggering one:
    /// <c>CreatedEntities("CreateStep17_localParameter")</c> is the record a create step made, and
    /// <c>CreatedEntities("CustomActivityStep6InitiatingUser_entity")</c> the record loaded for a code
    /// activity's output.
    /// </summary>
    private static (string? FromStep, string? FromStepOutput) OriginOf(string? entityExpression)
    {
        if (string.IsNullOrWhiteSpace(entityExpression)
            || !entityExpression.Contains("CreatedEntities", StringComparison.Ordinal))
            return (null, null);

        var created = Regex.Match(entityExpression, @"CreatedEntities\(&?q?u?o?t?;?""?([^""&)]+)");
        if (!created.Success)
            return (null, null);

        var key = created.Groups[1].Value;

        var localParameter = Regex.Match(key, @"^([A-Za-z]+Step\d+)_localParameter$");
        if (localParameter.Success)
            return (localParameter.Groups[1].Value, null);

        var loaded = Regex.Match(key, @"^([A-Za-z]+Step\d+)(.+)_entity$");
        if (loaded.Success)
            return (null, $"{loaded.Groups[1].Value}.{loaded.Groups[2].Value}");

        return (null, null);
    }

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

    /// <summary>
    /// The variable an argument expression ultimately reads, i.e. "Step1_1_converted" out of
    /// <c>[DirectCast(Step1_1_converted, Microsoft.Xrm.Sdk.EntityReference)]</c> or <c>[Step1_1]</c>.
    /// </summary>
    /// <remarks>
    /// <c>DirectCast</c> is only a type assertion — every helper variable in this XAML is
    /// <c>x:Object</c>, while the parameter is typed, so the expression declares the type without
    /// computing anything. The conversion itself happened in <c>ConvertCrmXrmTypes</c> before.
    /// </remarks>
    private static string? SourceVariableOf(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var inner = expression.Trim().Trim('[', ']').Trim();

        var cast = Regex.Match(inner, @"^DirectCast\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,");
        if (cast.Success)
            return cast.Groups[1].Value;

        return Regex.IsMatch(inner, @"^[A-Za-z_][A-Za-z0-9_]*$") ? inner : null;
    }

    /// <summary>"mxs:EntityReference" -> "EntityReference". The reverse of the builder's mapping.</summary>
    private static string? DataTypeForXamlType(string? typeArgument) => typeArgument switch
    {
        "x:String" => "String",
        "x:Int32" => "Integer",
        "x:Boolean" => "Boolean",
        "s:DateTime" => "DateTime",
        "x:Decimal" => "Decimal",
        "x:Double" => "Double",
        "mxs:Money" => "Money",
        "mxs:OptionSetValue" => "OptionSetValue",
        "mxs:EntityReference" => "EntityReference",
        "s:Guid" => "Guid",
        "mxs:EntityCollection" => "PartyList",
        _ => null
    };

    /// <summary>
    /// The value-preparation activities of one step, indexed by the variable they write, so a
    /// variable can be resolved back into the <see cref="WorkflowValue"/> it was built from.
    /// </summary>
    private sealed class ValueChain
    {
        private readonly Dictionary<string, (string? Entity, string? Attribute, string? Via,
            (string? FromStep, string? FromStepOutput) Origin)> _reads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string? Operator, string? Parameters)> _expressions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _conversions = new(StringComparer.Ordinal);

        public static ValueChain Of(XElement scope)
        {
            var chain = new ValueChain();

            foreach (var read in scope.Descendants().Where(e => e.Name.LocalName == "GetEntityProperty"))
            {
                var target = (Attr(read, "Value") ?? string.Empty).Trim('[', ']');
                if (target.Length > 0)
                    chain._reads[target] = (Attr(read, "EntityName"), Attr(read, "Attribute"),
                                            ViaOf(Attr(read, "Entity")), OriginOf(Attr(read, "Entity")));
            }

            foreach (var activity in scope.Descendants().Where(e => e.Name.LocalName == "ActivityReference"))
            {
                var aqn = Attr(activity, "AssemblyQualifiedName") ?? string.Empty;
                var target = (OutArgumentValue(activity, "Result") ?? string.Empty).Trim('[', ']');
                if (target.Length == 0)
                    continue;

                if (aqn.Contains(".EvaluateExpression"))
                    chain._expressions[target] = (ArgumentValue(activity, "ExpressionOperator"),
                                                  ArgumentValue(activity, "Parameters"));
                else if (aqn.Contains(".ConvertCrmXrmTypes"))
                {
                    var source = (ArgumentValue(activity, "Value") ?? string.Empty).Trim('[', ']');
                    if (source.Length > 0)
                        chain._conversions[target] = source;
                }
            }

            return chain;
        }

        /// <summary>The value behind a variable, or null when the chain cannot be reduced.</summary>
        public WorkflowValue? Resolve(string variable, string? dataType)
        {
            // A conversion carries no information of its own — keep walking to its source.
            if (_conversions.TryGetValue(variable, out var converted))
                return Resolve(converted, dataType);

            if (_reads.TryGetValue(variable, out var read) && read.Attribute is not null)
                return new WorkflowValue
                {
                    Kind = WorkflowValueKind.Field,
                    DataType = dataType,
                    Fields = [$"{read.Entity}.{read.Attribute}"],
                    Via = read.Via,
                    FromStep = read.Origin.FromStep,
                    FromStepOutput = read.Origin.FromStepOutput
                };

            if (!_expressions.TryGetValue(variable, out var expression))
                return StepOutputOf(variable) is { } reference
                    ? new WorkflowValue
                    {
                        Kind = WorkflowValueKind.StepOutput,
                        DataType = dataType,
                        StepOutput = reference
                    }
                    : null;

            return expression.Operator switch
            {
                "SelectFirstNonNull" => ResolveSelection(expression.Parameters, dataType),
                "CreateCrmType" => ResolveLiteral(expression.Parameters, dataType),
                "RetrieveCurrentTime" => new WorkflowValue
                {
                    Kind = WorkflowValueKind.Now,
                    DataType = dataType ?? "DateTime"
                },
                "Add" => ResolveConcat(expression.Parameters, dataType),
                _ => null
            };
        }

        /// <summary>An Add expression: the parts in order, each resolved on its own.</summary>
        private WorkflowValue? ResolveConcat(string? parameters, string? dataType)
        {
            var parts = new List<WorkflowValue>();

            foreach (var source in VariablesIn(parameters))
            {
                var part = Resolve(source, dataType);
                if (part is null)
                    return null;
                parts.Add(part);
            }

            return parts.Count == 0
                ? null
                : new WorkflowValue
                {
                    Kind = WorkflowValueKind.Concat,
                    DataType = dataType,
                    Parts = parts
                };
        }

        /// <summary>Fields in order, plus a trailing constant as the fallback.</summary>
        private WorkflowValue? ResolveSelection(string? parameters, string? dataType)
        {
            var sources = VariablesIn(parameters);

            // A single non-field source passes through unchanged: "now", a concatenation or an
            // earlier activity's output all reach their target wrapped in SelectFirstNonNull.
            if (sources.Count == 1 && !_reads.ContainsKey(sources[0])
                && Resolve(sources[0], dataType) is
                    { Kind: WorkflowValueKind.Now or WorkflowValueKind.Concat or WorkflowValueKind.StepOutput } only)
                return only;

            var fields = new List<string>();
            string? via = null;
            string? fallback = null;
            string? fromStep = null;
            string? fromStepOutput = null;

            foreach (var source in sources)
            {
                if (_reads.TryGetValue(source, out var read) && read.Attribute is not null)
                {
                    fields.Add($"{read.Entity}.{read.Attribute}");
                    via ??= read.Via;
                    fromStep ??= read.Origin.FromStep;
                    fromStepOutput ??= read.Origin.FromStepOutput;
                }
                else
                    switch (Resolve(source, dataType))
                    {
                        case { Kind: WorkflowValueKind.Literal } literal:
                            fallback = literal.Literal;
                            break;
                        // A lone activity output reaches the argument through SelectFirstNonNull.
                        case { Kind: WorkflowValueKind.StepOutput } output:
                            return output;
                        default:
                            return null;
                    }
            }

            if (fields.Count == 0)
                return fallback is null
                    ? null
                    : new WorkflowValue { Kind = WorkflowValueKind.Literal, DataType = dataType, Literal = fallback };

            return new WorkflowValue
            {
                Kind = WorkflowValueKind.Field,
                DataType = dataType,
                Fields = fields,
                Via = via,
                FromStep = fromStep,
                FromStepOutput = fromStepOutput,
                Fallback = fallback
            };
        }

        /// <summary>
        /// A constant. A record reference is built in two stages and comes back as "entity:guid";
        /// everything else is the single literal of the parameter array.
        /// </summary>
        private WorkflowValue? ResolveLiteral(string? parameters, string? dataType)
        {
            if (parameters is null)
                return null;

            // A party list wraps a reference; report the reference and keep PartyList as the type.
            if (parameters.Contains("WorkflowPropertyType.PartyList", StringComparison.Ordinal))
            {
                var wrapped = ArgumentTokens(parameters)
                    .FirstOrDefault(t => !t.IsString && t.Value is not null && !t.Value.Contains('.')).Value;

                if (wrapped is null || Resolve(wrapped, "EntityReference") is not { } recipient)
                    return null;

                return recipient with { DataType = "PartyList" };
            }

            if (parameters.Contains("WorkflowPropertyType.EntityReference", StringComparison.Ordinal))
            {
                var tokens = ArgumentTokens(parameters);
                var entity = tokens.FirstOrDefault(t => t.IsString).Value;

                // The id lives in the variable the second CreateCrmType refers to.
                var idVariable = tokens
                    .FirstOrDefault(t => !t.IsString && t.Value is not null && !t.Value.Contains('.')).Value;
                var id = idVariable is null
                    ? null
                    : ExtractGuid(_expressions.TryGetValue(idVariable, out var idExpression)
                        ? idExpression.Parameters
                        : null);

                return entity is null || id is null
                    ? null
                    : new WorkflowValue
                    {
                        Kind = WorkflowValueKind.Literal,
                        DataType = "EntityReference",
                        Literal = $"{entity}:{id}"
                    };
            }

            var value = ExtractCreateCrmTypeLiteral(parameters);
            return value is null
                ? null
                : new WorkflowValue { Kind = WorkflowValueKind.Literal, DataType = dataType, Literal = value };
        }
    }

    /// <summary>
    /// Strings and identifiers inside the braces of "[New Object() { … }]", in order — splitting on
    /// commas would break on a label that contains one, and reading the whole expression would pick
    /// up the "New Object" of the array initialiser.
    /// </summary>
    private static List<(bool IsString, string? Value)> ArgumentTokens(string parameters)
    {
        var braces = Regex.Match(parameters, @"\{(.*)\}", RegexOptions.Singleline);
        if (!braces.Success)
            return [];

        return Regex.Matches(braces.Groups[1].Value, @"""([^""]*)""|([A-Za-z_][A-Za-z0-9_.]*)")
            .Select(m => m.Groups[1].Success
                ? (IsString: true, Value: (string?)m.Groups[1].Value)
                : (IsString: false, Value: (string?)m.Groups[2].Value))
            .ToList();
    }

    /// <summary>
    /// The <c>dataType</c> a CreateCrmType call declares, taken from its
    /// <c>WorkflowPropertyType.&lt;x&gt;</c> marker. The reverse of the builder's mapping.
    /// </summary>
    private static string? CreateCrmTypeDataType(string? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
            return null;

        var match = Regex.Match(parameters, @"WorkflowPropertyType\.(\w+)");
        if (!match.Success)
            return null;

        return match.Groups[1].Value switch
        {
            "String" => "String",
            "Int" => "Integer",
            "Boolean" => "Boolean",
            "DateTime" => "DateTime",
            "Decimal" => "Decimal",
            "Double" => "Double",
            "Money" => "Money",
            "OptionSetValue" => "OptionSetValue",
            "EntityReference" => "EntityReference",
            "Guid" => "Guid",
            _ => null
        };
    }

    /// <summary>Pulls the literal out of a CreateCrmType parameter array.</summary>
    public static string? ExtractCreateCrmTypeLiteral(string? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
            return null;

        var match = Regex.Match(parameters, "WorkflowPropertyType\\.\\w+\\s*,\\s*\"([^\"]*)\"");

        // Commas are escaped inside the parameter array (see WorkflowXamlBuilder.MaskCommas);
        // undo that so the caller gets the plain text and a round trip stays stable.
        return match.Success ? match.Groups[1].Value.Replace("&#44;", ",") : null;
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
