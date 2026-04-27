namespace Dataverse.Tests.Integration;

using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

[TestFixture]
[Category("Integration")]
public sealed class EnvironmentVariableIntegrationTests : IntegrationTestBase
{
    private const string TestSchemaName = "sample_mcptest_apiurl";
    private const string ExpectedDefaultValue = "https://test.example.com";
    private const string IntegrationTestValue = "https://integration-test.example.com";

    [Test]
    public async Task ListEnvVars_ReturnsXvMcptestApiUrl()
    {
        var vars = await EnvironmentVariableService.ListAsync(OrgUrl);

        Assert.That(vars.Any(v => v.SchemaName == TestSchemaName), Is.True,
            $"Expected to find environment variable '{TestSchemaName}' in the list.");
    }

    [Test]
    public async Task GetEnvVar_ReturnsXvMcptestApiUrl()
    {
        var detail = await EnvironmentVariableService.GetAsync(OrgUrl, TestSchemaName);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.SchemaName, Is.EqualTo(TestSchemaName));
        Assert.That(detail.DefaultValue, Is.EqualTo(ExpectedDefaultValue));
    }

    [Test]
    public async Task SetEnvVarValue_ThenVerify()
    {
        await EnvironmentVariableService.SetAsync(OrgUrl, TestSchemaName, IntegrationTestValue);

        var detail = await EnvironmentVariableService.GetAsync(OrgUrl, TestSchemaName);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.CurrentValue, Is.EqualTo(IntegrationTestValue));
    }

    [Test]
    public async Task GetEnvVar_ReturnsNull_ForNonExistentVariable()
    {
        var detail = await EnvironmentVariableService.GetAsync(OrgUrl, "nonexistent_schema_xyz_fake");

        Assert.That(detail, Is.Null);
    }
}
