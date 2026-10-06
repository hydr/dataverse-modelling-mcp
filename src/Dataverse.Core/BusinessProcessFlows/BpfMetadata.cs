namespace Dataverse.Core.BusinessProcessFlows;

/// <summary>What the builder and validator need to know about one attribute.</summary>
/// <param name="AttributeType">The metadata <c>AttributeType</c>, e.g. "String", "Lookup", "Picklist".</param>
public sealed record BpfFieldInfo(string AttributeType, string? DisplayName);

/// <summary>Attributes of the tables a definition touches, keyed by table and attribute.</summary>
/// <remarks>
/// An <see cref="Empty"/> catalog is valid: the builder then writes text controls and the attribute
/// names as captions, and the validator skips the existence checks. That keeps both usable offline.
/// </remarks>
public sealed class BpfFieldCatalog
{
    private readonly Dictionary<string, Dictionary<string, BpfFieldInfo>?> _tables =
        new(StringComparer.OrdinalIgnoreCase);

    public static BpfFieldCatalog Empty { get; } = new();

    /// <summary>Registers a table; <paramref name="attributes"/> null means the table does not exist.</summary>
    public void Add(string entity, IReadOnlyDictionary<string, BpfFieldInfo>? attributes) =>
        _tables[entity] = attributes is null
            ? null
            : new Dictionary<string, BpfFieldInfo>(attributes, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the table was looked up, whether or not it exists.</summary>
    public bool Knows(string entity) => _tables.ContainsKey(entity);

    /// <summary>True when the table was looked up and does not exist.</summary>
    public bool IsMissing(string entity) => _tables.TryGetValue(entity, out var t) && t is null;

    public BpfFieldInfo? Find(string entity, string attribute) =>
        _tables.TryGetValue(entity, out var table) && table is not null && table.TryGetValue(attribute, out var info)
            ? info
            : null;

    /// <summary>Attributes of a known table, for suggestions.</summary>
    public IEnumerable<string> AttributesOf(string entity) =>
        _tables.TryGetValue(entity, out var table) && table is not null ? table.Keys : [];
}

/// <summary>Form control classes the designer writes into a data step.</summary>
/// <remarks>
/// The class ids are those of the form controls (<c>ClassId</c> in form XML). The designer picks one
/// by attribute type; the process bar renders by attribute type regardless, so a mismatch is cosmetic
/// in the designer but kept right here so a generated process looks like a designed one.
/// </remarks>
public static class BpfControlClass
{
    public const string Text = "4273EDBD-AC1D-40D3-9FB2-095C621B552D";
    public const string Memo = "E0DECE4B-6FC8-4A8F-A065-082708572369";
    public const string Lookup = "270BD3DB-D9AF-4782-9025-509E298DEC0A";
    public const string OptionSet = "3EF39988-22BB-4F0B-BBBE-64B5A3748AEE";
    public const string TwoOptions = "67FAC785-CD58-4F9F-ABB3-4B7DDC6ED5ED";
    public const string Money = "533B9E00-756B-4312-95A0-DC888637AC78";
    public const string DateTime = "5B773807-9FB2-42DB-97C3-7A91EFF8ADFF";
    public const string WholeNumber = "C6D124CA-7EDA-4A60-AEA9-7FB8D318B68F";
    public const string Decimal = "C3EFE0C3-0EC6-42BE-8349-CBD9079DFD8E";
    public const string Float = "0D2C745A-E5A8-4C8F-BA63-C6D3BB604660";
    public const string MultiSelectOptionSet = "4AA28AB7-9C13-4F57-A73D-AD894D048B5F";

    public static string For(string? attributeType) => attributeType switch
    {
        "Memo" => Memo,
        "Lookup" or "Customer" or "Owner" => Lookup,
        "Picklist" or "State" or "Status" => OptionSet,
        "Boolean" => TwoOptions,
        "Money" => Money,
        "DateTime" => DateTime,
        "Integer" or "BigInt" => WholeNumber,
        "Decimal" => Decimal,
        "Double" => Float,
        "Virtual" or "MultiSelectPicklist" => MultiSelectOptionSet,
        _ => Text
    };

    /// <summary>Attribute types a data step cannot show.</summary>
    public static readonly string[] Unsupported =
        ["PartyList", "Uniqueidentifier", "EntityName", "CalendarRules", "ManagedProperty", "File", "Image"];
}

/// <summary><c>processstage.stagecategory</c> by name and number.</summary>
public static class BpfStageCategory
{
    private static readonly (string Name, int Value)[] Known =
    [
        ("Qualify", 0), ("Develop", 1), ("Propose", 2), ("Close", 3),
        ("Identify", 4), ("Research", 5), ("Resolve", 6), ("Approval", 7)
    ];

    public static IEnumerable<string> Names => Known.Select(k => k.Name);

    /// <summary>The number the XAML carries; <c>-1</c> for none.</summary>
    public static int ToNumber(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return -1;

        if (int.TryParse(category, out var number))
            return number;

        var match = Known.FirstOrDefault(k => string.Equals(k.Name, category, StringComparison.OrdinalIgnoreCase));
        return match.Name is null ? -1 : match.Value;
    }

    /// <summary>True when the value is empty, a known name or a known number.</summary>
    public static bool IsValid(string? category) =>
        string.IsNullOrWhiteSpace(category)
        || Known.Any(k => string.Equals(k.Name, category, StringComparison.OrdinalIgnoreCase))
        || (int.TryParse(category, out var n) && (n == -1 || Known.Any(k => k.Value == n)));

    /// <summary>The name for a number read from XAML; null for none.</summary>
    public static string? FromNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var number) || number < 0)
            return null;

        var match = Known.FirstOrDefault(k => k.Value == number);
        return match.Name ?? raw;
    }
}
