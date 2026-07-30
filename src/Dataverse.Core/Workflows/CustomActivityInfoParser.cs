namespace Dataverse.Core.Workflows;

using System.Xml.Linq;
using Dataverse.Core.Models;

/// <summary>
/// Reads the parameter metadata of a Custom Workflow Activity from
/// <c>plugintype.customworkflowactivityinfo</c>.
/// </summary>
/// <remarks>
/// There is no <c>plugintypeattributes</c> table in current Dataverse versions — querying it
/// returns <c>0x80060888 "Resource not found for the segment"</c>. The parameters live in this
/// XML blob instead. It also carries a ready-made <c>AssemblyQualifiedName</c> including the real
/// <c>PublicKeyToken</c>, which must be used verbatim in workflow XAML.
/// See <c>docs/classic-workflows-reference.md</c>.
/// </remarks>
public static class CustomActivityInfoParser
{
    /// <summary>
    /// Maps a parameter type name to the <c>dataType</c> of the definition model.
    /// Returns null for types with no counterpart there.
    /// </summary>
    /// <remarks>
    /// Custom activities declare their parameters with the CRM-side types
    /// (<c>Microsoft.Crm.Sdk.Lookup</c>, <c>CrmBoolean</c>, …), not the .NET types the argument
    /// finally carries. This mapping is what lets the builder emit the right
    /// <c>x:TypeArguments</c> and the validator compare a caller's <c>dataType</c> against the
    /// parameter — see <c>skills/classic-workflows/workflow-tools.md</c>.
    /// </remarks>
    public static string? ToDataType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        // TypeName is assembly-qualified: "System.String, mscorlib, Version=..." — take the type.
        var bare = typeName.Split(',')[0].Trim();

        return bare switch
        {
            // CRM parameter types, as used by msdyncrmWorkflowTools and other code activities.
            "Microsoft.Crm.Sdk.Lookup" or "Microsoft.Crm.Sdk.Owner" or "Microsoft.Crm.Sdk.Customer"
                => "EntityReference",
            "Microsoft.Crm.Sdk.CrmBoolean" => "Boolean",
            "Microsoft.Crm.Sdk.CrmDateTime" => "DateTime",
            "Microsoft.Crm.Sdk.CrmDecimal" => "Decimal",
            "Microsoft.Crm.Sdk.CrmFloat" => "Double",
            "Microsoft.Crm.Sdk.CrmMoney" => "Money",
            "Microsoft.Crm.Sdk.CrmNumber" => "Integer",
            "Microsoft.Crm.Sdk.Picklist" or "Microsoft.Crm.Sdk.Status" or "Microsoft.Crm.Sdk.State"
                => "OptionSetValue",
            "Microsoft.Crm.Sdk.Key" or "Microsoft.Crm.Sdk.UniqueIdentifier" => "Guid",

            // .NET and Xrm types, used by activities written against the modern SDK.
            "System.String" => "String",
            "System.Int32" => "Integer",
            "System.Boolean" => "Boolean",
            "System.Double" => "Double",
            "System.Decimal" => "Decimal",
            "System.DateTime" => "DateTime",
            "System.Guid" => "Guid",
            "Microsoft.Xrm.Sdk.EntityReference" => "EntityReference",
            "Microsoft.Xrm.Sdk.OptionSetValue" => "OptionSetValue",
            "Microsoft.Xrm.Sdk.Money" => "Money",
            _ => null
        };
    }

    /// <summary>
    /// Maps a parameter type name to the XAML type argument used in <c>x:TypeArguments</c>.
    /// Returns null for types that have no well-known XAML primitive.
    /// </summary>
    public static string? ToXamlTypeArgument(string? typeName)
    {
        if (ToDataType(typeName) is { } dataType)
            return WorkflowXamlBuilder.XamlTypeFor(dataType);

        var bare = typeName?.Split(',')[0].Trim();
        return bare switch
        {
            "System.Int64" => "x:Int64",
            "System.Object" => "x:Object",
            "Microsoft.Xrm.Sdk.Entity" => "mxs:Entity",
            _ => null
        };
    }

    /// <summary>
    /// Parses the blob. Returns null when <paramref name="infoXml"/> is empty or not parseable —
    /// callers should then fall back to the plain plugintype columns.
    /// </summary>
    public static CustomActivityInfo? Parse(string? infoXml)
    {
        if (string.IsNullOrWhiteSpace(infoXml))
            return null;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(infoXml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var root = doc.Root;
        if (root is null)
            return null;

        var info = root.Element("CustomActivityInfo");

        var parameters = new List<WorkflowActivityParameter>();
        AddParameters(root.Element("Inputs"), "Input", parameters);
        AddParameters(root.Element("Outputs"), "Output", parameters);

        return new CustomActivityInfo(
            Name: Value(info, "Name"),
            TypeName: Value(info, "TypeName"),
            AssemblyName: Value(info, "AssemblyName"),
            AssemblyVersion: Value(info, "AssemblyVersion"),
            PublicKeyToken: Value(info, "PublicKeyToken"),
            Culture: Value(info, "Culture"),
            GroupName: Value(info, "GroupName"),
            AssemblyQualifiedName: Value(root, "AssemblyQualifiedName"),
            ValidationError: Value(root, "ValidationError"),
            Parameters: parameters);
    }

    private static void AddParameters(XElement? container, string direction, List<WorkflowActivityParameter> into)
    {
        if (container is null)
            return;

        foreach (var p in container.Elements("CustomActivityParameterInfo"))
        {
            var typeName = Value(p, "TypeName") ?? string.Empty;
            var name = Value(p, "Name") ?? string.Empty;

            into.Add(new WorkflowActivityParameter(
                Name: name,
                // Falls back to Name so the caller always has something usable as x:Key.
                DependencyPropertyName: Value(p, "DependencyPropertyName") ?? name,
                TypeName: typeName,
                XamlTypeArgument: ToXamlTypeArgument(typeName),
                Direction: direction,
                IsRequired: string.Equals(Value(p, "Required"), "true", StringComparison.OrdinalIgnoreCase),
                // NOTE: WorkflowAttributeType in this blob is unreliable (observed "Boolean" on
                // string parameters), so it is deliberately not surfaced. TypeName is authoritative.
                Description: null,
                DataType: ToDataType(typeName),
                EntityNames: EntityNames(p)));
        }
    }

    /// <summary>
    /// Target entities a Lookup parameter accepts. Empty for every other parameter type — a lookup
    /// pointing anywhere else is rejected at runtime.
    /// </summary>
    private static IReadOnlyList<string> EntityNames(XElement parameter) =>
        parameter.Element("EntityNames")?.Elements("string")
            .Select(e => e.Value.Trim())
            .Where(v => v.Length > 0)
            .ToList()
        ?? (IReadOnlyList<string>)[];

    private static string? Value(XElement? parent, string name)
    {
        var v = parent?.Element(name)?.Value;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}

public sealed record CustomActivityInfo(
    string? Name,
    string? TypeName,
    string? AssemblyName,
    string? AssemblyVersion,
    string? PublicKeyToken,
    string? Culture,
    string? GroupName,
    string? AssemblyQualifiedName,
    string? ValidationError,
    IReadOnlyList<WorkflowActivityParameter> Parameters);
