namespace Dataverse.Core.Workflows;

using System.Globalization;

/// <summary>
/// Maps <see cref="CrmValueType"/> to the various type spellings the WF4 XAML needs:
/// the XAML argument type string (<c>x:TypeArguments</c>), the VB.NET type name (for
/// expressions/casts), the <c>WorkflowPropertyType</c> enum member and the legacy
/// "CrmType" name used by the <c>CreateCrmType</c> expression operator.
/// </summary>
public static class CrmTypeMap
{
    public sealed record TypeInfo(
        CrmValueType Type,
        string XamlType,          // e.g. "x:String", "mxs:OptionSetValue", "s:DateTime"
        string VbType,            // e.g. "System.String", "Microsoft.Xrm.Sdk.OptionSetValue"
        string WorkflowPropertyType, // member of Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType
        string CrmTypeName);      // 3rd CreateCrmType param, e.g. "String", "Picklist", "Money"

    private static readonly IReadOnlyDictionary<CrmValueType, TypeInfo> Map = new Dictionary<CrmValueType, TypeInfo>
    {
        [CrmValueType.String] = new(CrmValueType.String, "x:String", "System.String", "String", "String"),
        [CrmValueType.Integer] = new(CrmValueType.Integer, "x:Int32", "System.Int32", "Integer", "Integer"),
        [CrmValueType.Decimal] = new(CrmValueType.Decimal, "x:Decimal", "System.Decimal", "Decimal", "Decimal"),
        [CrmValueType.Double] = new(CrmValueType.Double, "x:Double", "System.Double", "Double", "Double"),
        [CrmValueType.Money] = new(CrmValueType.Money, "mxs:Money", "Microsoft.Xrm.Sdk.Money", "Money", "Money"),
        [CrmValueType.Boolean] = new(CrmValueType.Boolean, "x:Boolean", "System.Boolean", "Boolean", "Boolean"),
        [CrmValueType.OptionSet] = new(CrmValueType.OptionSet, "mxs:OptionSetValue", "Microsoft.Xrm.Sdk.OptionSetValue", "OptionSetValue", "Picklist"),
        [CrmValueType.DateTime] = new(CrmValueType.DateTime, "s:DateTime", "System.DateTime", "DateTime", "DateTime"),
        [CrmValueType.EntityReference] = new(CrmValueType.EntityReference, "mxs:EntityReference", "Microsoft.Xrm.Sdk.EntityReference", "EntityReference", "Lookup"),
        [CrmValueType.Guid] = new(CrmValueType.Guid, "s:Guid", "System.Guid", "String", "Uniqueidentifier"),
    };

    public static TypeInfo Get(CrmValueType type) =>
        Map.TryGetValue(type, out var info) ? info : throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported CRM value type.");

    /// <summary>
    /// Builds a VB.NET literal expression for a constant value, suitable as the body of an
    /// InArgument that feeds a typed Custom Workflow Activity parameter.
    /// </summary>
    public static string ToVbLiteral(LiteralValue literal)
    {
        var value = literal.Value;
        switch (literal.Type)
        {
            case CrmValueType.String:
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            case CrmValueType.Integer:
                return value.Trim();
            case CrmValueType.Decimal:
                return value.Trim() + "D";
            case CrmValueType.Double:
                return value.Trim() + "R";
            case CrmValueType.Boolean:
                return bool.Parse(value) ? "True" : "False";
            case CrmValueType.Money:
                return $"New Microsoft.Xrm.Sdk.Money({value.Trim()}D)";
            case CrmValueType.OptionSet:
                return $"New Microsoft.Xrm.Sdk.OptionSetValue({value.Trim()})";
            case CrmValueType.Guid:
                return $"New System.Guid(\"{value.Trim()}\")";
            case CrmValueType.DateTime:
            {
                var dt = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                return "#" + dt.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture) + "#";
            }
            case CrmValueType.EntityReference:
            {
                if (string.IsNullOrWhiteSpace(literal.EntityReferenceLogicalName))
                    throw new ArgumentException("EntityReference literal requires EntityReferenceLogicalName.");
                return $"New Microsoft.Xrm.Sdk.EntityReference(\"{literal.EntityReferenceLogicalName}\", New System.Guid(\"{value.Trim()}\"))";
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(literal), literal.Type, "Unsupported literal type.");
        }
    }
}
