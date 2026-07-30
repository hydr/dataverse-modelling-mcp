namespace Dataverse.Core.Workflows;

using System.Text;
using Dataverse.Core.Models;

/// <summary>
/// Renders a workflow as readable text: trigger configuration, execution settings and the step tree.
/// </summary>
public static class WorkflowExplainer
{
    public static string Explain(WorkflowDetail detail, WorkflowParseResult parsed)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {detail.Name}");
        sb.AppendLine();
        sb.AppendLine($"- Table: {detail.PrimaryEntity}");
        sb.AppendLine($"- Status: {(detail.StateCode == 1 ? "activated" : "draft")}");
        sb.AppendLine($"- Mode: {detail.Mode} ({(detail.Mode == "Realtime" ? "synchronous" : "asynchronous")})");
        sb.AppendLine($"- Scope: {detail.Scope}");
        sb.AppendLine($"- Runs as: {detail.RunAs}");
        if (!string.IsNullOrWhiteSpace(detail.Description))
            sb.AppendLine($"- Description: {detail.Description}");

        // The first thing a caller needs to know: may this workflow be rewritten from here at all?
        sb.AppendLine(parsed.FullyUnderstood
            ? "- **Editable here:** yes — every construct was recognised, so workflow_set_definition "
              + "can write it back."
            : $"- **Editable here: NO** — {parsed.Unrecognised.Count} construct(s) were not recognised "
              + "(see the end of this report). Change this workflow in the designer; rewriting it "
              + "would drop those parts.");
        sb.AppendLine();

        sb.AppendLine("## Triggers");
        var triggers = new List<string>();
        if (detail.IsOnCreate) triggers.Add($"record is created ({detail.CreateStage ?? "post-operation"})");
        if (detail.IsOnUpdate)
        {
            var attributes = string.IsNullOrWhiteSpace(detail.TriggerOnUpdateAttributes)
                ? "any field"
                : detail.TriggerOnUpdateAttributes;
            triggers.Add($"record is updated ({detail.UpdateStage ?? "post-operation"}), fields: {attributes}");
        }
        if (detail.IsOnDelete) triggers.Add($"record is deleted ({detail.DeleteStage ?? "pre-operation"})");
        if (detail.OnDemand) triggers.Add("on demand (manually startable)");

        if (triggers.Count == 0)
            sb.AppendLine("- none — this workflow never starts on its own. " +
                          "Enable a trigger or 'ondemand', or call it as a child workflow.");
        else
            foreach (var trigger in triggers)
                sb.AppendLine($"- {trigger}");
        sb.AppendLine();

        sb.AppendLine("## Logic");
        if (parsed.Definition.Steps.Count == 0)
            sb.AppendLine("_No steps._");
        else
            AppendSteps(parsed.Definition.Steps, sb, 0);

        if (parsed.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Notes on this reading");
            foreach (var note in parsed.Notes)
                sb.AppendLine($"- {note}");
        }

        if (parsed.Unrecognised.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Not understood");
            sb.AppendLine("The following parts of the XAML could not be mapped. The explanation above is " +
                          "therefore incomplete, and this definition must NOT be written back — doing so " +
                          "would drop these parts:");
            foreach (var item in parsed.Unrecognised)
                sb.AppendLine($"- {item}");
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendSteps(IEnumerable<WorkflowStep> steps, StringBuilder sb, int depth)
    {
        var indent = new string(' ', depth * 2);

        foreach (var step in steps)
        {
            var label = string.IsNullOrWhiteSpace(step.Description) ? null : $" — {step.Description}";
            var id = step.StepId is null ? string.Empty : $" [{step.StepId}]";

            switch (step.Kind)
            {
                case WorkflowStepKind.Condition:
                case WorkflowStepKind.Wait:
                    var verb = step.Kind == WorkflowStepKind.Wait ? "Wait until" : "If";

                    if (step.Branches is { Count: > 0 })
                    {
                        // An if / else-if chain: every case on its own line, tested in order.
                        sb.AppendLine($"{indent}- {verb} — first matching case wins{id}{label}");
                        for (var b = 0; b < step.Branches.Count; b++)
                        {
                            var branch = step.Branches[b];
                            var word = b == 0 ? "case" : "else if";
                            sb.AppendLine($"{indent}  {word} {DescribeConditions(branch.Conditions, branch.LogicalOperator)}"
                                          + (branch.BranchId is null ? string.Empty : $" [{branch.BranchId}]") + ":");
                            AppendSteps(branch.Steps ?? [], sb, depth + 2);
                        }
                    }
                    else
                    {
                        sb.AppendLine($"{indent}- {verb} {DescribeConditions(step.Conditions ?? [], step.LogicalOperator)}{id}{label}");

                        if (step.Then is { Count: > 0 })
                        {
                            sb.AppendLine($"{indent}  then:");
                            AppendSteps(step.Then, sb, depth + 2);
                        }
                    }

                    if (step.Else is { Count: > 0 })
                    {
                        sb.AppendLine($"{indent}  otherwise:");
                        AppendSteps(step.Else, sb, depth + 2);
                    }
                    break;

                case WorkflowStepKind.Stage:
                    sb.AppendLine($"{indent}- Stage{id}{label}");
                    AppendSteps(step.Children ?? [], sb, depth + 1);
                    break;

                case WorkflowStepKind.UpdateRecord:
                    sb.AppendLine($"{indent}- Update {step.Entity}{id}{label}");
                    AppendAssignments(step, sb, depth + 1);
                    break;

                case WorkflowStepKind.CreateRecord:
                    sb.AppendLine($"{indent}- Create {step.Entity}{id}{label}");
                    AppendAssignments(step, sb, depth + 1);
                    break;

                case WorkflowStepKind.AssignRecord:
                    sb.AppendLine($"{indent}- Assign {step.Entity} to {step.OwnerId ?? "(not configured)"}{id}{label}");
                    break;

                case WorkflowStepKind.ChangeStatus:
                    sb.AppendLine($"{indent}- Change status of {step.Entity} to state={step.State}, status={step.Status}{id}{label}");
                    break;

                case WorkflowStepKind.StopWorkflow:
                    sb.AppendLine($"{indent}- Stop workflow ({step.Outcome ?? "succeeded"}){id}{label}");
                    break;

                case WorkflowStepKind.CustomActivity:
                    var type = step.AssemblyQualifiedName?.Split(',')[0] ?? "(unknown)";
                    sb.AppendLine($"{indent}- Run code activity {type}{id}{label}");
                    foreach (var (key, value) in step.Inputs ?? [])
                        sb.AppendLine($"{indent}  - in {key} = {DescribeValue(value, bare: true)}");
                    foreach (var output in step.Outputs ?? [])
                        sb.AppendLine($"{indent}  - out {output} (referencable as {step.StepId}.{output})");
                    break;

                case WorkflowStepKind.StartChildWorkflow:
                    sb.AppendLine($"{indent}- Start child workflow {step.ChildWorkflowId}{id}{label}");
                    break;

                case WorkflowStepKind.SendEmail:
                    sb.AppendLine($"{indent}- Send email{id}{label}");
                    break;

                case WorkflowStepKind.PerformAction:
                    sb.AppendLine($"{indent}- Perform action (SDK message){id}{label}");
                    break;

                default:
                    sb.AppendLine($"{indent}- {step.Kind}{id}{label}");
                    break;
            }
        }
    }

    private static void AppendAssignments(WorkflowStep step, StringBuilder sb, int depth)
    {
        var indent = new string(' ', depth * 2);
        foreach (var assignment in step.Attributes ?? [])
            sb.AppendLine($"{indent}- {assignment.Attribute} = {DescribeValue(assignment.Value, bare: true)}");
    }

    private static string DescribeValue(WorkflowValue? value, bool bare = false)
    {
        if (value is null)
            return string.Empty;

        var text = value.Kind switch
        {
            WorkflowValueKind.Literal when value.Literals is { Count: > 0 } =>
                "any of " + string.Join(", ", value.Literals.Select(l => $"\"{l}\"")),
            WorkflowValueKind.Literal => $"\"{value.Literal}\"",
            WorkflowValueKind.Field when value.Fields is { Count: > 0 } =>
                string.Join(" or ", value.Fields)
                + (value.Via is null ? string.Empty : $" (via {value.Via})")
                + (value.Fallback is null ? string.Empty : $" or \"{value.Fallback}\""),
            WorkflowValueKind.StepOutput => $"output {value.StepOutput}",
            WorkflowValueKind.Now => "the current date and time",
            // Long concatenations are the norm for e-mail bodies, so only the shape is shown.
            WorkflowValueKind.Concat when value.Parts is { Count: > 0 } =>
                $"{value.Parts.Count} parts joined: " + Shorten(string.Join(" + ",
                    value.Parts.Select(p => DescribeValue(p, bare: true)))),
            _ => "(unresolved)"
        };

        return bare ? text : " " + text;
    }

    /// <summary>Keeps a value readable — an e-mail body runs to thousands of characters.</summary>
    private static string Shorten(string text, int max = 160) =>
        text.Length <= max ? text : text[..max] + " …";

    /// <summary>The comparisons of one case as a single readable line.</summary>
    private static string DescribeConditions(List<WorkflowCondition> conditions, string? logicalOperator)
    {
        var joiner = string.Equals(logicalOperator, "Or", StringComparison.OrdinalIgnoreCase)
            ? " OR " : " AND ";

        return string.Join(joiner, conditions.Select(c => string.IsNullOrWhiteSpace(c.StepOutput)
            // A related read is worth naming as such, so the reader sees the hop.
            ? $"{c.Entity}.{c.Attribute}"
              + (string.IsNullOrWhiteSpace(c.Via) ? string.Empty : $" (via {c.Via})")
              + $" {Humanise(c.Operator)}{DescribeValue(c.Value)}"
            : $"output {c.StepOutput} {Humanise(c.Operator)}{DescribeValue(c.Value)}"));
    }

    private static string Humanise(string op) => op switch
    {
        "Equal" => "equals",
        "NotEqual" => "does not equal",
        "Contains" => "contains",
        "DoesNotContain" => "does not contain",
        "BeginsWith" => "begins with",
        "EndsWith" => "ends with",
        "NotNull" => "contains data",
        "Null" => "is empty",
        "GreaterThan" => ">",
        "GreaterEqual" => ">=",
        "LessThan" => "<",
        "LessEqual" => "<=",
        _ => op
    };
}
