namespace Dataverse.Core.Workflows;

using System.Globalization;

public sealed record WorkflowDefinitionValidation(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Pure (no network) validation of a <see cref="WorkflowDefinition"/> before it is built into
/// XAML and written. Catches structural problems, unsupported literal types and — crucially —
/// performs a build + re-parse round-trip so we never write XAML the parser cannot recognise.
/// Semantic checks that need live metadata (entity/attribute/activity existence) are layered on
/// top in <c>WorkflowService</c>.
/// </summary>
public static class WorkflowXamlValidator
{
    private static readonly Guid ProbeId = new("00000000-0000-0000-0000-0000000000aa");

    public static WorkflowDefinitionValidation Validate(WorkflowDefinition def)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(def.PrimaryEntity))
            errors.Add("PrimaryEntity is required.");

        if (def.Steps.Count == 0)
            errors.Add("At least one step is required.");

        for (var i = 0; i < def.Steps.Count; i++)
            ValidateStep(def.Steps[i], i, def.PrimaryEntity, errors, warnings);

        // Round-trip: only attempt if the IR is structurally sound so far.
        if (errors.Count == 0)
        {
            try
            {
                var xaml = WorkflowXamlBuilder.Build(def, ProbeId);
                var interpretation = WorkflowXamlParser.Interpret(xaml);
                if (!interpretation.IsWellFormed)
                    errors.Add("Generated XAML is not well-formed.");
                else if (!interpretation.IsTemplateRecognized)
                    errors.Add("Generated XAML does not round-trip to a recognised template (parser reported unknown steps).");
            }
            catch (Exception ex)
            {
                errors.Add($"XAML generation failed: {ex.Message}");
            }
        }

        return new WorkflowDefinitionValidation(errors.Count == 0, errors, warnings);
    }

    private static void ValidateStep(WorkflowStep step, int index, string primaryEntity, List<string> errors, List<string> warnings)
    {
        var prefix = $"Step {index + 1}";
        switch (step)
        {
            case UpdateEntityStep u:
                if (string.IsNullOrWhiteSpace(u.EntityName))
                    errors.Add($"{prefix}: UpdateEntity requires an EntityName.");
                else if (!string.Equals(u.EntityName, primaryEntity, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{prefix}: UpdateEntity can only target the primary entity ('{primaryEntity}') in this version; got '{u.EntityName}'.");
                ValidateAssignments(u.Assignments, prefix, errors);
                break;

            case CreateEntityStep c:
                if (string.IsNullOrWhiteSpace(c.EntityName))
                    errors.Add($"{prefix}: CreateEntity requires an EntityName.");
                ValidateAssignments(c.Assignments, prefix, errors);
                break;

            case CustomActivityStep a:
                if (string.IsNullOrWhiteSpace(a.AssemblyQualifiedName))
                    errors.Add($"{prefix}: CustomActivity requires an AssemblyQualifiedName.");
                if (string.IsNullOrWhiteSpace(a.Label))
                    warnings.Add($"{prefix}: CustomActivity has no Label; a generic name will be used.");
                foreach (var arg in a.InputArguments)
                {
                    if (arg.Value is null)
                        errors.Add($"{prefix}: input argument '{arg.Name}' has no value.");
                    else if (arg.Value is LiteralValue lit)
                        ValidateLiteral(lit, $"{prefix} arg '{arg.Name}'", errors, allowEntityReference: true);
                }
                break;

            case ConditionStep:
                errors.Add($"{prefix}: Condition steps cannot be generated in this version.");
                break;

            case UnknownStep us:
                errors.Add($"{prefix}: Unknown step '{us.ActivityType}'.");
                break;

            default:
                errors.Add($"{prefix}: Unsupported step kind '{step.GetType().Name}'.");
                break;
        }
    }

    private static void ValidateAssignments(IReadOnlyList<FieldAssignment> assignments, string prefix, List<string> errors)
    {
        if (assignments.Count == 0)
            errors.Add($"{prefix}: at least one field assignment is required.");

        foreach (var fa in assignments)
        {
            if (string.IsNullOrWhiteSpace(fa.Attribute))
                errors.Add($"{prefix}: a field assignment has no attribute name.");

            switch (fa.Value)
            {
                case LiteralValue lit:
                    // EntityReference literals are not supported for SetEntityProperty (CreateCrmType).
                    ValidateLiteral(lit, $"{prefix} field '{fa.Attribute}'", errors, allowEntityReference: false);
                    break;
                case FieldValue:
                    break;
                case null:
                    errors.Add($"{prefix}: field '{fa.Attribute}' has no value.");
                    break;
            }
        }
    }

    private static void ValidateLiteral(LiteralValue lit, string where, List<string> errors, bool allowEntityReference)
    {
        switch (lit.Type)
        {
            case CrmValueType.Integer when !long.TryParse(lit.Value, out _):
                errors.Add($"{where}: '{lit.Value}' is not a valid integer.");
                break;
            case CrmValueType.Decimal or CrmValueType.Double or CrmValueType.Money
                when !decimal.TryParse(lit.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out _):
                errors.Add($"{where}: '{lit.Value}' is not a valid number.");
                break;
            case CrmValueType.Boolean when !bool.TryParse(lit.Value, out _):
                errors.Add($"{where}: '{lit.Value}' is not a valid boolean (use 'true'/'false').");
                break;
            case CrmValueType.OptionSet when !int.TryParse(lit.Value, out _):
                errors.Add($"{where}: OptionSet value '{lit.Value}' must be an integer option value.");
                break;
            case CrmValueType.DateTime when !DateTime.TryParse(lit.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _):
                errors.Add($"{where}: '{lit.Value}' is not a valid date/time.");
                break;
            case CrmValueType.Guid when !Guid.TryParse(lit.Value, out _):
                errors.Add($"{where}: '{lit.Value}' is not a valid GUID.");
                break;
            case CrmValueType.EntityReference:
                if (!allowEntityReference)
                    errors.Add($"{where}: EntityReference literals are not supported here; use a field reference.");
                else if (string.IsNullOrWhiteSpace(lit.EntityReferenceLogicalName))
                    errors.Add($"{where}: EntityReference literal requires EntityReferenceLogicalName.");
                else if (!Guid.TryParse(lit.Value, out _))
                    errors.Add($"{where}: EntityReference id '{lit.Value}' is not a valid GUID.");
                break;
        }
    }
}
