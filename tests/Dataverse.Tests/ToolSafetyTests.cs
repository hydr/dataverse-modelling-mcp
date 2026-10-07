using System.Reflection;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Safety;
using Dataverse.Server;
using ModelContextProtocol.Server;
using NUnit.Framework;

namespace Dataverse.Tests;

[TestFixture]
public class ToolSafetyTests
{
    private static IEnumerable<(string Name, CustomAttributeData Attribute)> ToolAttributes() =>
        typeof(ToolSafety).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            .SelectMany(m => m.GetCustomAttributesData())
            .Where(a => a.AttributeType == typeof(McpServerToolAttribute))
            .Select(a => ((string)a.NamedArguments.Single(n => n.MemberName == "Name").TypedValue.Value!, a));

    private static bool Declares(CustomAttributeData attribute, string member) =>
        attribute.NamedArguments.Any(n => n.MemberName == member);

    [Test]
    public void EveryTool_DeclaresReadOnlyExplicitly()
    {
        // Read-only mode and the production guard both trust this flag. A tool without it would
        // count as a write tool in read-only mode — and an unreviewed new tool should be a
        // conscious decision, not a default.
        var missing = ToolAttributes().Where(t => !Declares(t.Attribute, "ReadOnly")).Select(t => t.Name).ToList();
        Assert.That(missing, Is.Empty, "Add ReadOnly = true/false to [McpServerTool] of: " + string.Join(", ", missing));
    }

    [Test]
    public void EveryWriteTool_DeclaresDestructiveExplicitly()
    {
        var missing = ToolAttributes()
            .Where(t => !ToolSafety.IsReadOnlyTool(t.Name) && !Declares(t.Attribute, "Destructive"))
            .Select(t => t.Name)
            .ToList();
        Assert.That(missing, Is.Empty, "Add Destructive = true/false to [McpServerTool] of: " + string.Join(", ", missing));
    }

    [TestCase("table_list", true)]
    [TestCase("solution_export", true)]
    [TestCase("fetchxml_validate", true)]
    [TestCase("table_create", false)]
    [TestCase("column_delete", false)]
    [TestCase("workflow_diagnose_activation", false)]
    [TestCase("flow_trigger_run", false)]
    [TestCase("publish_customizations", false)]
    public void Classification_IsAsExpected(string tool, bool readOnly) =>
        Assert.That(ToolSafety.IsReadOnlyTool(tool), Is.EqualTo(readOnly));

    [Test]
    public void DeleteTools_AreDestructive()
    {
        var notDestructive = ToolAttributes()
            .Where(t => t.Name.Contains("delete") || t.Name.Contains("uninstall") || t.Name.Contains("remove"))
            .Where(t => !(bool)t.Attribute.NamedArguments.Single(n => n.MemberName == "Destructive").TypedValue.Value!)
            .Select(t => t.Name)
            .ToList();
        Assert.That(notDestructive, Is.Empty);
    }

    [Test]
    public void UnknownTool_CountsAsWriteTool() =>
        Assert.That(ToolSafety.IsReadOnlyTool("does_not_exist"), Is.False);

    // --- SafetySettings ---

    [TestCase(false, null, false)]
    [TestCase(true, null, true)]
    [TestCase(false, "true", true)]
    [TestCase(false, "1", true)]
    [TestCase(false, "TRUE ", true)]
    [TestCase(false, "false", false)]
    [TestCase(true, "false", true)] // an env var cannot lift read-only mode set in config.json
    public void ReadOnly_ResolvesFromConfigAndEnvironment(bool inConfig, string? variable, bool expected)
    {
        var settings = SafetySettings.Resolve(
            new DataverseMcpConfig { ReadOnly = inConfig },
            name => name == SafetySettings.ReadOnlyVariable ? variable : null);
        Assert.That(settings.ReadOnly, Is.EqualTo(expected));
    }

    [TestCase(false, null, false)]
    [TestCase(true, null, true)]
    [TestCase(false, "yes", true)]
    [TestCase(false, "${user_config.allow_production_writes}", false)]
    public void AllowProductionWrites_ResolvesFromConfigAndEnvironment(bool inConfig, string? variable, bool expected)
    {
        var settings = SafetySettings.Resolve(
            new DataverseMcpConfig { AllowProductionWrites = inConfig },
            name => name == SafetySettings.AllowProductionWritesVariable ? variable : null);
        Assert.That(settings.AllowProductionWrites, Is.EqualTo(expected));
    }

    // --- EnvironmentTypeService ---

    [TestCase("Customer", true)]
    [TestCase("Secondary", true)]
    [TestCase("Default", true)]
    [TestCase("CustomerTest", false)]
    [TestCase("Developer", false)]
    [TestCase("Trial", false)]
    [TestCase(null, false)]
    public void IsProtected_CoversProductionAndDefault(string? type, bool expected) =>
        Assert.That(EnvironmentTypeService.IsProtected(type), Is.EqualTo(expected));

    [TestCase("""{"Detail":{"OrganizationType":"CustomerTest","UrlName":"contoso"}}""", "CustomerTest")]
    [TestCase("""{"Detail":{"OrganizationType":0}}""", "Customer")]
    [TestCase("""{"Detail":{"OrganizationType":12}}""", "Default")]
    [TestCase("""{"Detail":{"OrganizationType":5}}""", "5")]
    [TestCase("""{"Detail":{}}""", null)]
    [TestCase("""{"error":{"code":"0x80040220"}}""", null)]
    public void ReadOrganizationType_ParsesResponse(string json, string? expected) =>
        Assert.That(EnvironmentTypeService.ReadOrganizationType(JsonDocument.Parse(json).RootElement), Is.EqualTo(expected));
}
