using Dataverse.Core.Config;
using NUnit.Framework;

namespace Dataverse.Tests;

[TestFixture]
public class ConfigFromEnvironmentTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Name, v => v.Value);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    [Test]
    public void RequiredVariablesSet_BuildsSingleDefaultEnvironment()
    {
        var cfg = ConfigProvider.FromEnvironment(Env(
            ("DATAVERSE_URL", "https://contoso.crm4.dynamics.com/"),
            ("AZURE_CLIENT_ID", "11111111-1111-1111-1111-111111110001")));

        Assert.That(cfg, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(cfg!.Auth!.ClientId, Is.EqualTo("11111111-1111-1111-1111-111111110001"));
            Assert.That(cfg.Auth.TenantId, Is.Null);
            Assert.That(cfg.ActiveEnvironment, Is.EqualTo("default"));
            Assert.That(cfg.Environments!["default"].OrgUrl, Is.EqualTo("https://contoso.crm4.dynamics.com"));
            Assert.That(cfg.Environments["default"].EnvironmentId, Is.Null);
            Assert.That(cfg.Environments["default"].Region, Is.EqualTo("europe"));
        });
    }

    [Test]
    public void OptionalVariablesSet_AreCarriedOver()
    {
        var cfg = ConfigProvider.FromEnvironment(Env(
            ("DATAVERSE_URL", "https://contoso.crm.dynamics.com"),
            ("AZURE_CLIENT_ID", "11111111-1111-1111-1111-111111110001"),
            ("AZURE_TENANT_ID", "11111111-1111-1111-1111-111111110002"),
            ("DATAVERSE_ENVIRONMENT_ID", "11111111-1111-1111-1111-111111110003"),
            ("POWER_PLATFORM_REGION", "unitedstates")));

        Assert.Multiple(() =>
        {
            Assert.That(cfg!.Auth!.TenantId, Is.EqualTo("11111111-1111-1111-1111-111111110002"));
            Assert.That(cfg.Environments!["default"].EnvironmentId, Is.EqualTo("11111111-1111-1111-1111-111111110003"));
            Assert.That(cfg.Environments["default"].Region, Is.EqualTo("unitedstates"));
        });
    }

    [TestCase(null, "11111111-1111-1111-1111-111111110001")]
    [TestCase("https://contoso.crm4.dynamics.com", null)]
    [TestCase("  ", "11111111-1111-1111-1111-111111110001")]
    public void RequiredVariableMissing_ReturnsNull(string? url, string? clientId)
    {
        var vars = new List<(string, string)>();
        if (url is not null) vars.Add(("DATAVERSE_URL", url));
        if (clientId is not null) vars.Add(("AZURE_CLIENT_ID", clientId));

        Assert.That(ConfigProvider.FromEnvironment(Env(vars.ToArray())), Is.Null);
    }

    [Test]
    public void UnexpandedPluginPlaceholders_CountAsUnset()
    {
        var cfg = ConfigProvider.FromEnvironment(Env(
            ("DATAVERSE_URL", "https://contoso.crm4.dynamics.com"),
            ("AZURE_CLIENT_ID", "11111111-1111-1111-1111-111111110001"),
            ("AZURE_TENANT_ID", "${user_config.tenant_id}"),
            ("DATAVERSE_ENVIRONMENT_ID", "${user_config.environment_id}"),
            ("POWER_PLATFORM_REGION", "${user_config.region}")));

        Assert.Multiple(() =>
        {
            Assert.That(cfg!.Auth!.TenantId, Is.Null);
            Assert.That(cfg.Environments!["default"].EnvironmentId, Is.Null);
            Assert.That(cfg.Environments["default"].Region, Is.EqualTo("europe"));
        });
    }

    [Test]
    public void UnexpandedPlaceholderInRequiredVariable_ReturnsNull()
    {
        var cfg = ConfigProvider.FromEnvironment(Env(
            ("DATAVERSE_URL", "${user_config.dataverse_url}"),
            ("AZURE_CLIENT_ID", "11111111-1111-1111-1111-111111110001")));

        Assert.That(cfg, Is.Null);
    }
}
