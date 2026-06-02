namespace Dataverse.Core.Workflows;

using System.Xml.Linq;
using static Dataverse.Core.Workflows.WorkflowXamlNames;

public enum RollupAggregate
{
    Sum,
    Count,
    Avg,
    Max,
    Min
}

public enum RollupNumericType
{
    Integer,
    Decimal,
    Money
}

/// <summary>
/// Defines a "sum/count/… over all child records" rollup. v1 covers the unfiltered case over a
/// single 1:N relationship — the common "total of child amounts / number of children" rollup.
/// </summary>
public sealed record RollupDefinition(
    /// <summary>Logical name of the child entity to aggregate over (e.g. "salesorder").</summary>
    string ChildEntity,
    /// <summary>Schema name of the 1:N relationship from parent to child (e.g. "opportunity_sales_orders").</summary>
    string RelationshipSchemaName,
    /// <summary>Lookup attribute on the child pointing back to the parent (e.g. "opportunityid").</summary>
    string LookupAttribute,
    RollupAggregate Aggregate,
    /// <summary>Numeric attribute on the child to aggregate. Required for Sum/Avg/Max/Min; for Count
    /// it defaults to the child's primary-key attribute (<see cref="ChildPrimaryKey"/>).</summary>
    string? AggregateAttribute = null,
    /// <summary>Child primary-key attribute, used by Count. Defaults to "{ChildEntity}id".</summary>
    string? ChildPrimaryKey = null);

/// <summary>
/// Generates the WF4 <c>FormulaDefinition</c> XAML stored on a rollup attribute (SourceType=2).
/// Mirrors the platform's RollupRule shape (Source / Target / Aggregate sequences) observed on
/// real rollup fields. v1 emits the unfiltered variant (Target = relationship link only).
/// </summary>
public static class RollupFormulaBuilder
{
    public static string Build(RollupDefinition def)
    {
        if (string.IsNullOrWhiteSpace(def.ChildEntity)) throw new ArgumentException("ChildEntity is required.");
        if (string.IsNullOrWhiteSpace(def.RelationshipSchemaName)) throw new ArgumentException("RelationshipSchemaName is required.");
        if (string.IsNullOrWhiteSpace(def.LookupAttribute)) throw new ArgumentException("LookupAttribute is required.");

        var aggregateAttribute = def.Aggregate == RollupAggregate.Count
            ? def.ChildPrimaryKey ?? def.AggregateAttribute ?? $"{def.ChildEntity}id"
            : def.AggregateAttribute ?? throw new ArgumentException("AggregateAttribute is required for Sum/Avg/Max/Min.");

        var tempKey = $"relatedlinked_{def.RelationshipSchemaName}#{def.LookupAttribute}#{def.ChildEntity}#Temp";

        // Source: declares the (unused, non-hierarchical) relationship-name variable.
        var source = new XElement(Activities + "Sequence",
            new XAttribute("DisplayName", "Source"),
            new XElement(Activities + "Sequence.Variables",
                new XElement(Activities + "Variable",
                    new XAttribute(X + "TypeArguments", "x:String"),
                    new XAttribute("Default", "[Nothing]"),
                    new XAttribute("Name", "HierarchicalRelationshipName"))));

        // Target: the relationship link (no filter in v1).
        var target = new XElement(Activities + "Sequence",
            new XAttribute("DisplayName", "Target"),
            new XElement(Mcwc + "SetAttributeValue",
                new XAttribute("DisplayName", $"{def.ChildEntity}.{def.LookupAttribute}.{def.RelationshipSchemaName}"),
                new XAttribute("Entity", $"[CreatedEntities(\"{tempKey}\")]"),
                new XAttribute("EntityName", def.ChildEntity)));

        // Aggregate: read the value from the child, then aggregate.
        var aggregate = new XElement(Activities + "Sequence",
            new XAttribute("DisplayName", "Aggregate"),
            new XElement(Activities + "Sequence.Variables",
                VarObject("RollupRuleStep1_1"),   // result
                VarObject("RollupRuleStep1_2")),  // value read from child
            GetEntityPropertyNullType(aggregateAttribute, def.ChildEntity, "RollupRuleStep1_2"),
            new XElement(Mxswa + "ActivityReference",
                new XAttribute("AssemblyQualifiedName", EvaluateExpressionAqn),
                new XAttribute("DisplayName", "EvaluateExpression"),
                new XElement(Mxswa + "ActivityReference.Arguments",
                    ArgIn("x:String", "ExpressionOperator", def.Aggregate.ToString()),
                    ArgIn("s:Object[]", "Parameters", "[New Object() { RollupRuleStep1_2 }]"),
                    NullTargetTypeArg(),
                    ArgOut("x:Object", "Result", "[RollupRuleStep1_1]"))));

        var rollupRule = new XElement(Activities + "Sequence",
            new XAttribute("DisplayName", "RollupRuleStep1"),
            source, target, aggregate);

        var workflow = new XElement(Mxswa + "Workflow", rollupRule);

        var className = ZeroClassName;
        var root = new XElement(Activities + "Activity",
            new XAttribute(X + "Class", className),
            new XAttribute(XNamespace.Xmlns + "mcwc", McwcUri),
            new XAttribute(XNamespace.Xmlns + "mva", MvaUri),
            new XAttribute(XNamespace.Xmlns + "mxs", MxsUri),
            new XAttribute(XNamespace.Xmlns + "mxswa", MxswaUri),
            new XAttribute(XNamespace.Xmlns + "s", SUri),
            new XAttribute(XNamespace.Xmlns + "scg", ScgUri),
            new XAttribute(XNamespace.Xmlns + "srs", SrsUri),
            new XAttribute(XNamespace.Xmlns + "this", ThisUri),
            new XAttribute(XNamespace.Xmlns + "x", XamlUri),
            new XElement(X + "Members",
                new XElement(X + "Property",
                    new XAttribute("Name", "InputEntities"),
                    new XAttribute("Type", "InArgument(scg:IDictionary(x:String, mxs:Entity))")),
                new XElement(X + "Property",
                    new XAttribute("Name", "CreatedEntities"),
                    new XAttribute("Type", "InArgument(scg:IDictionary(x:String, mxs:Entity))"))),
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

    private static XElement GetEntityPropertyNullType(string attribute, string entityName, string outVar) =>
        new(Mxswa + "GetEntityProperty",
            new XAttribute("Attribute", attribute),
            new XAttribute("Entity", "[InputEntities(\"primaryEntity\")]"),
            new XAttribute("EntityName", entityName),
            new XAttribute("Value", $"[{outVar}]"),
            new XElement(Mxswa + "GetEntityProperty.TargetType",
                new XElement(Activities + "InArgument",
                    new XAttribute(X + "TypeArguments", "s:Type"),
                    new XElement(Mxswa + "ReferenceLiteral",
                        new XAttribute(X + "TypeArguments", "s:Type"),
                        new XElement(X + "Null")))));

    private static XElement NullTargetTypeArg() =>
        new(Activities + "InArgument",
            new XAttribute(X + "TypeArguments", "s:Type"),
            new XAttribute(X + "Key", "TargetType"),
            new XElement(Mxswa + "ReferenceLiteral",
                new XAttribute(X + "TypeArguments", "s:Type"),
                new XElement(X + "Null")));

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
}
