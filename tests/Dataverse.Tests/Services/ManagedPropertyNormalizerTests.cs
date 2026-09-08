namespace Dataverse.Tests.Services;

using System.Text.Json;
using Dataverse.Core.Services;
using NUnit.Framework;

/// <summary>
/// Guards the rewrite of managed properties from a plain value into their object shape.
/// </summary>
/// <remarks>
/// The whole point is that a plain <c>"IsValidForAdvancedFind": false</c> used to reach Dataverse
/// unchanged and come back as a raw OData deserializer complaint. The scope split is the part most
/// likely to be broken by a well-meant refactor: the property is a managed property on a column and
/// a plain boolean on a table, so a single shared list would break table updates.
/// </remarks>
[TestFixture]
public sealed class ManagedPropertyNormalizerTests
{
    private static Dictionary<string, object?> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;

    private static (bool Found, JsonElement Value) Reserialize(
        Dictionary<string, object?> payload,
        string property)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return doc.RootElement.TryGetProperty(property, out var el)
            ? (true, el.Clone())
            : (false, default);
    }

    [Test]
    public void Attribute_WrapsPlainBoolean_IntoBooleanManagedProperty()
    {
        var payload = Parse("""
            {
              "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata",
              "SchemaName": "sample_test",
              "IsValidForAdvancedFind": false
            }
            """);

        var normalized = ManagedPropertyNormalizer.Normalize(payload, ManagedPropertyScope.Attribute);

        Assert.That(normalized, Is.EquivalentTo(new[] { "IsValidForAdvancedFind" }));

        var (found, value) = Reserialize(payload, "IsValidForAdvancedFind");
        Assert.That(found, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(value.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(value.GetProperty("Value").GetBoolean(), Is.False);
            Assert.That(value.GetProperty("CanBeChanged").GetBoolean(), Is.True);

            // The logical name follows "who may change this", not the property name.
            Assert.That(
                value.GetProperty("ManagedPropertyLogicalName").GetString(),
                Is.EqualTo("canmodifysearchsettings"));
        });
    }

    /// <summary>
    /// Property names must survive verbatim. The serializer's camelCase policy applies to POCO
    /// properties, not to dictionary keys — a nested object built as a POCO would arrive as
    /// <c>"value"</c> and be rejected.
    /// </summary>
    [Test]
    public void Attribute_KeepsPascalCasePropertyNames_WhenSerialized()
    {
        var payload = Parse("""{ "IsAuditEnabled": true }""");
        ManagedPropertyNormalizer.Normalize(payload, ManagedPropertyScope.Attribute);

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        Assert.That(json, Does.Contain("\"Value\""));
        Assert.That(json, Does.Not.Contain("\"value\""));
    }

    [TestCase("IsAuditEnabled", "canmodifyauditsettings")]
    [TestCase("IsCustomizable", "iscustomizable")]
    [TestCase("IsRenameable", "isrenameable")]
    [TestCase("IsGlobalFilterEnabled", "canmodifyglobalfiltersettings")]
    [TestCase("IsSortableEnabled", "canmodifyissortablesettings")]
    [TestCase("CanModifyAdditionalSettings", "canmodifyadditionalsettings")]
    public void Attribute_UsesTheDocumentedManagedPropertyLogicalName(string property, string logicalName)
    {
        var payload = Parse($"{{ \"{property}\": true }}");

        ManagedPropertyNormalizer.Normalize(payload, ManagedPropertyScope.Attribute);

        var (_, value) = Reserialize(payload, property);
        Assert.That(value.GetProperty("ManagedPropertyLogicalName").GetString(), Is.EqualTo(logicalName));
    }

    [TestCase("IsValidForGrid")]
    [TestCase("IsValidForForm")]
    [TestCase("IsSecured")]
    [TestCase("CanBeSecuredForRead")]
    [TestCase("CanBeSecuredForCreate")]
    [TestCase("CanBeSecuredForUpdate")]
    [TestCase("IsPrimaryName")]
    public void Attribute_LeavesPlainBooleanProperties_Alone(string property)
    {
        var payload = Parse($"{{ \"{property}\": true }}");

        var normalized = ManagedPropertyNormalizer.Normalize(payload, ManagedPropertyScope.Attribute);

        Assert.That(normalized, Is.Empty);
        var (_, value) = Reserialize(payload, property);
        Assert.That(value.ValueKind, Is.EqualTo(JsonValueKind.True));
    }

    [Test]
    public void Attribute_WrapsRequiredLevel_GivenAsAPlainString()
    {
        var payload = Parse("""{ "RequiredLevel": "ApplicationRequired" }""");

        var normalized = ManagedPropertyNormalizer.Normalize(payload, ManagedPropertyScope.Attribute);

        Assert.That(normalized, Is.EquivalentTo(new[] { "RequiredLevel" }));
        var (_, value) = Reserialize(payload, "RequiredLevel");
        Assert.Multiple(() =>
        {
            Assert.That(value.GetProperty("Value").GetString(), Is.EqualTo("ApplicationRequired"));
            Assert.That(
                value.GetProperty("ManagedPropertyLogicalName").GetString(),
                Is.EqualTo("canmodifyrequirementlevelsettings"));
        });
    }

    [Test]
    public void LeavesAnAlreadyWellFormedManagedProperty_Untouched()
    {
        var payload = Parse("""
            {
              "IsValidForAdvancedFind": {
                "Value": false,
                "CanBeChanged": false,
                "ManagedPropertyLogicalName": "canmodifysearchsettings"
              }
            }
            """);

        var normalized = ManagedPropertyNormalizer.Normalize(payload, ManagedPropertyScope.Attribute);

        Assert.That(normalized, Is.Empty);
        var (_, value) = Reserialize(payload, "IsValidForAdvancedFind");
        Assert.That(value.GetProperty("CanBeChanged").GetBoolean(), Is.False,
            "A caller's own CanBeChanged must not be overwritten.");
    }

    /// <summary>
    /// The trap this class exists to avoid: <c>IsValidForAdvancedFind</c> is a managed property on a
    /// column but a plain <c>Edm.Boolean</c> on a table. Wrapping the table one would break
    /// table_update.
    /// </summary>
    [Test]
    public void Entity_LeavesIsValidForAdvancedFind_AsAPlainBoolean()
    {
        var payload = Parse("""{ "IsValidForAdvancedFind": false }""");

        var normalized = ManagedPropertyNormalizer.Normalize(payload, ManagedPropertyScope.Entity);

        Assert.That(normalized, Is.Empty);
        var (_, value) = Reserialize(payload, "IsValidForAdvancedFind");
        Assert.That(value.ValueKind, Is.EqualTo(JsonValueKind.False));
    }

    [TestCase("IsAuditEnabled")]
    [TestCase("IsCustomizable")]
    [TestCase("IsMappable")]
    [TestCase("IsValidForQueue")]
    [TestCase("IsDuplicateDetectionEnabled")]
    [TestCase("CanCreateForms")]
    [TestCase("CanCreateViews")]
    [TestCase("CanChangeTrackingBeEnabled")]
    public void Entity_WrapsItsOwnManagedProperties(string property)
    {
        var payload = Parse($"{{ \"{property}\": true }}");

        var normalized = ManagedPropertyNormalizer.Normalize(payload, ManagedPropertyScope.Entity);

        Assert.That(normalized, Is.EquivalentTo(new[] { property }));
        var (_, value) = Reserialize(payload, property);
        Assert.Multiple(() =>
        {
            Assert.That(value.GetProperty("Value").GetBoolean(), Is.True);

            // No guessed logical name for tables — the documented write payload does not cover them,
            // and the shape alone is what the deserializer needs.
            Assert.That(value.TryGetProperty("ManagedPropertyLogicalName", out _), Is.False);
        });
    }

    [Test]
    public void Entity_IgnoresRequiredLevel_WhichIsNotAnEntityProperty()
    {
        var payload = Parse("""{ "RequiredLevel": "None" }""");

        var normalized = ManagedPropertyNormalizer.Normalize(payload, ManagedPropertyScope.Entity);

        Assert.That(normalized, Is.Empty);
    }

    [Test]
    public void MatchesPropertyNames_CaseInsensitively()
    {
        var payload = Parse("""{ "isvalidforadvancedfind": true }""");

        var normalized = ManagedPropertyNormalizer.Normalize(payload, ManagedPropertyScope.Attribute);

        Assert.That(normalized, Is.EquivalentTo(new[] { "isvalidforadvancedfind" }));
        var (found, _) = Reserialize(payload, "isvalidforadvancedfind");
        Assert.That(found, Is.True, "The caller's spelling of the key must be preserved.");
    }

    [Test]
    public void HandlesRealBooleans_NotOnlyJsonElements()
    {
        var payload = new Dictionary<string, object?> { ["IsAuditEnabled"] = true };

        var normalized = ManagedPropertyNormalizer.Normalize(payload, ManagedPropertyScope.Attribute);

        Assert.That(normalized, Is.EquivalentTo(new[] { "IsAuditEnabled" }));
    }
}
