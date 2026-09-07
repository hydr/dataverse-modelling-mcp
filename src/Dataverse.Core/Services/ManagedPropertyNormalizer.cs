namespace Dataverse.Core.Services;

using System.Text.Json;

/// <summary>
/// Which metadata object a payload describes. Decides which properties are managed properties —
/// the two lists genuinely differ.
/// </summary>
public enum ManagedPropertyScope
{
    /// <summary>An <c>EntityMetadata</c> payload (table create/update).</summary>
    Entity,

    /// <summary>An <c>AttributeMetadata</c> payload (column create/update).</summary>
    Attribute
}

/// <summary>
/// Rewrites managed properties that were written as plain values into the object shape the metadata
/// endpoint expects.
/// </summary>
/// <remarks>
/// A managed property is not a boolean on the wire — it is a <c>BooleanManagedProperty</c> object.
/// Writing <c>"IsValidForAdvancedFind": false</c> therefore fails inside the OData deserializer with
/// <c>0x80048d19 … A 'PrimitiveValue' node with non-null value was found when trying to read the
/// value of the property 'IsValidForAdvancedFind'; however, a 'StartArray' node, a 'StartObject'
/// node, or a 'PrimitiveValue' node with null value was expected</c> — a message that says nothing
/// about managed properties and leaves the caller guessing. The shape is mechanical, so it is fixed
/// here instead of being reported.
/// <para>
/// <b>The lists are per scope and must stay that way.</b>
/// <c>AttributeMetadata.IsValidForAdvancedFind</c> is a <c>BooleanManagedProperty</c>, while
/// <c>EntityMetadata.IsValidForAdvancedFind</c> is a plain <c>Edm.Boolean</c>. One shared list would
/// wrap the entity property and break table updates that work today.
/// </para>
/// <para>
/// Only plain values are rewritten. A caller who already passes the full object — with
/// <c>CanBeChanged</c> or a <c>ManagedPropertyLogicalName</c> of their own — is left untouched.
/// </para>
/// </remarks>
public static class ManagedPropertyNormalizer
{
    /// <summary>
    /// <c>AttributeMetadata</c> properties of type <c>BooleanManagedProperty</c>, mapped to the
    /// <c>ManagedPropertyLogicalName</c> Dataverse reports for them. The logical name follows "who
    /// may change this", not the property name — <c>IsValidForAdvancedFind</c> is
    /// <c>canmodifysearchsettings</c>. Values taken from the documented round-trip payload in
    /// learn.microsoft.com/power-apps/developer/data-platform/webapi/create-update-column-definitions-using-web-api.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AttributeBooleanManagedProperties =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["IsAuditEnabled"] = "canmodifyauditsettings",
            ["IsCustomizable"] = "iscustomizable",
            ["IsGlobalFilterEnabled"] = "canmodifyglobalfiltersettings",
            ["IsRenameable"] = "isrenameable",
            ["IsSortableEnabled"] = "canmodifyissortablesettings",
            ["IsValidForAdvancedFind"] = "canmodifysearchsettings",
            ["CanModifyAdditionalSettings"] = "canmodifyadditionalsettings",
        };

    /// <summary>
    /// <c>EntityMetadata</c> properties of type <c>BooleanManagedProperty</c>
    /// (learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/entitymetadata).
    /// </summary>
    /// <remarks>
    /// No logical names here: unlike the column ones they are not covered by a documented write
    /// payload, and a guessed <c>ManagedPropertyLogicalName</c> is worse than none — the shape alone
    /// is what the deserializer needs.
    /// <para>
    /// <c>IsValidForAdvancedFind</c> is deliberately absent: on a table it is a plain boolean.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlySet<string> EntityBooleanManagedProperties =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CanBeInManyToMany",
            "CanBeInCustomEntityAssociation",
            "CanBePrimaryEntityInRelationship",
            "CanBeRelatedEntityInRelationship",
            "CanChangeHierarchicalRelationship",
            "CanChangeTrackingBeEnabled",
            "CanCreateAttributes",
            "CanCreateCharts",
            "CanCreateForms",
            "CanCreateViews",
            "CanEnableSyncToExternalSearchIndex",
            "CanModifyAdditionalSettings",
            "IsAuditEnabled",
            "IsConnectionsEnabled",
            "IsCustomizable",
            "IsDuplicateDetectionEnabled",
            "IsMailMergeEnabled",
            "IsMappable",
            "IsOfflineInMobileClient",
            "IsReadOnlyInMobileClient",
            "IsRenameable",
            "IsValidForQueue",
            "IsVisibleInMobile",
            "IsVisibleInMobileClient",
        };

    /// <summary>
    /// <c>RequiredLevel</c> is an <c>AttributeRequiredLevelManagedProperty</c>, so a bare
    /// <c>"RequiredLevel": "None"</c> fails the same way a bare boolean does. It is the one managed
    /// property that appears in the documented create examples, and there it always carries this
    /// logical name and <c>CanBeChanged</c>.
    /// </summary>
    private const string RequiredLevelLogicalName = "canmodifyrequirementlevelsettings";

    /// <summary>
    /// Normalize a metadata payload in place. Returns the names of the properties that were
    /// rewritten, so the caller can say what it changed rather than silently editing the request.
    /// </summary>
    public static IReadOnlyList<string> Normalize(
        Dictionary<string, object?> payload,
        ManagedPropertyScope scope)
    {
        var normalized = new List<string>();

        foreach (var key in payload.Keys.ToList())
        {
            var value = payload[key];

            if (scope == ManagedPropertyScope.Attribute
                && key.Equals("RequiredLevel", StringComparison.OrdinalIgnoreCase)
                && TryGetPlainString(value, out var requiredLevel))
            {
                payload[key] = new Dictionary<string, object?>
                {
                    ["Value"] = requiredLevel,
                    ["CanBeChanged"] = true,
                    ["ManagedPropertyLogicalName"] = RequiredLevelLogicalName
                };
                normalized.Add(key);
                continue;
            }

            if (!TryGetPlainBoolean(value, out var flag))
                continue;

            if (scope == ManagedPropertyScope.Attribute)
            {
                if (!AttributeBooleanManagedProperties.TryGetValue(key, out var logicalName))
                    continue;

                payload[key] = new Dictionary<string, object?>
                {
                    ["Value"] = flag,
                    ["CanBeChanged"] = true,
                    ["ManagedPropertyLogicalName"] = logicalName
                };
                normalized.Add(key);
            }
            else
            {
                if (!EntityBooleanManagedProperties.Contains(key))
                    continue;

                payload[key] = new Dictionary<string, object?> { ["Value"] = flag };
                normalized.Add(key);
            }
        }

        return normalized;
    }

    /// <summary>
    /// True when the value is a bare JSON boolean. Values arrive as <see cref="JsonElement"/> after
    /// deserializing the caller's JSON, but a payload built in C# holds real booleans — both count.
    /// </summary>
    private static bool TryGetPlainBoolean(object? value, out bool flag)
    {
        switch (value)
        {
            case bool b:
                flag = b;
                return true;
            case JsonElement { ValueKind: JsonValueKind.True }:
                flag = true;
                return true;
            case JsonElement { ValueKind: JsonValueKind.False }:
                flag = false;
                return true;
            default:
                flag = false;
                return false;
        }
    }

    private static bool TryGetPlainString(object? value, out string? text)
    {
        switch (value)
        {
            case string s:
                text = s;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } el:
                text = el.GetString();
                return true;
            default:
                text = null;
                return false;
        }
    }
}
