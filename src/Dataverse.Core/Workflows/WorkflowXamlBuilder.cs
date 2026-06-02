namespace Dataverse.Core.Workflows;

using System.Xml.Linq;
using static Dataverse.Core.Workflows.WorkflowXamlNames;

/// <summary>
/// Generates WF4 XAML for a <see cref="WorkflowDefinition"/>, following the conventions the
/// Dataverse Classic Workflow designer emits (step DisplayNames, Composite/EvaluateExpression
/// wrappers, InputEntities/CreatedEntities dictionaries). The generated XAML is intended to be
/// written to <c>workflow.xaml</c> and to round-trip through <see cref="WorkflowXamlParser"/>.
///
/// Supported step kinds: <see cref="UpdateEntityStep"/>, <see cref="CreateEntityStep"/>,
/// <see cref="CustomActivityStep"/>. Other kinds throw <see cref="NotSupportedException"/>.
/// </summary>
public static class WorkflowXamlBuilder
{
    private const string InputPrimary = "[InputEntities(\"primaryEntity\")]";

    public static string Build(WorkflowDefinition def, Guid workflowId)
    {
        if (workflowId == Guid.Empty)
            throw new ArgumentException("A non-empty workflowId is required to derive the x:Class name.", nameof(workflowId));

        var className = ClassName(workflowId);
        var ctx = new BuildContext();

        var workflowChildren = new List<XElement>();
        foreach (var step in def.Steps)
            workflowChildren.Add(BuildStep(step, def.PrimaryEntity, ctx));

        var workflow = new XElement(Mxswa + "Workflow");
        if (ctx.WorkflowVariables.Count > 0)
        {
            workflow.Add(new XElement(Mxswa + "Workflow.Variables", ctx.WorkflowVariables));
        }
        workflow.Add(workflowChildren);

        var root = new XElement(Activities + "Activity",
            new XAttribute(X + "Class", className),
            // Namespace declarations (exact prefixes the platform uses).
            new XAttribute(XNamespace.Xmlns + "mva", MvaUri),
            new XAttribute(XNamespace.Xmlns + "mxs", MxsUri),
            new XAttribute(XNamespace.Xmlns + "mxsq", MxsqUri),
            new XAttribute(XNamespace.Xmlns + "mxsw", MxswUri),
            new XAttribute(XNamespace.Xmlns + "mxswa", MxswaUri),
            new XAttribute(XNamespace.Xmlns + "s", SUri),
            new XAttribute(XNamespace.Xmlns + "scg", ScgUri),
            new XAttribute(XNamespace.Xmlns + "sco", ScoUri),
            new XAttribute(XNamespace.Xmlns + "srs", SrsUri),
            new XAttribute(XNamespace.Xmlns + "this", ThisUri),
            new XAttribute(XNamespace.Xmlns + "x", XamlUri),
            // x:Members
            new XElement(X + "Members",
                new XElement(X + "Property",
                    new XAttribute("Name", "InputEntities"),
                    new XAttribute("Type", "InArgument(scg:IDictionary(x:String, mxs:Entity))")),
                new XElement(X + "Property",
                    new XAttribute("Name", "CreatedEntities"),
                    new XAttribute("Type", "InArgument(scg:IDictionary(x:String, mxs:Entity))"))),
            // Default values for the two members
            new XElement(This + (className + ".InputEntities"),
                new XElement(Activities + "InArgument",
                    new XAttribute(X + "TypeArguments", "scg:IDictionary(x:String, mxs:Entity)"))),
            new XElement(This + (className + ".CreatedEntities"),
                new XElement(Activities + "InArgument",
                    new XAttribute(X + "TypeArguments", "scg:IDictionary(x:String, mxs:Entity)"))),
            new XElement(Mva + "VisualBasic.Settings",
                "Assembly references and imported namespaces for internal implementation"),
            workflow);

        return WorkflowXamlNames.Serialize(root);
    }

    private static XElement BuildStep(WorkflowStep step, string primaryEntity, BuildContext ctx) => step switch
    {
        UpdateEntityStep u => BuildEntityWriteStep(u.EntityName, u.Assignments, primaryEntity, ctx, isCreate: false),
        CreateEntityStep c => BuildEntityWriteStep(c.EntityName, c.Assignments, primaryEntity, ctx, isCreate: true),
        CustomActivityStep a => BuildCustomActivityStep(a, primaryEntity, ctx),
        _ => throw new NotSupportedException(
            $"Step kind '{step.GetType().Name}' cannot be generated. Only UpdateEntityStep, CreateEntityStep and CustomActivityStep are buildable.")
    };

    private static XElement BuildEntityWriteStep(
        string entityName,
        IReadOnlyList<FieldAssignment> assignments,
        string primaryEntity,
        BuildContext ctx,
        bool isCreate)
    {
        var stepNum = ctx.NextStep();
        var stepName = (isCreate ? "CreateStep" : "UpdateStep") + stepNum;
        var tempKey = isCreate ? $"{stepName}_localParameter#Temp" : "primaryEntity#Temp";
        var tempRef = $"[CreatedEntities(\"{tempKey}\")]";

        var seqVars = new List<XElement>();
        var activities = new List<XElement>();
        var varIndex = 0;

        // 1. Create the in-memory target entity.
        activities.Add(new XElement(Activities + "Assign",
            new XAttribute(X + "TypeArguments", "mxs:Entity"),
            new XAttribute("To", tempRef),
            new XAttribute("Value", $"[New Entity(\"{entityName}\")]")));

        // 2. For updates, copy the primary record's Id onto the temp entity.
        if (!isCreate)
        {
            activities.Add(new XElement(Activities + "Assign",
                new XAttribute(X + "TypeArguments", "s:Guid"),
                new XAttribute("To", $"[CreatedEntities(\"{tempKey}\").Id]"),
                new XAttribute("Value", "[InputEntities(\"primaryEntity\").Id]")));
        }

        // 3. One value-computation + SetEntityProperty per field.
        foreach (var fa in assignments)
        {
            var info = CrmTypeMap.Get(fa.Type);
            var varName = $"{stepName}_{++varIndex}";
            seqVars.Add(VarObject(varName));

            activities.Add(BuildValueComputation(fa.Value, info, varName, primaryEntity));

            activities.Add(new XElement(Mxswa + "SetEntityProperty",
                new XAttribute("Attribute", fa.Attribute),
                new XAttribute("Entity", tempRef),
                new XAttribute("EntityName", entityName),
                new XAttribute("Value", $"[{varName}]"),
                ReferenceLiteralChild(Mxswa + "SetEntityProperty.TargetType", info.XamlType)));
        }

        // 4. The actual Create/Update activity.
        if (isCreate)
        {
            activities.Add(new XElement(Mxswa + "CreateEntity",
                new XAttribute("EntityId", "{x:Null}"),
                new XAttribute("DisplayName", stepName),
                new XAttribute("Entity", tempRef),
                new XAttribute("EntityName", entityName)));
            // Expose the created record under its non-temp key.
            activities.Add(new XElement(Activities + "Assign",
                new XAttribute(X + "TypeArguments", "mxs:Entity"),
                new XAttribute("To", $"[CreatedEntities(\"{stepName}_localParameter\")]"),
                new XAttribute("Value", tempRef)));
        }
        else
        {
            activities.Add(new XElement(Mxswa + "UpdateEntity",
                new XAttribute("DisplayName", stepName),
                new XAttribute("Entity", tempRef),
                new XAttribute("EntityName", entityName)));
            // Write the updated temp entity back to the primary slot.
            activities.Add(new XElement(Activities + "Assign",
                new XAttribute(X + "TypeArguments", "mxs:Entity"),
                new XAttribute("To", InputPrimary),
                new XAttribute("Value", tempRef)));
        }

        var seq = new XElement(Activities + "Sequence", new XAttribute("DisplayName", stepName));
        if (seqVars.Count > 0)
            seq.Add(new XElement(Activities + "Sequence.Variables", seqVars));
        seq.Add(activities);
        return seq;
    }

    private static XElement BuildCustomActivityStep(CustomActivityStep step, string primaryEntity, BuildContext ctx)
    {
        var stepNum = ctx.NextStep();
        var stepName = "CustomActivityStep" + stepNum;
        var display = $"{stepName}: {step.Label}";

        var compositeVars = new List<XElement>();
        var compositeActivities = new List<XElement>();
        var innerArgs = new List<XElement>();
        var varIndex = 0;

        foreach (var arg in step.InputArguments)
        {
            var info = CrmTypeMap.Get(arg.Type);
            string content;
            switch (arg.Value)
            {
                case LiteralValue lit:
                    content = CrmTypeMap.ToVbLiteral(lit);
                    break;
                case FieldValue fld:
                {
                    var varName = $"{stepName}_{++varIndex}";
                    compositeVars.Add(VarObject(varName));
                    compositeActivities.Add(BuildGetEntityProperty(fld.Attribute, primaryEntity, varName, CrmTypeMap.Get(fld.Type).XamlType));
                    content = $"[DirectCast({varName}, {info.VbType})]";
                    break;
                }
                case null:
                    throw new ArgumentException($"Input argument '{arg.Name}' has no value.");
                default:
                    throw new NotSupportedException($"Unsupported argument value '{arg.Value.GetType().Name}'.");
            }

            innerArgs.Add(new XElement(Activities + "InArgument",
                new XAttribute(X + "TypeArguments", info.XamlType),
                new XAttribute(X + "Key", arg.Name),
                content));
        }

        foreach (var arg in step.OutputArguments ?? Array.Empty<ActivityArgument>())
        {
            var info = CrmTypeMap.Get(arg.Type);
            var outVar = arg.OutputVariable ?? $"{stepName}{arg.Name}_localParameter";
            ctx.WorkflowVariables.Add(new XElement(Activities + "Variable",
                new XAttribute(X + "TypeArguments", info.XamlType),
                new XAttribute("Default", "[Nothing]"),
                new XAttribute("Name", outVar)));
            innerArgs.Add(new XElement(Activities + "OutArgument",
                new XAttribute(X + "TypeArguments", info.XamlType),
                new XAttribute(X + "Key", arg.Name),
                $"[{outVar}]"));
        }

        var inner = new XElement(Mxswa + "ActivityReference",
            new XAttribute("AssemblyQualifiedName", step.AssemblyQualifiedName),
            new XAttribute("DisplayName", display),
            new XElement(Mxswa + "ActivityReference.Arguments", innerArgs));

        var varsCollection = new XElement(Sco + "Collection",
            new XAttribute(X + "TypeArguments", "Variable"),
            new XAttribute(X + "Key", "Variables"),
            compositeVars);

        var activitiesCollection = new XElement(Sco + "Collection",
            new XAttribute(X + "TypeArguments", "Activity"),
            new XAttribute(X + "Key", "Activities"));
        activitiesCollection.Add(compositeActivities);
        activitiesCollection.Add(inner);

        return new XElement(Mxswa + "ActivityReference",
            new XAttribute("AssemblyQualifiedName", CompositeAqn),
            new XAttribute("DisplayName", display),
            new XElement(Mxswa + "ActivityReference.Properties",
                varsCollection,
                activitiesCollection));
    }

    /// <summary>CreateCrmType (literal) or GetEntityProperty (field) writing into <paramref name="outVar"/>.</summary>
    private static XElement BuildValueComputation(ArgumentValue value, CrmTypeMap.TypeInfo info, string outVar, string primaryEntity) => value switch
    {
        LiteralValue lit => BuildCreateCrmType(lit, info, outVar),
        FieldValue fld => BuildGetEntityProperty(fld.Attribute, primaryEntity, outVar, CrmTypeMap.Get(fld.Type).XamlType),
        _ => throw new NotSupportedException($"Unsupported argument value '{value.GetType().Name}'.")
    };

    private static XElement BuildCreateCrmType(LiteralValue lit, CrmTypeMap.TypeInfo info, string outVar)
    {
        if (lit.Type == CrmValueType.EntityReference)
            throw new NotSupportedException("EntityReference literals are not supported for entity field assignments; use a field reference instead.");

        var escaped = lit.Value.Replace("\"", "\"\"");
        var parameters = $"[New Object() {{ Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType.{info.WorkflowPropertyType}, \"{escaped}\", \"{info.CrmTypeName}\" }}]";

        return new XElement(Mxswa + "ActivityReference",
            new XAttribute("AssemblyQualifiedName", EvaluateExpressionAqn),
            new XAttribute("DisplayName", "EvaluateExpression"),
            new XElement(Mxswa + "ActivityReference.Arguments",
                ArgIn("x:String", "ExpressionOperator", "CreateCrmType"),
                ArgIn("s:Object[]", "Parameters", parameters),
                new XElement(Activities + "InArgument",
                    new XAttribute(X + "TypeArguments", "s:Type"),
                    new XAttribute(X + "Key", "TargetType"),
                    new XElement(Mxswa + "ReferenceLiteral",
                        new XAttribute(X + "TypeArguments", "s:Type"),
                        new XAttribute("Value", info.XamlType))),
                ArgOut("x:Object", "Result", $"[{outVar}]")));
    }

    private static XElement BuildGetEntityProperty(string attribute, string primaryEntity, string outVar, string targetXamlType) =>
        new(Mxswa + "GetEntityProperty",
            new XAttribute("Attribute", attribute),
            new XAttribute("Entity", InputPrimary),
            new XAttribute("EntityName", primaryEntity),
            new XAttribute("Value", $"[{outVar}]"),
            ReferenceLiteralChild(Mxswa + "GetEntityProperty.TargetType", targetXamlType));

    private static XElement ReferenceLiteralChild(XName propertyElement, string xamlType) =>
        new(propertyElement,
            new XElement(Activities + "InArgument",
                new XAttribute(X + "TypeArguments", "s:Type"),
                new XElement(Mxswa + "ReferenceLiteral",
                    new XAttribute(X + "TypeArguments", "s:Type"),
                    new XAttribute("Value", xamlType))));

    private static XElement VarObject(string name) =>
        new(Activities + "Variable",
            new XAttribute(X + "TypeArguments", "x:Object"),
            new XAttribute("Name", name));

    private static XElement ArgIn(string typeArgs, string key, string content) =>
        new(Activities + "InArgument",
            new XAttribute(X + "TypeArguments", typeArgs),
            new XAttribute(X + "Key", key),
            content);

    private static XElement ArgOut(string typeArgs, string key, string content) =>
        new(Activities + "OutArgument",
            new XAttribute(X + "TypeArguments", typeArgs),
            new XAttribute(X + "Key", key),
            content);

    private sealed class BuildContext
    {
        public List<XElement> WorkflowVariables { get; } = new();
        private int _stepCounter;
        public int NextStep() => ++_stepCounter;
    }
}
